using Provena.Api.Domain;
namespace Provena.Api.Contracts;

// Post-completion Concern & Corrective Action v1 contracts. No type here carries OrganizationId. Requests use nullable members so a missing required value is a 400 from the validators.
// These records are manually recorded facts about a completed or archived case; none of them is a compliance, approval, recall-completion or regulatory-acceptance claim.

// ---- requests ---------------------------------------------------------------------------------------------------------------------------
public sealed record CreateConcernInput(ConcernCategory? Category, string? Summary, string? Details, DateTimeOffset? RaisedAtUtc);
public sealed record RecordConcernActionInput(string? Description, DateTimeOffset? PerformedAtUtc, Guid? EvidenceDocumentId);
// recipientId: a CaseRecipient of this case (from the recipients statement history).
public sealed record RecordConcernNotificationInput(Guid? RecipientId, ConcernNotificationChannel? Channel, DateTimeOffset? NotifiedAtUtc, string? Note, Guid? EvidenceDocumentId);
public sealed record ResolveConcernInput(ConcernResolutionOutcome? Outcome, string? Rationale);

// ---- responses --------------------------------------------------------------------------------------------------------------------------
public enum ConcernStatus { Open, Resolved } // derived: Resolved iff a resolution exists

public sealed record ConcernActionDto(Guid ActionId, string Description, DateTimeOffset PerformedAtUtc, Guid RecordedByUserId, DateTimeOffset RecordedAtUtc, Guid? EvidenceDocumentId);
// The recipient is the immutable recipient row that was named: its id and its identifying fields as stored, so who was notified stays reconstructable.
public sealed record ConcernRecipientDto(Guid RecipientId, string Name, string PostalAddress, string Email);
public sealed record ConcernNotificationDto(Guid NotificationId, ConcernRecipientDto Recipient, ConcernNotificationChannel Channel, DateTimeOffset NotifiedAtUtc, string? Note, Guid? EvidenceDocumentId,
    Guid RecordedByUserId, DateTimeOffset RecordedAtUtc);
public sealed record ConcernResolutionDto(Guid ResolutionId, ConcernResolutionOutcome Outcome, string Rationale, Guid ResolvedByUserId, DateTimeOffset ResolvedAtUtc);

public sealed record ConcernSummaryDto(Guid ConcernId, int Number, ConcernCategory Category, string Summary, DateTimeOffset RaisedAtUtc, ConcernStatus Status, int ActionCount, int NotificationCount,
    DateTimeOffset? ResolvedAtUtc);
public sealed record ConcernDto(Guid ConcernId, int Number, ConcernCategory Category, string Summary, string Details, DateTimeOffset RaisedAtUtc, Guid RecordedByUserId, DateTimeOffset RecordedAtUtc,
    ConcernStatus Status, IReadOnlyList<ConcernActionDto> Actions, IReadOnlyList<ConcernNotificationDto> Notifications, ConcernResolutionDto? Resolution);
