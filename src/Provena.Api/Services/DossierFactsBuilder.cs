using Microsoft.EntityFrameworkCore;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;
// Collects the CURRENT case-owned facts and immutable source rows of a case into one DossierFacts (ADR 0008). Read-only and tenant-scoped through the
// global query filters (no IgnoreQueryFilters). Live catalogue rows are read only to compute the advisory drift of the case's master-data copy; no live
// catalogue value ever becomes a dossier fact. Callers needing a coherent view must call it inside a transaction (a locked one, or a repeatable-read snapshot).
public sealed class DossierFactsBuilder(AppDbContext db)
{
    public async Task<DossierFacts> Build(EudrCase c, CancellationToken ct)
    {
        var caseId = c.Id;
        var shipment = await db.Shipments.AsNoTracking().SingleAsync(x => x.Id == c.ShipmentId, ct);
        var masterData = await db.CaseMasterDataVersions.AsNoTracking().Current().SingleOrDefaultAsync(x => x.EudrCaseId == caseId, ct);
        var applicability = await db.CaseApplicabilities.AsNoTracking().Current().SingleOrDefaultAsync(x => x.EudrCaseId == caseId, ct);
        var statement = await db.CaseRecipientStatements.AsNoTracking().Current().SingleOrDefaultAsync(x => x.EudrCaseId == caseId, ct);
        var recipients = statement == null ? [] : await db.CaseRecipients.AsNoTracking().Where(x => x.CaseRecipientStatementId == statement.Id).ToListAsync(ct);

        var dossier = await db.CaseDossiers.AsNoTracking().SingleOrDefaultAsync(x => x.EudrCaseId == caseId, ct);
        var lot = dossier == null ? null : await db.CaseLots.AsNoTracking().Current().SingleOrDefaultAsync(x => x.CaseDossierId == dossier.Id, ct);
        var contributions = new List<LotContributionFacts>();
        if (lot != null)
        {
            var rows = await db.LotContributions.AsNoTracking().Where(x => x.CaseLotId == lot.Id).ToListAsync(ct);
            var rowIds = rows.Select(x => x.Id).ToList();
            var links = await db.ContributionPlots.AsNoTracking().Where(x => rowIds.Contains(x.LotContributionId)).ToListAsync(ct);
            var revisionIds = links.Select(x => x.PlotRevisionId).Distinct().ToList();
            var revisions = revisionIds.Count == 0 ? [] : await db.PlotRevisions.AsNoTracking().Where(x => revisionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            foreach (var row in rows.OrderBy(x => x.ProducerId))
                contributions.Add(new(row, links.Where(l => l.LotContributionId == row.Id).OrderBy(l => l.ProductionPlotId).ThenBy(l => l.PlotRevisionId).Select(l => new PlotLinkFacts(l, revisions[l.PlotRevisionId])).ToList()));
        }

        var documents = await db.Documents.AsNoTracking().Current().Where(x => x.EudrCaseId == caseId).OrderBy(x => x.Id).ToListAsync(ct);
        var geolocation = await db.Geolocations.AsNoTracking().Current().SingleOrDefaultAsync(x => x.EudrCaseId == caseId, ct);
        var submission = await db.Submissions.AsNoTracking().Current().SingleOrDefaultAsync(x => x.EudrCaseId == caseId, ct);
        // Only non-withdrawn links count as current evidence links; whether the linked document is itself current is decided by the evaluator against Documents.
        var evidenceLinks = await db.CaseEvidenceLinks.AsNoTracking().Current().Where(x => x.EudrCaseId == caseId).OrderBy(x => x.Id).ToListAsync(ct);
        var requirements = await db.Requirements.AsNoTracking().Where(x => x.EudrCaseId == caseId).OrderBy(x => x.Type).ToListAsync(ct);

        // Advisory only: the live operator profile and the copy's own supplier and product against the copy.
        var drift = new List<Contracts.MasterDataDriftDto>();
        if (masterData != null)
        {
            var profile = await db.OperatorProfiles.AsNoTracking().SingleOrDefaultAsync(ct);
            var supplier = await db.Suppliers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == masterData.SupplierId, ct);
            var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == masterData.ProductId, ct);
            drift = CaseMasterDataService.Drift(masterData, profile, supplier, product);
        }
        return new(shipment, masterData, applicability, statement, recipients, dossier, lot, contributions, documents, geolocation, submission, evidenceLinks, requirements, drift);
    }
}
