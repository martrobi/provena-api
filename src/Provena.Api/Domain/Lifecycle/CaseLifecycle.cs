namespace Provena.Api.Domain;

// What a closure records: the outcome is derived from the Owner review that backs it (never chosen freely):
// AcceptedAsCompleteUnderPolicy -> ExternalDdsCompleted, ConfirmedOutsideRegulatoryScope -> OutsideRegulatoryScope, ConfirmedUnsupportedFlow -> UnsupportedFlow.
// ReturnedForCorrection never closes a case. Not a legal-compliance or DDS-submission claim.
public enum CaseClosureOutcome { ExternalDdsCompleted, OutsideRegulatoryScope, UnsupportedFlow }

// One closing of a case (Case Lifecycle v2), backed by exactly one Owner review (ReviewRecord, unique: one review backs at most one closure).
// Number is the per-case sequence. The case is Closed iff its highest-Number closure has no CaseReopening (a service invariant, not SQL).
// ExternalDdsReference belongs to this closure only (for ExternalDdsCompleted a copy of the current ExternalDdsRecord.ReferenceNumber at closing, absent otherwise; unverified). Fully immutable and never deleted
// (application-level guard): a reopen is a separate CaseReopening row, so closure history stays reconstructable.
public sealed class CaseClosure : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public int Number { get; set; }
    public CaseClosureOutcome Outcome { get; set; }
    public Guid ReviewRecordId { get; set; }
    public string? ExternalDdsReference { get; set; }
    // Risk Assessment v1: set only for an ExternalDdsCompleted closure. The composite FK (Org, Case, ReviewRecordId, RiskAssessmentId) proves in the database that the assessment was made
    // on the SAME review that backs this closure. Null for legacy closures and for the other outcomes.
    public Guid? RiskAssessmentId { get; set; }
    public Guid ClosedByUserId { get; set; }
    public DateTimeOffset ClosedAtUtc { get; set; }
}

// The Owner's explicit reopening of exactly one closure (unique CaseClosureId: a closure is reopened at most once). The reason is required.
// Nothing about the closure, its review, its DDS reference or the audit history is changed. Fully immutable and never deleted (application-level guard).
public sealed class CaseReopening : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public Guid CaseClosureId { get; set; }
    public string Reason { get; set; } = "";
    public Guid ReopenedByUserId { get; set; }
    public DateTimeOffset ReopenedAtUtc { get; set; }
}

// One archive period of a case. Archive is independent of the workflow status and never rewrites lifecycle history: archiving and unarchiving change
// only this row. Current = UnarchivedAtUtc IS NULL (at most one per case, partial unique index). The reasons are required and non-blank (API rule).
// ArchivedByUserId is null only for rows created by the CaseLifecycleV2 migration from the legacy Archived status. Retirement-guarded:
// the only allowed update is the one-way unarchive (UnarchivedAtUtc together with UnarchivedByUserId and UnarchiveReason); never deleted.
public sealed class CaseArchival : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public int Number { get; set; }
    public DateTimeOffset ArchivedAtUtc { get; set; }
    public Guid? ArchivedByUserId { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset? UnarchivedAtUtc { get; set; }
    public Guid? UnarchivedByUserId { get; set; }
    public string? UnarchiveReason { get; set; }
}
