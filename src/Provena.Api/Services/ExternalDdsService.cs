using System.Data;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Provena.Api.Auth;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;

// External DDS Record v1: records, replaces and reads the manually recorded fact that the Owner submitted the dossier version captured by a review to the EU Information System OUTSIDE
// Provena. Nothing here calls the EU system or claims approval, acceptance, risk clearance or compliance. Record follows the lifecycle contract order: transaction; live Owner check (403,
// before any case lookup and before the body is read); tenant-scoped case lock (404); the case must be mutable (409 closed or archived); body shape (400); then the optimistic check against
// the current record (409), the replacement-reason rule (400), the review rules re-evaluated under the case lock (409) and the receipt document rule (409); then the write.
// A replacement saves the supersession first and the successor second (EF cannot order writes around the filtered unique index). VerificationNumber is returned only by these endpoints and
// never reaches audit text, ProblemDetails or logs; the audit text carries ids only. Foreign or unknown review/document ids answer with the same 409 as a wrong one, so nothing leaks.
public sealed class ExternalDdsService(AppDbContext db, TenantContext tenant, CaseService cases, OwnerGate owner, CompletenessService completeness, ReviewFreshnessService freshness,
    AuditService audit, IValidator<CreateExternalDdsRecordInput> validator)
{
    // ---- reads (any member) -------------------------------------------------------------------------------------------------------------
    public async Task<ExternalDdsRecordDto?> Current(Guid caseId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await RequireCase(caseId, ct);
        var record = await db.ExternalDdsRecords.AsNoTracking().Current().SingleOrDefaultAsync(x => x.EudrCaseId == caseId, ct);
        return record == null ? null : ToDto(record);
    }

    // Every version, newest first (highest Number first).
    public async Task<List<ExternalDdsRecordDto>> History(Guid caseId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await RequireCase(caseId, ct);
        return (await db.ExternalDdsRecords.AsNoTracking().Where(x => x.EudrCaseId == caseId).OrderByDescending(x => x.Number).ToListAsync(ct)).Select(ToDto).ToList();
    }

    // ---- record / replace ---------------------------------------------------------------------------------------------------------------
    public async Task<ExternalDdsRecordDto> Record(Guid caseId, Func<CancellationToken, Task<CreateExternalDdsRecordInput?>> readBody, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await owner.Require("Only an organization owner can record an external DDS submission.", ct);
        var c = await cases.Lock(caseId, ct);
        await cases.EnsureMutable(c, ct);
        var input = await readBody(ct) ?? throw new ApiException(400, "A JSON request body is required.");
        var request = input with
        {
            ReferenceNumber = (input.ReferenceNumber ?? "").Trim(), VerificationNumber = (input.VerificationNumber ?? "").Trim(),
            ReplacementReason = string.IsNullOrWhiteSpace(input.ReplacementReason) ? null : input.ReplacementReason.Trim(),
            SubmittedAtUtc = input.SubmittedAtUtc?.ToUniversalTime(),
        };
        await validator.ValidateAndThrowAsync(request, ct);
        var now = DateTimeOffset.UtcNow;
        if (request.SubmittedAtUtc > now) throw Invalid(nameof(CreateExternalDdsRecordInput.SubmittedAtUtc), "submittedAtUtc cannot be in the future.");

        // Optimistic concurrency under the case lock: the Owner records against exactly the history they loaded (null = no record existed).
        var previous = await db.ExternalDdsRecords.Current().SingleOrDefaultAsync(x => x.EudrCaseId == caseId, ct);
        if (request.ExpectedLatestExternalDdsRecordId != previous?.Id)
            throw new ApiException(409, "External DDS history changed since it was loaded", new { reason = "ExternalDdsHistoryChanged" });
        if (previous != null && request.ReplacementReason == null) throw Invalid(nameof(CreateExternalDdsRecordInput.ReplacementReason), "A replacement requires a replacementReason.");
        if (previous == null && request.ReplacementReason != null) throw Invalid(nameof(CreateExternalDdsRecordInput.ReplacementReason), "replacementReason is only accepted when replacing a record.");

        // The referenced review must be the latest one, accept the dossier as complete and still describe the present dossier (re-evaluated under the case lock).
        var latest = await freshness.Latest(caseId, ct);
        if (latest == null || latest.Value.Record.Id != request.ReviewId)
            throw new ApiException(409, "The review must be the latest Owner review of this case.", new { reason = "ReviewNotLatest" });
        if (latest.Value.Record.Decision != ReviewDecision.AcceptedAsCompleteUnderPolicy)
            throw new ApiException(409, "Only a review that accepted the dossier as complete under the policy can be recorded as submitted externally.", new { reason = "ReviewNotAccepted" });
        // Chronology: the submission cannot predate the immutable review it is recorded against (equality is valid, no tolerance). Checked before anything is written or superseded.
        if (request.SubmittedAtUtc < latest.Value.Record.ReviewedAtUtc.ToUniversalTime())
            throw Invalid(nameof(CreateExternalDdsRecordInput.SubmittedAtUtc), "submittedAtUtc cannot be earlier than the time of the review it refers to.");
        var state = await completeness.Evaluate(c, ct);
        var (reviewFreshness, latestReview) = ReviewFreshnessService.Evaluate(latest.Value.Revision, latest.Value.Record, state.Facts);
        if (reviewFreshness != ReviewFreshness.Current)
            throw new ApiException(409, "The latest review is stale; record a new review of the current dossier before recording the external submission.", new { reason = "StaleReview", changedAreas = latestReview.ChangedAreas });

        // A receipt must be a current (not withdrawn) document of this case; a withdrawal later keeps the historical link.
        if (request.ReceiptDocumentId is { } receipt && !await db.Documents.Current().AnyAsync(x => x.Id == receipt && x.EudrCaseId == caseId, ct))
            throw new ApiException(409, "The receipt document must be a current document of this case.", new { reason = "ReceiptDocumentNotCurrent" });

        if (previous != null)
        {
            previous.SupersededAtUtc = now;
            await db.SaveChangesAsync(ct);
        }
        var number = (await db.ExternalDdsRecords.Where(x => x.EudrCaseId == caseId).MaxAsync(x => (int?)x.Number, ct) ?? 0) + 1;
        var record = new ExternalDdsRecord
        {
            OrganizationId = tenant.OrganizationId, EudrCaseId = caseId, Number = number, ReviewId = latest.Value.Record.Id, ReferenceNumber = request.ReferenceNumber!,
            VerificationNumber = request.VerificationNumber!, SubmittedAtUtc = request.SubmittedAtUtc!.Value, RecordedByUserId = tenant.UserId!.Value, ReceiptDocumentId = request.ReceiptDocumentId,
            SupersedesExternalDdsRecordId = previous?.Id, ReplacementReason = request.ReplacementReason,
        };
        db.ExternalDdsRecords.Add(record);
        c.UpdatedAtUtc = now;
        audit.Add(caseId, previous == null ? "ExternalDdsRecorded" : "ExternalDdsReplaced", previous == null
            ? $"External DDS record {record.Id} (number {number}) recorded for review {record.ReviewId}. This records a submission made outside Provena."
            : $"External DDS record {record.Id} (number {number}) recorded for review {record.ReviewId}, replacing record {previous.Id}. Earlier records are retained.");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return ToDto(record);
    }

    private async Task RequireCase(Guid caseId, CancellationToken ct)
    { if (!await db.Cases.AsNoTracking().AnyAsync(x => x.Id == caseId, ct)) throw ApiException.NotFound(); }

    private static ValidationException Invalid(string property, string message) => new([new FluentValidation.Results.ValidationFailure(property, message)]); // 400, same shape as the validators

    public static ExternalDdsRecordDto ToDto(ExternalDdsRecord r) => new(r.Id, r.Number, r.ReviewId, r.ReferenceNumber, r.VerificationNumber, r.SubmittedAtUtc, r.RecordedByUserId, r.CreatedAtUtc,
        r.ReceiptDocumentId, r.SupersedesExternalDdsRecordId, r.ReplacementReason, r.SupersededAtUtc, r.SupersededAtUtc == null);
    // The close gate's error summary: deliberately without the verification number.
    public static ExternalDdsSummaryDto ToSummary(ExternalDdsRecord r) => new(r.Id, r.ReviewId, r.ReferenceNumber, r.SubmittedAtUtc);
}
