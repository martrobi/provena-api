using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Provena.Api.Auth;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;
// Creates the immutable human review of the current working dossier (ADR 0008). Owner-only through a LIVE membership check (never the JWT role, never a cached claim)
// that runs before any case lookup, so a Member learns nothing about a case. The ORDER of the steps in Create is a contract: do not reorder them.
// A review records a decision on exactly the facts the Owner loaded (expectedFingerprint + expectedLatestReviewId). It never changes requirements, the case status or Complete,
// and no decision is a legal-compliance determination. Audit text carries ids and the decision name only.
public sealed class ReviewService(AppDbContext db, TenantContext tenant, CaseService cases, CompletenessService completeness, ReviewFreshnessService freshness, AuditService audit, IValidator<CaseReviewInput> validator)
{
    // The request body is supplied as a reader and examined only at step 5, so a Member (step 2) is denied before the case AND before the body is even parsed.
    public async Task<ReviewDetailDto> Create(Guid caseId, Func<CancellationToken, Task<CaseReviewInput?>> readBody, CancellationToken ct)
    {
        // 1. Transaction.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // 2. Live Owner membership check, before any case lookup (403 whatever the case id or the body).
        await RequireOwner(ct);
        // 3. Tenant-scoped case lock (404 unknown or foreign).
        var c = await cases.Lock(caseId, ct);
        // 4. The case must be mutable (409 Closed or archived).
        await cases.EnsureMutable(c, ct);
        // 5. Body validation (400).
        var input = await readBody(ct) ?? throw new ApiException(400, "A JSON request body is required.");
        var request = input with { Rationale = (input.Rationale ?? "").Trim(), ExpectedFingerprint = (input.ExpectedFingerprint ?? "").Trim().ToLowerInvariant() };
        await validator.ValidateAndThrowAsync(request, ct);
        // 6. Read the current shipment (under the case lock, which shipment edits also take).
        var shipment = await db.Shipments.AsNoTracking().SingleAsync(x => x.Id == c.ShipmentId, ct);
        // 7. Lock the live catalogue sources FOR SHARE in the same order as the master-data refresh (profile, supplier, product): a concurrent catalogue edit waits for
        //    this transaction, or committed first and is visible below as drift.
        await db.OperatorProfiles.FromSqlInterpolated($"SELECT * FROM \"OperatorProfiles\" WHERE \"OrganizationId\" = {tenant.OrganizationId} FOR SHARE").AsNoTracking().SingleOrDefaultAsync(ct);
        await db.Suppliers.FromSqlInterpolated($"SELECT * FROM \"Suppliers\" WHERE \"Id\" = {shipment.SupplierId} AND \"OrganizationId\" = {tenant.OrganizationId} FOR SHARE").AsNoTracking().SingleAsync(ct);
        await db.Products.FromSqlInterpolated($"SELECT * FROM \"Products\" WHERE \"Id\" = {shipment.ProductId} AND \"OrganizationId\" = {tenant.OrganizationId} FOR SHARE").AsNoTracking().SingleAsync(ct);
        // 8-10. Build the facts, evaluate completeness and compute fingerprint, section hashes, drift, shipment match and applicability status.
        var state = await completeness.Evaluate(c, ct);
        // 11. The actual latest review, by CaseRevision.Number.
        var latest = await freshness.Latest(caseId, ct);
        // 12-14. Optimistic concurrency: the Owner reviews exactly the dossier and history they loaded. A null expectedLatestReviewId means "no previous review existed".
        var fingerprintMatches = string.Equals(request.ExpectedFingerprint, state.Fingerprint.Fingerprint, StringComparison.Ordinal);
        var latestMatches = request.ExpectedLatestReviewId == latest?.Record.Id;
        if (!fingerprintMatches || !latestMatches) throw ApiException.Conflict("Dossier or review history changed since it was loaded");
        // 15. Decision preconditions (409 with the failing items).
        if (ReviewPreconditions.Check(request.Decision, state.Evaluation, state.Facts) is { } failure)
            throw new ApiException(409, failure.Reason, new { reason = failure.Reason, failingItems = failure.FailingItems });

        // 16. Next sequence number under the case lock.
        var number = (await db.CaseRevisions.Where(x => x.EudrCaseId == caseId).MaxAsync(x => (int?)x.Number, ct) ?? 0) + 1;
        var producerIds = state.Facts.Contributions.Select(x => x.Contribution.ProducerId).Distinct().ToList();
        var plotIds = state.Facts.Contributions.SelectMany(x => x.Links).Select(x => x.Link.ProductionPlotId).Distinct().ToList();
        var producers = producerIds.Count == 0 ? [] : await db.Producers.AsNoTracking().Where(x => producerIds.Contains(x.Id)).ToListAsync(ct);
        var plots = plotIds.Count == 0 ? [] : await db.ProductionPlots.AsNoTracking().Where(x => plotIds.Contains(x.Id)).ToListAsync(ct);
        var snapshot = ReviewSnapshot.Build(state.Facts, state.Evaluation, producers, plots);

        // 17. Insert the revision with its pins, the document and evidence-link pins and the review record. The lot pinned is the CURRENT lot read above.
        var f = state.Facts; var now = DateTimeOffset.UtcNow; var lotPinned = f.Dossier != null && f.Lot != null;
        var revision = new CaseRevision
        {
            OrganizationId = tenant.OrganizationId, EudrCaseId = caseId, Number = number,
            CaseDossierId = lotPinned ? f.Dossier!.Id : null, CaseLotId = lotPinned ? f.Lot!.Id : null,
            CaseApplicabilityId = f.Applicability?.Id, CaseRecipientStatementId = f.Recipients?.Id, CaseMasterDataVersionId = f.MasterData?.Id,
            GeoSubmissionId = f.Geolocation?.Id, SupplierSubmissionId = f.Submission?.Id,
            PolicyId = CompletenessPolicy.Id, PolicyVersion = CompletenessPolicy.Version, SnapshotSchemaVersion = ReviewSnapshot.SchemaVersion,
            FingerprintVersion = state.Fingerprint.Version, Fingerprint = state.Fingerprint.Fingerprint,
            SectionHashes = ReviewSnapshot.SerializeHashes(state.Fingerprint.SectionHashes), SnapshotJson = ReviewSnapshot.Serialize(snapshot),
        };
        db.CaseRevisions.Add(revision);
        var documentIds = f.Documents.Select(d => d.Id).Order().ToList(); var linkIds = f.EvidenceLinks.Where(l => l.WithdrawnAtUtc == null).Select(l => l.Id).Order().ToList();
        foreach (var id in documentIds) db.CaseRevisionDocuments.Add(new CaseRevisionDocument { OrganizationId = tenant.OrganizationId, EudrCaseId = caseId, CaseRevisionId = revision.Id, CaseDocumentId = id });
        foreach (var id in linkIds) db.CaseRevisionEvidenceLinks.Add(new CaseRevisionEvidenceLink { OrganizationId = tenant.OrganizationId, EudrCaseId = caseId, CaseRevisionId = revision.Id, CaseEvidenceLinkId = id });
        var record = new ReviewRecord
        {
            OrganizationId = tenant.OrganizationId, EudrCaseId = caseId, CaseRevisionId = revision.Id, Decision = request.Decision, Rationale = request.Rationale,
            ReviewerUserId = tenant.UserId!.Value, ReviewedAtUtc = now, PolicyVersion = CompletenessPolicy.Version,
        };
        db.ReviewRecords.Add(record);
        // 18-19. Audit (ids and the decision name only) and the established case-write convention.
        audit.Add(caseId, "CaseReviewRecorded", $"Case review {record.Id} recorded on revision {revision.Id} (number {number}); decision {request.Decision}.");
        c.UpdatedAtUtc = now;
        // 20. Save and commit.
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return ReviewReadService.ToDetail(revision, record, documentIds, linkIds, snapshot);
    }

    // Live check against OrganizationUsers (UserId + OrganizationId + Role Owner). A removed or demoted Owner is denied even with a still-valid token.
    private async Task RequireOwner(CancellationToken ct)
    {
        var userId = tenant.UserId;
        if (userId == null || tenant.OrganizationId == Guid.Empty || !await db.OrganizationUsers.AsNoTracking().AnyAsync(x => x.UserId == userId && x.OrganizationId == tenant.OrganizationId && x.Role == OrganizationRole.Owner, ct))
            throw new ApiException(403, "Only an organization owner can record a review.");
    }
}
