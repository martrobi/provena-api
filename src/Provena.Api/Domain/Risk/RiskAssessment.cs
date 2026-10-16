namespace Provena.Api.Domain;

// Risk Assessment & Mitigation v1: a retained decision layer that is separate from completeness, the Owner review, the External DDS record and the case closure.
// It is human judgement recorded against structured prompts; Provena computes no risk and makes no regulatory, legal or compliance claim.
public enum RiskConclusion { Negligible, NotNegligible }
public enum RiskCriterionFinding { NoConcern, Concern, InsufficientInformation }

// The Owner's immutable risk decision on exactly ONE immutable ReviewRecord. Number is the per-case sequence and the only definition of "latest" (never a timestamp); a later assessment
// does not change an earlier one. The review it rests on supplies the "what was assessed" through the existing fingerprint and freshness (there is no second staleness concept).
// ConsideredMitigationThroughNumber is provenance only: it proves that mitigation measures 1..N existed under the case lock when the assessment was recorded (and that the Owner's
// request named measure N as the latest). It does NOT prove relevance, effectiveness, sufficiency or that the Owner relied on any measure. Findings live in RiskAssessmentFinding.
// Fully immutable and never deleted (application-level guard). Not a legal-compliance determination, regulatory approval or DDS statement.
public sealed class RiskAssessment : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public int Number { get; set; }
    public Guid ReviewRecordId { get; set; }
    public string CriteriaCatalogueId { get; set; } = "";
    public string CriteriaCatalogueVersion { get; set; } = "";
    public RiskConclusion Conclusion { get; set; }
    public string Rationale { get; set; } = "";
    public int? ConsideredMitigationThroughNumber { get; set; }
    public Guid AssessedByUserId { get; set; }
    public DateTimeOffset AssessedAtUtc { get; set; }
}

// One human finding per catalogue criterion of one assessment (unique per assessment and code). Immutable and never deleted.
public sealed class RiskAssessmentFinding : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public Guid RiskAssessmentId { get; set; }
    public string CriterionCode { get; set; } = "";
    public RiskCriterionFinding Finding { get; set; }
    public string? Note { get; set; }
}

// A mitigation measure that was actually PERFORMED (a record of something done, never a task: no assignee, due date, priority, reminder or mutable status). It is anchored to the
// assessment that was the latest, NotNegligible one when it was recorded. EvidenceDocumentId pins a retained case document that was current when linked; a later withdrawal keeps the
// link. Number is the per-case sequence. Immutable and never deleted; a correction is a new measure. The row's CreatedAtUtc is the time it was recorded.
public sealed class RiskMitigationMeasure : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public int Number { get; set; }
    public Guid RiskAssessmentId { get; set; }
    public string Description { get; set; } = "";
    public DateTimeOffset PerformedAtUtc { get; set; }
    public Guid? EvidenceDocumentId { get; set; }
    public Guid RecordedByUserId { get; set; }
}
