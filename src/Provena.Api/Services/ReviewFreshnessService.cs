using Microsoft.EntityFrameworkCore;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;
// Freshness of the LATEST human review (ADR 0008), derived on read and never stored. The latest review is the one with the highest CaseRevision.Number
// (never a timestamp: ReviewedAtUtc may collide). Current iff the current fingerprint, computed with the review's STORED FingerprintVersion, equals the stored
// one; otherwise Stale with the sections whose hashes differ. A policy-version difference is informational (policyChanged) and never stales a review.
// Catalogue drift, timestamps and UpdatedAtUtc play no part.
public sealed class ReviewFreshnessService(AppDbContext db)
{
    public async Task<(CaseRevision Revision, ReviewRecord Record)?> Latest(Guid caseId, CancellationToken ct)
    {
        var row = await (from r in db.CaseRevisions.AsNoTracking() join rec in db.ReviewRecords.AsNoTracking() on r.Id equals rec.CaseRevisionId
                         where r.EudrCaseId == caseId orderby r.Number descending select new { r, rec }).FirstOrDefaultAsync(ct);
        return row == null ? null : (row.r, row.rec);
    }

    public async Task<(ReviewFreshness Freshness, LatestReviewDto? Latest)> Resolve(Guid caseId, DossierFacts facts, CancellationToken ct)
    {
        var latest = await Latest(caseId, ct);
        return latest == null ? (ReviewFreshness.None, null) : Evaluate(latest.Value.Revision, latest.Value.Record, facts);
    }

    // Pure: compares the stored fingerprint and section hashes with the current facts under the stored fingerprint version.
    public static (ReviewFreshness Freshness, LatestReviewDto Latest) Evaluate(CaseRevision revision, ReviewRecord record, DossierFacts currentFacts)
    {
        var current = DossierFingerprint.Compute(currentFacts, revision.FingerprintVersion);
        var isCurrent = string.Equals(current.Fingerprint, revision.Fingerprint, StringComparison.Ordinal);
        var stored = ReviewSnapshot.DeserializeHashes(revision.SectionHashes);
        var changed = isCurrent ? [] : DossierFingerprint.Sections.Where(s => !stored.TryGetValue(s, out var hash) || !string.Equals(hash, current.SectionHashes[s], StringComparison.Ordinal)).ToList();
        var policyChanged = !string.Equals(CompletenessPolicy.Version, revision.PolicyVersion, StringComparison.Ordinal);
        return (isCurrent ? ReviewFreshness.Current : ReviewFreshness.Stale, new(record.Id, revision.Id, record.Decision, record.ReviewedAtUtc, revision.PolicyVersion, policyChanged, changed));
    }
}
