namespace Provena.Api.Domain;

public enum ConcernCategory { NewInformation, SupplierNotification, AuthorityNotification, InternalFinding, Other }
public enum ConcernNotificationChannel { Email, Phone, Letter, Portal, Other }
public enum ConcernResolutionOutcome { CorrectiveActionCompleted, NoFurtherActionRequired }

// Post-completion Concern & Corrective Action v1: a concern discovered AFTER a case was Completed (Closed) or archived, recorded as a new retained layer on top of the
// completed case. It never reopens the case and never touches the case status, review, reviewed snapshot, closure, External DDS record, quantities or fingerprint. The concern, its actions,
// its recipient notifications and its resolution are fully immutable and never deleted (application-level guard; no PUT, PATCH, DELETE or supersede flow in v1). "Open" or "Resolved" is
// derived from the existence of the single ConcernResolution, never stored. Provena sends nothing and claims no compliance, approval, recall completion or regulatory acceptance.
// Number is the per-case sequence (unique per case). The row's CreatedAtUtc is the time it was recorded.
public sealed class CaseConcern : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public int Number { get; set; }
    public ConcernCategory Category { get; set; }
    public string Summary { get; set; } = "";
    public string Details { get; set; } = "";
    public DateTimeOffset RaisedAtUtc { get; set; }
    public Guid RecordedByUserId { get; set; }
}

// A corrective action that was performed. EvidenceDocumentId pins a retained case document that was current when the action was recorded; a later withdrawal keeps the link.
public sealed class ConcernAction : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public Guid ConcernId { get; set; }
    public string Description { get; set; } = "";
    public DateTimeOffset PerformedAtUtc { get; set; }
    public Guid RecordedByUserId { get; set; }
    public Guid? EvidenceDocumentId { get; set; }
}

// A manually recorded notification of a recipient (Provena sends nothing). The recipient identity is the immutable CaseRecipient row: recipient rows are fully immutable and never
// deleted, so the row id always reconstructs exactly who was named, even after the case's recipient statement is superseded. That the recipient belongs to this case is a service rule.
public sealed class ConcernNotification : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public Guid ConcernId { get; set; }
    public Guid CaseRecipientId { get; set; }
    public ConcernNotificationChannel Channel { get; set; }
    public DateTimeOffset NotifiedAtUtc { get; set; }
    public string? Note { get; set; }
    public Guid? EvidenceDocumentId { get; set; }
    public Guid RecordedByUserId { get; set; }
}

// The Owner's resolution of a concern: at most one per concern (unique ConcernId). ResolvedAtUtc comes from the server clock.
public sealed class ConcernResolution : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public Guid ConcernId { get; set; }
    public ConcernResolutionOutcome Outcome { get; set; }
    public string Rationale { get; set; } = "";
    public Guid ResolvedByUserId { get; set; }
    public DateTimeOffset ResolvedAtUtc { get; set; }
}
