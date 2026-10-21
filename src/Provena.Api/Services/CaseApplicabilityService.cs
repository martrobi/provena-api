using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Provena.Api.Auth;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;
// Versioned, immutable human applicability assessment (ADR 0008). The PUT body is the complete assessment; every material change supersedes the
// current version and inserts a successor. The assessed product identity is copied by the server from the case's current master-data copy, which
// must describe the case's current shipment. Recording an assessment is a working fact only: it never changes requirements or the case status and
// is not a legal determination. Audit text carries identifiers only.
public sealed class CaseApplicabilityService(AppDbContext db, TenantContext tenant, CaseService cases, OwnerGate owner, AuditService audit, IValidator<CaseApplicabilityInput> validator)
{
    private const string RefreshFirst = "Refresh the case master data first.";
    public const string MasterDataRefreshRequiredReason = "MasterDataRefreshRequired";
    public const string HistoryChangedReason = "ApplicabilityHistoryChanged";
    private static ApiException RefreshRequired() => new(409, RefreshFirst, new { reason = MasterDataRefreshRequiredReason });

    // 204 (null) when nothing is recorded; 404 for an unknown or foreign case.
    public async Task<CaseApplicabilityDto?> Current(Guid caseId, CancellationToken ct)
    {
        if (!await db.Cases.AnyAsync(x => x.Id == caseId, ct)) throw ApiException.NotFound();
        var current = await db.CaseApplicabilities.AsNoTracking().Current().SingleOrDefaultAsync(x => x.EudrCaseId == caseId, ct);
        return current == null ? null : await View(caseId, current, ct);
    }

    // Retained versions, newest first. Unpaginated: per-case, pilot scale.
    public async Task<List<CaseApplicabilityVersionDto>> History(Guid caseId, CancellationToken ct)
    {
        if (!await db.Cases.AnyAsync(x => x.Id == caseId, ct)) throw ApiException.NotFound();
        return (await db.CaseApplicabilities.AsNoTracking().Where(x => x.EudrCaseId == caseId).OrderByDescending(x => x.Number).ToListAsync(ct)).Select(x => x.ToDto()).ToList();
    }

    // Owner-only. Order: live Owner check (403, before any case lookup and before the body is read) -> case lock (404) -> editable (409) -> body (400) -> master-data match (409)
    // -> current version and expected-id comparison (409) -> identical no-op -> supersede/insert.
    public async Task<CaseApplicabilityDto> Save(Guid caseId, Func<CancellationToken, Task<CaseApplicabilityInput?>> readBody, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await owner.Require("Only an organization owner can record the applicability determination.", ct);
        var c = await cases.Lock(caseId, ct); await cases.EnsureMutable(c, ct);
        var input = await readBody(ct) ?? throw new ApiException(400, "A JSON request body is required.");
        var normalized = Normalize(input);
        await validator.ValidateAndThrowAsync(normalized, ct);

        // The assessment is only meaningful against a copy of the case's current shipment's supplier and product.
        var copy = await db.CaseMasterDataVersions.AsNoTracking().Current().SingleOrDefaultAsync(x => x.EudrCaseId == caseId, ct) ?? throw RefreshRequired();
        var shipment = await db.Shipments.AsNoTracking().SingleAsync(x => x.Id == c.ShipmentId, ct);
        if (!CaseMasterDataService.ShipmentMatches(copy, shipment)) throw RefreshRequired();

        var current = await db.CaseApplicabilities.Current().SingleOrDefaultAsync(x => x.EudrCaseId == caseId, ct);
        // The Owner replaces exactly the version they loaded (null: none existed). Compared BEFORE the identical-resubmission no-op, so a stale client never gets a silent success.
        if (normalized.ExpectedCurrentApplicabilityId != current?.Id)
            throw new ApiException(409, "Applicability history changed since it was loaded", new { reason = HistoryChangedReason, currentApplicabilityId = current?.Id, currentApplicabilityNumber = current?.Number });
        if (current != null && IsIdentical(normalized, copy, current)) { await tx.CommitAsync(ct); return await View(caseId, current, ct); }

        var now = DateTimeOffset.UtcNow;
        if (current != null)
        {
            // Supersede first and save before inserting the successor: EF cannot order writes around the filtered unique index.
            current.SupersededAtUtc = now;
            await db.SaveChangesAsync(ct);
        }
        var number = (await db.CaseApplicabilities.Where(x => x.EudrCaseId == caseId).MaxAsync(x => (int?)x.Number, ct) ?? 0) + 1;
        var decided = normalized.RegulatoryScope != RegulatoryScope.Pending;
        var saved = new CaseApplicability
        {
            OrganizationId = tenant.OrganizationId, EudrCaseId = caseId, Number = number,
            AssessedProductId = copy.ProductId, AssessedCnCode = copy.ProductCnCode, AssessedCommodity = copy.ProductCommodity,
            ProductDescription = normalized.ProductDescription, SingleRelevantCommodity = normalized.SingleRelevantCommodity, ActivityType = normalized.ActivityType,
            OperatorRole = normalized.OperatorRole, OperatorRoleDescription = normalized.OperatorRoleDescription, ActivityDate = normalized.ActivityDate, ActivityDateKind = normalized.ActivityDateKind,
            ScopeCitation = normalized.ScopeCitation, Qualifications = normalized.Qualifications, LegalBasisVersion = normalized.LegalBasisVersion, EffectiveDate = normalized.EffectiveDate,
            Reasoning = normalized.Reasoning, RegulatoryScope = normalized.RegulatoryScope, FlowSupport = normalized.FlowSupport,
            DecidedByUserId = decided ? tenant.UserId : null, DecidedAtUtc = decided ? now : null, CreatedByUserId = tenant.UserId,
        };
        db.CaseApplicabilities.Add(saved);
        audit.Add(caseId, "CaseApplicabilitySaved", $"Case applicability version {saved.Id} (number {number}) saved" + (current == null ? "." : $"; superseded {current.Id}."));
        c.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return await View(caseId, saved, ct);
    }

    private async Task<CaseApplicabilityDto> View(Guid caseId, CaseApplicability version, CancellationToken ct)
    {
        var copy = await db.CaseMasterDataVersions.AsNoTracking().Current().SingleOrDefaultAsync(x => x.EudrCaseId == caseId, ct);
        return new(ApplicabilityDerivation.Derive(version, copy), version.ToDto());
    }

    // Trims text and turns blank optionals into null; the server-stamped and server-copied facts are not part of the input.
    public static CaseApplicabilityInput Normalize(CaseApplicabilityInput input) => input with
    {
        ProductDescription = Optional(input.ProductDescription), OperatorRoleDescription = Optional(input.OperatorRoleDescription), ScopeCitation = Optional(input.ScopeCitation),
        Qualifications = Optional(input.Qualifications), LegalBasisVersion = Optional(input.LegalBasisVersion), Reasoning = Optional(input.Reasoning),
    };
    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Equality of the normalized factual content with the stored current version, including the server-copied assessed product identity.
    // The decision stamp is not content: an identical resubmission keeps the original decider and time.
    public static bool IsIdentical(CaseApplicabilityInput input, CaseMasterDataVersion copy, CaseApplicability current) =>
        current.AssessedProductId == copy.ProductId && current.AssessedCnCode == copy.ProductCnCode && current.AssessedCommodity == copy.ProductCommodity
        && input.ProductDescription == current.ProductDescription && input.SingleRelevantCommodity == current.SingleRelevantCommodity && input.ActivityType == current.ActivityType
        && input.OperatorRole == current.OperatorRole && input.OperatorRoleDescription == current.OperatorRoleDescription && input.ActivityDate == current.ActivityDate
        && input.ActivityDateKind == current.ActivityDateKind && input.ScopeCitation == current.ScopeCitation && input.Qualifications == current.Qualifications
        && input.LegalBasisVersion == current.LegalBasisVersion && input.EffectiveDate == current.EffectiveDate && input.Reasoning == current.Reasoning
        && input.RegulatoryScope == current.RegulatoryScope && input.FlowSupport == current.FlowSupport;
}
