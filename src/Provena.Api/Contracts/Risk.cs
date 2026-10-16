using Provena.Api.Domain;
namespace Provena.Api.Contracts;

// Risk Assessment & Mitigation v1 contracts. No type here carries OrganizationId. These are human-entered records about a case; none of them is a regulatory approval, legal-compliance
// determination or DDS statement. Requests use nullable members so a missing required value is a 400 from the validators/rules (not a silent default).

// ---- requests ---------------------------------------------------------------------------------------------------------------------------
public sealed record RiskFindingInput(string? CriterionCode, RiskCriterionFinding? Finding, string? Note);
// expectedLatestRiskAssessmentId / expectedLatestMitigationMeasureId: null when none existed, otherwise the id the Owner loaded. criteriaCatalogueVersion: the catalogue version the Owner saw.
public sealed record CreateRiskAssessmentInput(Guid? ReviewId, Guid? ExpectedLatestRiskAssessmentId, Guid? ExpectedLatestMitigationMeasureId, string? CriteriaCatalogueVersion, RiskConclusion? Conclusion,
    string? Rationale, IReadOnlyList<RiskFindingInput>? Findings);
// expectedRiskAssessmentId: the id of the latest (NotNegligible) assessment the measure answers.
public sealed record RecordRiskMitigationInput(Guid? ExpectedRiskAssessmentId, string? Description, DateTimeOffset? PerformedAtUtc, Guid? EvidenceDocumentId);

// ---- responses --------------------------------------------------------------------------------------------------------------------------
public enum RiskFreshness { None, Current, Stale } // derived from the review's own freshness; never stored
public enum RiskStaleReason { ReviewSuperseded, ReviewStale }

public sealed record RiskCriterionDto(string Code, string Title, string Guidance);
public sealed record RiskCatalogueDto(string Id, string Version, IReadOnlyList<RiskCriterionDto> Criteria);
public sealed record RiskFindingDto(string CriterionCode, RiskCriterionFinding Finding, string? Note);
public sealed record RiskAssessmentDto(Guid AssessmentId, int Number, Guid ReviewId, int ReviewRevisionNumber, string CriteriaCatalogueId, string CriteriaCatalogueVersion, RiskConclusion Conclusion,
    string Rationale, IReadOnlyList<RiskFindingDto> Findings, int? ConsideredMitigationThroughNumber, Guid AssessedByUserId, DateTimeOffset AssessedAtUtc);
public sealed record RiskMitigationDto(Guid MitigationId, int Number, Guid RiskAssessmentId, string Description, DateTimeOffset PerformedAtUtc, Guid? EvidenceDocumentId, Guid RecordedByUserId,
    DateTimeOffset RecordedAtUtc);
// Freshness is None (no assessment), Current (the latest assessment rests on the latest review and that review is Current) or Stale (ReviewSuperseded: a newer review exists; ReviewStale: the
// review's facts changed, with the review's own changedAreas). LatestReviewBacksClosure: the latest review already backs a closure (after a reopen a new review is required).
public sealed record RiskOverviewDto(RiskCatalogueDto Catalogue, RiskFreshness Freshness, RiskStaleReason? StaleReason, IReadOnlyList<string> ChangedAreas, Guid? LatestReviewId,
    bool LatestReviewBacksClosure, RiskAssessmentDto? LatestAssessment, IReadOnlyList<RiskAssessmentDto> Assessments, IReadOnlyList<RiskMitigationDto> Mitigations);

// The safe summary a failed close may carry: ids, number and the conclusion only - never the rationale or finding notes.
public sealed record RiskAssessmentSummaryDto(Guid AssessmentId, int Number, Guid ReviewId, RiskConclusion Conclusion);
