using Microsoft.EntityFrameworkCore;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;
// Tenant- and case-scoped reads of the immutable review history (ADR 0008). Details come from the stored SnapshotJson and the pins, never from the live catalogue
// or a recomputation under today's policy. "Latest" and ordering use CaseRevision.Number only.
public sealed class ReviewReadService(AppDbContext db)
{
    // Summaries, newest first. Unpaginated: per-case, pilot scale (like the other per-case history lists).
    public async Task<List<ReviewSummaryDto>> List(Guid caseId, CancellationToken ct)
    {
        if (!await db.Cases.AnyAsync(x => x.Id == caseId, ct)) throw ApiException.NotFound();
        var rows = await (from r in db.CaseRevisions.AsNoTracking() join rec in db.ReviewRecords.AsNoTracking() on r.Id equals rec.CaseRevisionId
                          where r.EudrCaseId == caseId orderby r.Number descending
                          select new ReviewSummaryDto(rec.Id, r.Id, r.Number, rec.Decision, rec.ReviewerUserId, rec.ReviewedAtUtc, r.PolicyId, rec.PolicyVersion, r.Fingerprint, r.FingerprintVersion)).ToListAsync(ct);
        return rows;
    }

    public async Task<ReviewDetailDto> Get(Guid caseId, Guid reviewId, CancellationToken ct)
    {
        var (revision, record) = await Load(caseId, reviewId, ct);
        var documentIds = await db.CaseRevisionDocuments.AsNoTracking().Where(x => x.CaseRevisionId == revision.Id).Select(x => x.CaseDocumentId).OrderBy(x => x).ToListAsync(ct);
        var linkIds = await db.CaseRevisionEvidenceLinks.AsNoTracking().Where(x => x.CaseRevisionId == revision.Id).Select(x => x.CaseEvidenceLinkId).OrderBy(x => x).ToListAsync(ct);
        return ToDetail(revision, record, documentIds, linkIds, ReviewSnapshot.Deserialize(revision.SnapshotJson, revision.SnapshotSchemaVersion));
    }

    // 404 for an unknown or foreign case, an unknown review, or a review that belongs to another case.
    public async Task<(CaseRevision Revision, ReviewRecord Record)> Load(Guid caseId, Guid reviewId, CancellationToken ct)
    {
        if (!await db.Cases.AnyAsync(x => x.Id == caseId, ct)) throw ApiException.NotFound();
        var record = await db.ReviewRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reviewId && x.EudrCaseId == caseId, ct) ?? throw ApiException.NotFound();
        var revision = await db.CaseRevisions.AsNoTracking().SingleAsync(x => x.Id == record.CaseRevisionId, ct);
        return (revision, record);
    }

    public static ReviewDetailDto ToDetail(CaseRevision r, ReviewRecord rec, IReadOnlyList<Guid> documentIds, IReadOnlyList<Guid> linkIds, CaseRevisionSnapshotV1 snapshot) => new(
        rec.Id, r.Id, r.Number, rec.Decision, rec.Rationale, rec.ReviewerUserId, rec.ReviewedAtUtc, r.PolicyId, rec.PolicyVersion, r.SnapshotSchemaVersion, r.FingerprintVersion, r.Fingerprint,
        ReviewSnapshot.DeserializeHashes(r.SectionHashes),
        new(r.CaseDossierId, r.CaseLotId, r.CaseApplicabilityId, r.CaseRecipientStatementId, r.CaseMasterDataVersionId, r.GeoSubmissionId, r.SupplierSubmissionId, documentIds, linkIds), snapshot);
}
