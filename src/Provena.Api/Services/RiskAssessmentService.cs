using System.Data;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Provena.Api.Auth;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;

// Risk Assessment & Mitigation v1: the Owner's immutable risk decision on one immutable Owner review, and the measures actually performed. It is its own decision layer: it never changes
// completeness, the review, the reviewed snapshot, the fingerprint, requirements, quantities, the External DDS record, supplier links or the case status, and it is not a regulatory
// approval, legal-compliance determination or DDS statement. Staleness is NOT a second concept: an assessment is Current exactly while its review is the latest review and that review is
// Current under the existing ReviewFreshnessService (so mitigation evidence that adds a current document stales the review, and the path is mitigation -> new review -> new assessment).
// Both writes follow the lifecycle contract order inside one transaction under the tenant-scoped case lock: assessment = live Owner check (403, before any case lookup and before the body is
// read) -> case lock (404) -> mutable case (409) -> body (400/415) -> validators and rules (400) -> optimistic and state checks (409) -> write. Mitigation has no Owner check. The audit text
// carries ids, numbers, the catalogue version and the conclusion name only - never the rationale, finding notes, mitigation description or document names.
public sealed class RiskAssessmentService(AppDbContext db, TenantContext tenant, CaseService cases, OwnerGate owner, CompletenessService completeness, ReviewFreshnessService freshness,
    AuditService audit, IValidator<CreateRiskAssessmentInput> assessmentValidator, IValidator<RecordRiskMitigationInput> mitigationValidator)
{
    // ---- reads (any member) -------------------------------------------------------------------------------------------------------------
    // One repeatable-read snapshot gives a coherent view of the assessments, the measures and the freshness without taking a lock.
    public async Task<RiskOverviewDto> Overview(Guid caseId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var c = await db.Cases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == caseId, ct) ?? throw ApiException.NotFound();
        return await Build(c, ct);
    }

    // The same read model for a caller that already holds a transaction (a lock or a snapshot): the working export builds it under its case lock, so GET risk and the export can never disagree
    // about freshness. It opens no transaction of its own.
    public async Task<RiskOverviewDto> Build(EudrCase c, CancellationToken ct)
    {
        var caseId = c.Id;
        var assessments = await Assessments(caseId, ct);
        var mitigations = (await db.RiskMitigationMeasures.AsNoTracking().Where(x => x.EudrCaseId == caseId).OrderByDescending(x => x.Number).ToListAsync(ct)).Select(ToDto).ToList();
        var latestReview = await freshness.Latest(caseId, ct);
        var backsClosure = latestReview != null && await db.CaseClosures.AsNoTracking().AnyAsync(x => x.ReviewRecordId == latestReview.Value.Record.Id, ct);
        var latest = assessments.FirstOrDefault();
        var (state, reason, changed) = (RiskFreshness.None, (RiskStaleReason?)null, (IReadOnlyList<string>)[]);
        if (latest != null)
        {
            if (latestReview == null || latest.ReviewId != latestReview.Value.Record.Id) (state, reason) = (RiskFreshness.Stale, RiskStaleReason.ReviewSuperseded);
            else
            {
                var facts = (await completeness.Evaluate(c, ct)).Facts;
                var (reviewFreshness, review) = ReviewFreshnessService.Evaluate(latestReview.Value.Revision, latestReview.Value.Record, facts);
                if (reviewFreshness == ReviewFreshness.Current) state = RiskFreshness.Current;
                else (state, reason, changed) = (RiskFreshness.Stale, RiskStaleReason.ReviewStale, review.ChangedAreas);
            }
        }
        return new(RiskCriteriaCatalogue.Dto(), state, reason, changed, latestReview?.Record.Id, backsClosure, latest, assessments, mitigations);
    }

    public async Task<RiskAssessmentDto> Get(Guid caseId, Guid assessmentId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        if (!await db.Cases.AsNoTracking().AnyAsync(x => x.Id == caseId, ct)) throw ApiException.NotFound();
        return (await Assessments(caseId, ct)).SingleOrDefault(x => x.AssessmentId == assessmentId) ?? throw ApiException.NotFound();
    }

    // ---- assessment (Owner only) --------------------------------------------------------------------------------------------------------
    public async Task<RiskAssessmentDto> Assess(Guid caseId, Func<CancellationToken, Task<CreateRiskAssessmentInput?>> readBody, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await owner.Require("Only an organization owner can record a risk assessment.", ct);
        var c = await cases.Lock(caseId, ct);
        await cases.EnsureMutable(c, ct);
        var input = await readBody(ct) ?? throw new ApiException(400, "A JSON request body is required.");
        var request = input with
        {
            CriteriaCatalogueVersion = (input.CriteriaCatalogueVersion ?? "").Trim(), Rationale = (input.Rationale ?? "").Trim(),
            Findings = input.Findings?.Select(f => f == null ? new RiskFindingInput(null, null, null)
                : f with { CriterionCode = f.CriterionCode?.Trim(), Note = string.IsNullOrWhiteSpace(f.Note) ? null : f.Note.Trim() }).ToList(),
        };
        await assessmentValidator.ValidateAndThrowAsync(request, ct);
        if (RiskAssessmentRules.Check(request.Conclusion!.Value, request.Findings?.Select(f => new RiskFindingEntry(f.CriterionCode, f.Finding, f.Note)).ToList()) is { Count: > 0 } broken)
            throw new ValidationException(broken.Select(e => new FluentValidation.Results.ValidationFailure(e.Property, e.Message)).ToList()); // 400, same shape as the validators

        if (request.CriteriaCatalogueVersion != RiskCriteriaCatalogue.Version)
            throw new ApiException(409, "The risk criteria catalogue changed since it was loaded.", new { reason = "RiskCriteriaCatalogueChanged", currentVersion = RiskCriteriaCatalogue.Version });
        // Optimistic concurrency under the case lock: the Owner assesses against exactly the assessment and mitigation history they loaded (null = none existed).
        var previous = await LatestAssessment(caseId, ct);
        if (request.ExpectedLatestRiskAssessmentId != previous?.Id)
            throw new ApiException(409, "Risk assessment history changed since it was loaded", new { reason = "RiskAssessmentHistoryChanged" });
        var latestMeasure = await db.RiskMitigationMeasures.AsNoTracking().Where(x => x.EudrCaseId == caseId).OrderByDescending(x => x.Number).FirstOrDefaultAsync(ct);
        if (request.ExpectedLatestMitigationMeasureId != latestMeasure?.Id)
            throw new ApiException(409, "Mitigation history changed since it was loaded", new { reason = "MitigationHistoryChanged" });

        // The review must be the latest one (by revision number), must not already back a closure, must accept the dossier as complete and must still describe the present dossier.
        var latest = await freshness.Latest(caseId, ct);
        if (latest == null || latest.Value.Record.Id != request.ReviewId)
            throw new ApiException(409, "The review must be the latest Owner review of this case.", new { reason = "ReviewNotLatest" });
        if (await db.CaseClosures.AnyAsync(x => x.ReviewRecordId == latest.Value.Record.Id, ct))
            throw CaseClosurePolicy.ReviewAlreadyBacksClosure();
        if (latest.Value.Record.Decision != ReviewDecision.AcceptedAsCompleteUnderPolicy)
            throw new ApiException(409, "Only a review that accepted the dossier as complete under the policy can be assessed for risk.", new { reason = "ReviewNotAccepted" });
        var state = await completeness.Evaluate(c, ct);
        var (reviewFreshness, latestReview) = ReviewFreshnessService.Evaluate(latest.Value.Revision, latest.Value.Record, state.Facts);
        if (reviewFreshness != ReviewFreshness.Current)
            throw new ApiException(409, "The latest review is stale; record a new review of the current dossier before recording a risk assessment.", new { reason = "StaleReview", changedAreas = latestReview.ChangedAreas });

        var now = DateTimeOffset.UtcNow; // truncated to the microsecond PostgreSQL stores, so the response equals what is read back
        var assessment = new RiskAssessment
        {
            OrganizationId = tenant.OrganizationId, EudrCaseId = caseId, Number = (previous?.Number ?? 0) + 1, ReviewRecordId = latest.Value.Record.Id,
            CriteriaCatalogueId = RiskCriteriaCatalogue.Id, CriteriaCatalogueVersion = RiskCriteriaCatalogue.Version, Conclusion = request.Conclusion!.Value, Rationale = request.Rationale!,
            ConsideredMitigationThroughNumber = latestMeasure?.Number, AssessedByUserId = tenant.UserId!.Value, AssessedAtUtc = now.AddTicks(-(now.Ticks % 10)),
        };
        db.RiskAssessments.Add(assessment);
        var findings = request.Findings!.Select(f => new RiskAssessmentFinding
        {
            OrganizationId = tenant.OrganizationId, EudrCaseId = caseId, RiskAssessmentId = assessment.Id, CriterionCode = f.CriterionCode!, Finding = f.Finding!.Value, Note = f.Note,
        }).ToList();
        db.RiskAssessmentFindings.AddRange(findings);
        c.UpdatedAtUtc = now;
        audit.Add(caseId, "RiskAssessmentRecorded", $"Risk assessment {assessment.Id} (number {assessment.Number}) recorded on review {assessment.ReviewRecordId}; conclusion {assessment.Conclusion}; criteria catalogue {assessment.CriteriaCatalogueId} version {assessment.CriteriaCatalogueVersion}; considered mitigation through {(assessment.ConsideredMitigationThroughNumber?.ToString() ?? "none")}. This is an internal record, not a regulatory decision.");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return ToDto(assessment, latest.Value.Revision.Number, findings);
    }

    // ---- mitigation (any member) --------------------------------------------------------------------------------------------------------
    public async Task<RiskMitigationDto> Mitigate(Guid caseId, Func<CancellationToken, Task<RecordRiskMitigationInput?>> readBody, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await cases.Lock(caseId, ct);
        await cases.EnsureMutable(c, ct);
        var input = await readBody(ct) ?? throw new ApiException(400, "A JSON request body is required.");
        var request = input with { Description = (input.Description ?? "").Trim(), PerformedAtUtc = input.PerformedAtUtc?.ToUniversalTime() };
        await mitigationValidator.ValidateAndThrowAsync(request, ct);
        var now = DateTimeOffset.UtcNow;
        if (request.PerformedAtUtc > now) throw new ValidationException([new FluentValidation.Results.ValidationFailure(nameof(RecordRiskMitigationInput.PerformedAtUtc), "performedAtUtc cannot be in the future.")]);

        // The measure answers exactly the latest assessment the member loaded, and only a NotNegligible one calls for measures. This is a traceability anchor, not a sufficiency rule.
        var latest = await LatestAssessment(caseId, ct);
        if (latest == null || request.ExpectedRiskAssessmentId != latest.Id)
            throw new ApiException(409, "The risk assessment changed since it was loaded", new { reason = "RiskAssessmentChanged" });
        if (latest.Conclusion != RiskConclusion.NotNegligible)
            throw new ApiException(409, "The latest risk assessment is Negligible; no mitigation measure is expected for it.", new { reason = "MitigationNotExpected" });
        // Evidence must be a current (not withdrawn) document of this case when it is linked; a later withdrawal keeps the historical link.
        if (request.EvidenceDocumentId is { } id && !await db.Documents.Current().AnyAsync(x => x.Id == id && x.EudrCaseId == caseId, ct))
            throw new ApiException(409, "The evidence document must be a current document of this case.", new { reason = "EvidenceDocumentNotCurrent" });

        var number = (await db.RiskMitigationMeasures.Where(x => x.EudrCaseId == caseId).MaxAsync(x => (int?)x.Number, ct) ?? 0) + 1;
        var measure = new RiskMitigationMeasure
        {
            OrganizationId = tenant.OrganizationId, EudrCaseId = caseId, Number = number, RiskAssessmentId = latest.Id, Description = request.Description!, PerformedAtUtc = request.PerformedAtUtc!.Value,
            EvidenceDocumentId = request.EvidenceDocumentId, RecordedByUserId = tenant.UserId!.Value,
        };
        db.RiskMitigationMeasures.Add(measure);
        c.UpdatedAtUtc = now;
        audit.Add(caseId, "RiskMitigationRecorded", $"Mitigation measure {measure.Id} (number {number}) recorded for risk assessment {latest.Id} (number {latest.Number}).");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return ToDto(measure);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------------------------
    private Task<RiskAssessment?> LatestAssessment(Guid caseId, CancellationToken ct)
        => db.RiskAssessments.AsNoTracking().Where(x => x.EudrCaseId == caseId).OrderByDescending(x => x.Number).FirstOrDefaultAsync(ct);

    // Newest first (highest Number first); the review's revision number is joined so a client can show which review each assessment rests on.
    private async Task<List<RiskAssessmentDto>> Assessments(Guid caseId, CancellationToken ct)
    {
        var rows = await (from a in db.RiskAssessments.AsNoTracking() join r in db.ReviewRecords.AsNoTracking() on a.ReviewRecordId equals r.Id
                          join v in db.CaseRevisions.AsNoTracking() on r.CaseRevisionId equals v.Id where a.EudrCaseId == caseId orderby a.Number descending
                          select new { Assessment = a, RevisionNumber = v.Number }).ToListAsync(ct);
        var findings = (await db.RiskAssessmentFindings.AsNoTracking().Where(x => x.EudrCaseId == caseId).ToListAsync(ct)).ToLookup(x => x.RiskAssessmentId);
        return rows.Select(x => ToDto(x.Assessment, x.RevisionNumber, findings[x.Assessment.Id])).ToList();
    }

    public static RiskAssessmentDto ToDto(RiskAssessment a, int revisionNumber, IEnumerable<RiskAssessmentFinding> findings)
        => new(a.Id, a.Number, a.ReviewRecordId, revisionNumber, a.CriteriaCatalogueId, a.CriteriaCatalogueVersion, a.Conclusion, a.Rationale,
            findings.OrderBy(f => f.CriterionCode, StringComparer.Ordinal).Select(f => new RiskFindingDto(f.CriterionCode, f.Finding, f.Note)).ToList(), a.ConsideredMitigationThroughNumber, a.AssessedByUserId, a.AssessedAtUtc);
    public static RiskMitigationDto ToDto(RiskMitigationMeasure m) => new(m.Id, m.Number, m.RiskAssessmentId, m.Description, m.PerformedAtUtc, m.EvidenceDocumentId, m.RecordedByUserId, m.CreatedAtUtc);
    // The close gate's error summary: deliberately without the rationale or finding notes.
    public static RiskAssessmentSummaryDto ToSummary(RiskAssessment a) => new(a.Id, a.Number, a.ReviewRecordId, a.Conclusion);
}
