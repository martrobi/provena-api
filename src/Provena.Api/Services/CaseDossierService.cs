using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Provena.Api.Auth;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;
// Operator-maintained case dossier: factual lot, producer contributions and pinned plot revisions (ADR 0007). The PUT body is the complete lot;
// every material change supersedes the current CaseLot and inserts a successor, so history rows are never edited. It records facts only: no
// completeness, sufficiency or review decision, and it never touches requirements or the case status. Audit text carries identifiers only.
public sealed class CaseDossierService(AppDbContext db, TenantContext tenant, CaseService cases, AuditService audit, IValidator<CaseLotInput> validator)
{
    public const string HistoryChangedReason = "LotHistoryChanged";
    private const string ProducerNotFound = "Producer was not found in your organization.";
    private const string PlotNotFound = "Production plot was not found in your organization.";
    private const string RevisionNotFound = "Plot revision was not found for the production plot in your organization.";

    // 204 (null) when the case has no dossier yet; 404 for an unknown or foreign case.
    public async Task<CaseDossierDto?> Dossier(Guid caseId, CancellationToken ct)
    {
        var c = await db.Cases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == caseId, ct) ?? throw ApiException.NotFound();
        var dossier = await db.CaseDossiers.AsNoTracking().SingleOrDefaultAsync(x => x.EudrCaseId == caseId, ct);
        if (dossier == null) return null;
        var lot = await db.CaseLots.AsNoTracking().Current().SingleAsync(x => x.CaseDossierId == dossier.Id, ct);
        var shipment = await db.Shipments.AsNoTracking().SingleAsync(x => x.Id == c.ShipmentId, ct);
        var current = (await Lots([lot], ct)).Single();
        return new(dossier.Id, caseId, dossier.CreatedAtUtc, shipment.Quantity, shipment.Unit, current, ProductionCoverage.FromLot(current));
    }

    // Retained lot versions, newest first. Unpaginated: per-case, pilot scale.
    public async Task<List<CaseLotDto>> History(Guid caseId, CancellationToken ct)
    {
        if (!await db.Cases.AnyAsync(x => x.Id == caseId, ct)) throw ApiException.NotFound();
        var dossierId = await db.CaseDossiers.Where(x => x.EudrCaseId == caseId).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        if (dossierId == null) return [];
        return await Lots(await db.CaseLots.AsNoTracking().Where(x => x.CaseDossierId == dossierId).OrderByDescending(x => x.Number).ToListAsync(ct), ct);
    }

    public async Task<CaseDossierDto> SaveLot(Guid caseId, CaseLotInput input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // The route case is resolved and locked (404) and must be editable (409) before the body is examined (400).
        var c = await cases.Lock(caseId, ct); await cases.EnsureMutable(c, ct);
        var lot = Normalize(input);
        await validator.ValidateAndThrowAsync(lot, ct);

        var dossier = await db.CaseDossiers.SingleOrDefaultAsync(x => x.EudrCaseId == caseId, ct);
        var current = dossier == null ? null : await db.CaseLots.Current().SingleOrDefaultAsync(x => x.CaseDossierId == dossier.Id, ct);
        // The caller replaces exactly the version they loaded (null: none existed). Compared under the case lock and BEFORE the identical-resubmission no-op, so a stale client never gets a silent success.
        if (lot.ExpectedCurrentLotId != current?.Id)
            throw new ApiException(409, "Lot history changed since it was loaded", new { reason = HistoryChangedReason, currentLotId = current?.Id, currentLotNumber = current?.Number });
        var currentContributions = new List<LotContribution>(); var currentLinks = new List<ContributionPlot>();
        if (current != null)
        {
            currentContributions = await db.LotContributions.AsNoTracking().Where(x => x.CaseLotId == current.Id).ToListAsync(ct);
            var contributionIds = currentContributions.Select(x => x.Id).ToList();
            currentLinks = await db.ContributionPlots.AsNoTracking().Where(x => contributionIds.Contains(x.LotContributionId)).ToListAsync(ct);
            if (IsIdentical(lot, current, currentContributions, currentLinks)) { await tx.CommitAsync(ct); return (await Dossier(caseId, ct))!; }
        }

        // LotReference is a versioned reference: only other CURRENT lots in the organization conflict. The partial unique index is the race backstop (23505 -> 409).
        var ownDossierId = dossier?.Id ?? Guid.Empty;
        if (await db.CaseLots.Current().AnyAsync(x => x.LotReference == lot.LotReference && x.CaseDossierId != ownDossierId, ct))
            throw ApiException.Conflict("Lot reference is already used by another case in your organization.");

        // Lock order: case, then producers, then plots (each by id), FOR SHARE: a concurrent deactivation (an UPDATE of the same row) waits for this
        // transaction, or committed before the locks were taken and is seen below. Revisions are immutable and need no active-state lock.
        var producerIds = lot.Contributions.Select(x => x.ProducerId).Distinct().Order().ToArray();
        var plotIds = lot.Contributions.SelectMany(x => x.Plots).Select(x => x.ProductionPlotId).Distinct().Order().ToArray();
        var producers = (await LockProducers(producerIds, ct)).ToDictionary(x => x.Id);
        if (producers.Count != producerIds.Length) throw new ApiException(400, ProducerNotFound);
        var plots = (await LockPlots(plotIds, ct)).ToDictionary(x => x.Id);
        if (plots.Count != plotIds.Length) throw new ApiException(400, PlotNotFound);
        var revisionIds = lot.Contributions.SelectMany(x => x.Plots).Select(x => x.PlotRevisionId).Distinct().ToList();
        var revisions = revisionIds.Count == 0 ? [] : await db.PlotRevisions.AsNoTracking().Where(x => revisionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);

        foreach (var contribution in lot.Contributions)
        foreach (var link in contribution.Plots)
        {
            if (plots[link.ProductionPlotId].ProducerId != contribution.ProducerId) throw new ApiException(400, "Production plot does not belong to the contribution's producer.");
            // The exact submitted revision is the historical pin; it must belong to the submitted plot. "Current" is never substituted.
            if (!revisions.TryGetValue(link.PlotRevisionId, out var revision) || revision.ProductionPlotId != link.ProductionPlotId) throw new ApiException(400, RevisionNotFound);
        }

        // New attribution needs an active producer and plot; sources carried over from the current version stay usable after deactivation.
        var carriedProducers = currentContributions.Select(x => x.ProducerId).ToHashSet();
        var carriedLinks = currentLinks.Select(x => (x.ProducerId, x.ProductionPlotId, x.PlotRevisionId)).ToHashSet();
        foreach (var contribution in lot.Contributions)
        {
            if (!carriedProducers.Contains(contribution.ProducerId) && !producers[contribution.ProducerId].IsActive)
                throw ApiException.Conflict("Producer is inactive; reactivate it before attributing new lot sources.");
            foreach (var link in contribution.Plots)
            {
                if (carriedLinks.Contains((contribution.ProducerId, link.ProductionPlotId, link.PlotRevisionId))) continue;
                if (!producers[contribution.ProducerId].IsActive) throw ApiException.Conflict("Producer is inactive; reactivate it before attributing new lot sources.");
                if (!plots[link.ProductionPlotId].IsActive) throw ApiException.Conflict("Production plot is inactive; reactivate it before attributing new lot sources.");
            }
        }

        Reconcile(lot.Quantity, lot.Contributions.Select(x => x.Quantity)); // 400 on over-allocation

        var now = DateTimeOffset.UtcNow;
        if (dossier == null) { dossier = new CaseDossier { OrganizationId = tenant.OrganizationId, EudrCaseId = caseId, CreatedByUserId = tenant.UserId }; db.CaseDossiers.Add(dossier); }
        if (current != null)
        {
            // Supersede first and save before inserting the successor: EF cannot order writes around the filtered unique indexes.
            current.SupersededAtUtc = now;
            await db.SaveChangesAsync(ct);
        }
        var number = (await db.CaseLots.Where(x => x.CaseDossierId == dossier.Id).MaxAsync(x => (int?)x.Number, ct) ?? 0) + 1;
        var saved = new CaseLot { OrganizationId = tenant.OrganizationId, CaseDossierId = dossier.Id, Number = number, LotReference = lot.LotReference, Quantity = lot.Quantity, Unit = lot.Unit, Note = lot.Note, CreatedByUserId = tenant.UserId };
        db.CaseLots.Add(saved);
        foreach (var contribution in lot.Contributions)
        {
            var row = new LotContribution { OrganizationId = tenant.OrganizationId, CaseLotId = saved.Id, ProducerId = contribution.ProducerId, Quantity = contribution.Quantity, Unit = contribution.Unit };
            db.LotContributions.Add(row);
            foreach (var link in contribution.Plots)
                db.ContributionPlots.Add(new ContributionPlot { OrganizationId = tenant.OrganizationId, LotContributionId = row.Id, ProducerId = contribution.ProducerId, ProductionPlotId = link.ProductionPlotId, PlotRevisionId = link.PlotRevisionId, ProductionStartDate = link.ProductionStartDate, ProductionEndDate = link.ProductionEndDate });
        }
        audit.Add(caseId, "CaseLotSaved", $"Case lot version {saved.Id} (number {number}) saved" + (current == null ? "." : $"; superseded {current.Id}."));
        c.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return (await Dossier(caseId, ct))!;
    }

    // Trims the reference and turns a blank note into null; tolerates null members so validation (not a NullReferenceException) reports them.
    public static CaseLotInput Normalize(CaseLotInput input) => input with
    {
        LotReference = (input.LotReference ?? "").Trim(),
        Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note,
    };

    // Derived on read, never stored. Exact decimal equality; no tolerance, no conversion. Over-allocation is rejected (400).
    public static QuantityReconciliationDto Reconcile(decimal lotQuantity, IEnumerable<decimal> contributionQuantities)
    {
        var allocated = contributionQuantities.Sum();
        if (allocated > lotQuantity) throw new ApiException(400, "Contribution quantities exceed the lot quantity.");
        return new(allocated, lotQuantity - allocated, allocated == lotQuantity ? QuantityReconciliationStatus.Reconciled : QuantityReconciliationStatus.UnderAllocated);
    }

    // Order-independent equality of a normalized input and the stored current version: contributions are a set keyed by producer, links a set keyed
    // by (plot, revision) within a contribution. Quantities compare by decimal value (1.5 == 1.500000).
    public static bool IsIdentical(CaseLotInput input, CaseLot current, IReadOnlyCollection<LotContribution> contributions, IReadOnlyCollection<ContributionPlot> links)
    {
        if (input.LotReference != current.LotReference || input.Quantity != current.Quantity || input.Unit != current.Unit || input.Note != current.Note) return false;
        if (input.Contributions.Count != contributions.Count) return false;
        var stored = contributions.ToDictionary(x => x.ProducerId);
        foreach (var contribution in input.Contributions)
        {
            if (!stored.TryGetValue(contribution.ProducerId, out var row) || row.Quantity != contribution.Quantity || row.Unit != contribution.Unit) return false;
            var rowLinks = links.Where(x => x.LotContributionId == row.Id).ToList();
            if (rowLinks.Count != contribution.Plots.Count) return false;
            foreach (var link in contribution.Plots)
            {
                var match = rowLinks.SingleOrDefault(x => x.ProductionPlotId == link.ProductionPlotId && x.PlotRevisionId == link.PlotRevisionId);
                if (match == null || match.ProductionStartDate != link.ProductionStartDate || match.ProductionEndDate != link.ProductionEndDate) return false;
            }
        }
        return true;
    }

    private Task<List<Producer>> LockProducers(Guid[] ids, CancellationToken ct) => ids.Length == 0 ? Task.FromResult(new List<Producer>()) : db.Producers
        .FromSqlInterpolated($"SELECT * FROM \"Producers\" WHERE \"Id\" = ANY({ids}) AND \"OrganizationId\" = {tenant.OrganizationId} ORDER BY \"Id\" FOR SHARE")
        .AsNoTracking().ToListAsync(ct);
    private Task<List<ProductionPlot>> LockPlots(Guid[] ids, CancellationToken ct) => ids.Length == 0 ? Task.FromResult(new List<ProductionPlot>()) : db.ProductionPlots
        .FromSqlInterpolated($"SELECT * FROM \"ProductionPlots\" WHERE \"Id\" = ANY({ids}) AND \"OrganizationId\" = {tenant.OrganizationId} ORDER BY \"Id\" FOR SHARE")
        .AsNoTracking().ToListAsync(ct);

    // Names and active flags are live master data (not snapshotted); plot and revision metadata come from the pinned rows. Geometry is never included.
    private async Task<List<CaseLotDto>> Lots(List<CaseLot> lots, CancellationToken ct)
    {
        var lotIds = lots.Select(x => x.Id).ToList();
        var contributions = await db.LotContributions.AsNoTracking().Where(x => lotIds.Contains(x.CaseLotId)).ToListAsync(ct);
        var contributionIds = contributions.Select(x => x.Id).ToList();
        var links = await db.ContributionPlots.AsNoTracking().Where(x => contributionIds.Contains(x.LotContributionId)).ToListAsync(ct);
        var producerIds = contributions.Select(x => x.ProducerId).Distinct().ToList();
        var plotIds = links.Select(x => x.ProductionPlotId).Distinct().ToList();
        var revisionIds = links.Select(x => x.PlotRevisionId).Distinct().ToList();
        var producers = await db.Producers.AsNoTracking().Where(x => producerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var plots = await db.ProductionPlots.AsNoTracking().Where(x => plotIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var revisions = await db.PlotRevisions.AsNoTracking().Where(x => revisionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        return lots.Select(lot =>
        {
            var rows = contributions.Where(x => x.CaseLotId == lot.Id).ToList();
            var dtos = rows.Select(row => new LotContributionDto(row.Id, row.ProducerId, producers[row.ProducerId].Name, producers[row.ProducerId].IsActive, row.Quantity, row.Unit,
                    links.Where(l => l.LotContributionId == row.Id).Select(l => new ContributionPlotDto(l.Id, l.ProductionPlotId, plots[l.ProductionPlotId].Name, plots[l.ProductionPlotId].IsActive, l.PlotRevisionId,
                        revisions[l.PlotRevisionId].Number, revisions[l.PlotRevisionId].CountryCode, revisions[l.PlotRevisionId].GeometryType, revisions[l.PlotRevisionId].AreaHectares, revisions[l.PlotRevisionId].SupersededAtUtc,
                        l.ProductionStartDate, l.ProductionEndDate))
                        .OrderBy(p => p.PlotName, StringComparer.Ordinal).ThenBy(p => p.ProductionPlotId).ToList()))
                .OrderBy(x => x.ProducerName, StringComparer.Ordinal).ThenBy(x => x.ProducerId).ToList();
            return new CaseLotDto(lot.Id, lot.Number, lot.LotReference, lot.Quantity, lot.Unit, lot.Note, Reconcile(lot.Quantity, rows.Select(r => r.Quantity)), dtos, lot.CreatedByUserId, lot.CreatedAtUtc, lot.SupersededAtUtc);
        }).ToList();
    }
}
