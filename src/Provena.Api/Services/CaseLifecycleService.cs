using System.Data;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Provena.Api.Auth;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;

// Case Lifecycle v2: close, reopen, archive and unarchive. Every write is Owner-only through the shared OwnerGate (a LIVE membership check) and follows one fixed order, which is a
// contract: begin the transaction; live Owner check (403, before any case lookup and before the request body is read, so a Member learns nothing about a case); the tenant-scoped case
// lock FOR UPDATE (404); the state preconditions (409); body shape (400); then the optimistic checks and the write. The case lock serializes these writes with every other case write
// (public supplier writes, lots, reviews, ...), so a close either sees a dossier change as Stale or the change sees the case Closed.
// A closure is backed by exactly one Owner review that is the LATEST review (highest CaseRevision.Number), is Current for the present dossier (fingerprint freshness under the review's
// own fingerprint version) and has not backed a closure before. An ExternalDdsCompleted closure is additionally backed by a Negligible risk assessment of that same review (Risk Assessment v1).
// ReadyForExport is not a gate, and nothing here is a legal-compliance, release or DDS-submission decision.
// Closing and archiving revoke active supplier links; reopening and unarchiving never restore them. Closure and reopening rows are immutable; an archive period is retired one way.
public sealed class CaseLifecycleService(AppDbContext db, TenantContext tenant, CaseService cases, OwnerGate owner, CompletenessService completeness, ReviewFreshnessService freshness,
    EudrCaseStatusService statuses, CaseLifecycleReadService reads, AuditService audit,
    IValidator<CloseCaseInput> closeValidator, IValidator<ReopenCaseInput> reopenValidator, IValidator<ArchiveCaseInput> archiveValidator, IValidator<UnarchiveCaseInput> unarchiveValidator)
{
    // ---- read ---------------------------------------------------------------------------------------------------------------------------
    // A repeatable-read snapshot gives one coherent view of the closures, reopenings and archive periods without taking a lock.
    public async Task<CaseLifecycleDto> Get(Guid caseId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        return await reads.Get(caseId, ct);
    }

    // ---- close --------------------------------------------------------------------------------------------------------------------------
    public async Task<CaseClosureDto> Close(Guid caseId, Func<CancellationToken, Task<CloseCaseInput?>> readBody, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await owner.Require("Only an organization owner can close a case.", ct);
        var c = await cases.Lock(caseId, ct);
        await cases.EnsureNotArchived(c, ct);
        if (c.Status == EudrCaseStatus.Closed) throw ApiException.Conflict("The case is already closed.");
        var input = await readBody(ct) ?? throw new ApiException(400, "A JSON request body is required.");
        var request = input with { ExternalDdsReference = CaseClosurePolicy.NormalizeReference(input.ExternalDdsReference) };
        await closeValidator.ValidateAndThrowAsync(request, ct);

        // The Owner closes exactly the review they loaded: the latest one by revision number (never by timestamp).
        var latest = await freshness.Latest(caseId, ct) ?? throw ApiException.Conflict("No Owner review exists for this case; record a review before closing it.");
        if (request.ExpectedReviewId != latest.Record.Id) throw ApiException.Conflict("Review history changed since it was loaded");
        // One review backs at most one closure, even after a reopen: a reopened case needs a new review of what it is now.
        if (await db.CaseClosures.AnyAsync(x => x.ReviewRecordId == latest.Record.Id, ct)) throw CaseClosurePolicy.ReviewAlreadyBacksClosure();
        var outcome = CaseClosurePolicy.Outcome(latest.Record.Decision) ?? throw ApiException.Conflict("A review that returned the dossier for correction cannot close the case.");
        if (CaseClosurePolicy.ReferenceError(outcome, request.ExternalDdsReference) is { } referenceError)
            throw new ValidationException([new FluentValidation.Results.ValidationFailure(nameof(CloseCaseInput.ExternalDdsReference), referenceError)]); // 400, same shape as the validators
        // Freshness: the review must still describe the present dossier. Built under the case lock, so there is no torn view; the live catalogue is not part of the fingerprint.
        var state = await completeness.Evaluate(c, ct);
        var (reviewFreshness, latestReview) = ReviewFreshnessService.Evaluate(latest.Revision, latest.Record, state.Facts);
        if (reviewFreshness != ReviewFreshness.Current)
            throw new ApiException(409, "The latest review is stale; record a new review of the current dossier before closing the case.", new { reason = "StaleReview", changedAreas = latestReview.ChangedAreas });
        // Quantity Reconciliation v1: the CURRENT reconciliation (same facts, same lock) must be Matched for ExternalDdsCompleted, also for a Current review made under policy v1.
        if (CaseClosurePolicy.QuantityError(outcome, state.Evaluation.QuantityReconciliation) is { } quantityError)
            throw new ApiException(409, quantityError, new { reason = "QuantityNotReconciled", quantityReconciliation = state.Evaluation.QuantityReconciliation });

        // Risk Assessment v1: the LATEST risk assessment must rest on exactly the latest review and be Negligible. Evaluated after the quantity gate and before the External DDS gate, under the
        // same case lock. No timestamp is compared with the External DDS record. The closure pins this exact assessment (the composite FK proves it was made on the closure's own review).
        var riskAssessment = outcome == CaseClosureOutcome.ExternalDdsCompleted
            ? await db.RiskAssessments.AsNoTracking().Where(x => x.EudrCaseId == caseId).OrderByDescending(x => x.Number).FirstOrDefaultAsync(ct) : null;
        if (CaseClosurePolicy.RiskError(outcome, latest.Record.Id, riskAssessment) is { } riskError)
            throw new ApiException(409, riskError.Message, new { reason = riskError.Reason, riskAssessment = riskAssessment == null ? null : RiskAssessmentService.ToSummary(riskAssessment) });

        // External DDS Record v1: the structured current record (not the request's free text) is authoritative for ExternalDdsCompleted. Read under the same case lock.
        var ddsRecord = outcome == CaseClosureOutcome.ExternalDdsCompleted ? await db.ExternalDdsRecords.AsNoTracking().Current().SingleOrDefaultAsync(x => x.EudrCaseId == caseId, ct) : null;
        if (CaseClosurePolicy.ExternalDdsError(outcome, latest.Record.Id, ddsRecord, request.ExternalDdsReference) is { } ddsError)
            throw new ApiException(409, ddsError.Message, new { reason = ddsError.Reason, externalDds = ddsRecord == null ? null : ExternalDdsService.ToSummary(ddsRecord) });

        var now = DateTimeOffset.UtcNow; var previous = c.Status;
        var number = (await db.CaseClosures.Where(x => x.EudrCaseId == caseId).MaxAsync(x => (int?)x.Number, ct) ?? 0) + 1;
        var closure = new CaseClosure
        {
            OrganizationId = tenant.OrganizationId, EudrCaseId = caseId, Number = number, Outcome = outcome, ReviewRecordId = latest.Record.Id,
            ExternalDdsReference = ddsRecord?.ReferenceNumber, RiskAssessmentId = riskAssessment?.Id, ClosedByUserId = tenant.UserId!.Value, ClosedAtUtc = now,
        };
        db.CaseClosures.Add(closure);
        var revoked = await RevokeActiveRequests(caseId, now, ct);
        c.Status = EudrCaseStatus.Closed; c.UpdatedAtUtc = now;
        audit.Add(caseId, "CaseClosed", $"Case closed (closure {closure.Id}, number {number}) as {outcome} on review {latest.Record.Id}{(riskAssessment == null ? "" : $", risk assessment {riskAssessment.Id}")}{(ddsRecord == null ? "" : $" and external DDS record {ddsRecord.Id}")}; status was {previous}; {revoked} active supplier request(s) revoked. This is not a DDS submission.");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return CaseLifecycleReadService.ToDto(closure, latest.Record.Decision, latest.Revision.Number, null);
    }

    // ---- reopen -------------------------------------------------------------------------------------------------------------------------
    public async Task<CaseReopeningDto> Reopen(Guid caseId, Func<CancellationToken, Task<ReopenCaseInput?>> readBody, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await owner.Require("Only an organization owner can reopen a case.", ct);
        var c = await cases.Lock(caseId, ct);
        await cases.EnsureNotArchived(c, ct);
        if (c.Status != EudrCaseStatus.Closed) throw ApiException.Conflict("The case is not closed.");
        var input = await readBody(ct) ?? throw new ApiException(400, "A JSON request body is required.");
        var request = input with { Reason = (input.Reason ?? "").Trim() };
        await reopenValidator.ValidateAndThrowAsync(request, ct);

        // The current closure is the highest-Number one; the Owner reopens exactly the closure they loaded.
        var current = await db.CaseClosures.Where(x => x.EudrCaseId == caseId).OrderByDescending(x => x.Number).FirstOrDefaultAsync(ct);
        if (current == null || await db.CaseReopenings.AnyAsync(x => x.CaseClosureId == current.Id, ct)) throw ApiException.Conflict("The case has no open closure to reopen.");
        if (request.ExpectedClosureId != current.Id) throw ApiException.Conflict("Closure history changed since it was loaded");

        var now = DateTimeOffset.UtcNow;
        var reopening = new CaseReopening { OrganizationId = tenant.OrganizationId, EudrCaseId = caseId, CaseClosureId = current.Id, Reason = request.Reason!, ReopenedByUserId = tenant.UserId!.Value, ReopenedAtUtc = now };
        db.CaseReopenings.Add(reopening);
        var next = await statuses.RecalculateAfterReopen(c, ct);
        // Supplier links are deliberately not restored: a member regenerates one if the supplier is still needed.
        audit.Add(caseId, "CaseReopened", $"Case reopened (reopening {reopening.Id}) from closure {current.Id}; status is now {next}. Previous closure, review and audit history are retained.");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return CaseLifecycleReadService.ToDto(reopening);
    }

    // ---- archive / unarchive ------------------------------------------------------------------------------------------------------------
    public async Task<CaseArchivalDto> Archive(Guid caseId, Func<CancellationToken, Task<ArchiveCaseInput?>> readBody, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await owner.Require("Only an organization owner can archive a case.", ct);
        var c = await cases.Lock(caseId, ct);
        if (await db.CaseArchivals.Current().AnyAsync(x => x.EudrCaseId == caseId, ct)) throw ApiException.Conflict("The case is already archived.");
        var input = await readBody(ct) ?? throw new ApiException(400, "A JSON request body is required.");
        var request = input with { Reason = (input.Reason ?? "").Trim() };
        await archiveValidator.ValidateAndThrowAsync(request, ct);

        var now = DateTimeOffset.UtcNow;
        var number = (await db.CaseArchivals.Where(x => x.EudrCaseId == caseId).MaxAsync(x => (int?)x.Number, ct) ?? 0) + 1;
        var archival = new CaseArchival { OrganizationId = tenant.OrganizationId, EudrCaseId = caseId, Number = number, ArchivedAtUtc = now, ArchivedByUserId = tenant.UserId!.Value, Reason = request.Reason! };
        db.CaseArchivals.Add(archival);
        var revoked = await RevokeActiveRequests(caseId, now, ct);
        c.UpdatedAtUtc = now; // The workflow status and all lifecycle history are untouched: archive is an independent dimension.
        audit.Add(caseId, "CaseArchived", $"Case archived (period {archival.Id}, number {number}); status stays {c.Status}; {revoked} active supplier request(s) revoked; evidence retained.");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return CaseLifecycleReadService.ToDto(archival);
    }

    public async Task<CaseArchivalDto> Unarchive(Guid caseId, Func<CancellationToken, Task<UnarchiveCaseInput?>> readBody, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await owner.Require("Only an organization owner can unarchive a case.", ct);
        var c = await cases.Lock(caseId, ct);
        var archival = await db.CaseArchivals.Current().SingleOrDefaultAsync(x => x.EudrCaseId == caseId, ct) ?? throw ApiException.Conflict("The case is not archived.");
        var input = await readBody(ct) ?? throw new ApiException(400, "A JSON request body is required.");
        var request = input with { Reason = (input.Reason ?? "").Trim() };
        await unarchiveValidator.ValidateAndThrowAsync(request, ct);

        var now = DateTimeOffset.UtcNow;
        archival.UnarchivedAtUtc = now; archival.UnarchivedByUserId = tenant.UserId!.Value; archival.UnarchiveReason = request.Reason; // the one-way retirement
        c.UpdatedAtUtc = now; // The workflow status is exactly what it was; revoked supplier links stay revoked.
        audit.Add(caseId, "CaseUnarchived", $"Case unarchived (period {archival.Id}, number {archival.Number}); status is {c.Status}.");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return CaseLifecycleReadService.ToDto(archival);
    }

    // Revoking is the same one-way marker the revoke route sets; it needs no case status check and never restores anything later.
    private async Task<int> RevokeActiveRequests(Guid caseId, DateTimeOffset now, CancellationToken ct)
    {
        var active = await db.SupplierRequests.Where(x => x.EudrCaseId == caseId && x.RevokedAtUtc == null).ToListAsync(ct);
        foreach (var r in active) r.RevokedAtUtc = now;
        return active.Count;
    }
}
