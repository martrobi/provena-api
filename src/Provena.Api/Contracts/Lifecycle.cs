using Provena.Api.Domain;
namespace Provena.Api.Contracts;

// Case Lifecycle v2 contracts. No type here carries OrganizationId. Requests use nullable members so that a missing required value is a 400 from the validators
// (not a silent default); the bodies are read by the handler after the live Owner check, so a Member never learns whether the body or the case was valid.

// ---- requests ---------------------------------------------------------------------------------------------------------------------------
public sealed record CloseCaseInput(Guid? ExpectedReviewId, string? ExternalDdsReference);
public sealed record ReopenCaseInput(Guid? ExpectedClosureId, string? Reason);
public sealed record ArchiveCaseInput(string? Reason);
public sealed record UnarchiveCaseInput(string? Reason);
public enum ArchivedFilter { Exclude, Only, Include }

// ---- responses --------------------------------------------------------------------------------------------------------------------------
public sealed record CaseReopeningDto(Guid ReopeningId, Guid ClosureId, string Reason, Guid ReopenedByUserId, DateTimeOffset ReopenedAtUtc);
// The closure, the Owner review that backed it (id, revision number, decision), the risk assessment that backed an ExternalDdsCompleted closure (null for legacy closures and the other outcomes) and,
// when it was reopened, the reopening. The outcome is derived from the review decision.
public sealed record CaseClosureDto(Guid ClosureId, int Number, CaseClosureOutcome Outcome, Guid ReviewId, int ReviewRevisionNumber, ReviewDecision ReviewDecision, string? ExternalDdsReference, Guid? RiskAssessmentId,
    Guid ClosedByUserId, DateTimeOffset ClosedAtUtc, CaseReopeningDto? Reopening);
public sealed record CaseArchivalDto(Guid ArchivalId, int Number, string Reason, Guid? ArchivedByUserId, DateTimeOffset ArchivedAtUtc, string? UnarchiveReason, Guid? UnarchivedByUserId, DateTimeOffset? UnarchivedAtUtc);
// Reconstructable lifecycle history: every closure (with its reopening) and every archive period, oldest first. CurrentClosure is set only while the case is Closed.
public sealed record CaseLifecycleDto(EudrCaseStatus Status, bool Archived, CaseClosureDto? CurrentClosure, CaseArchivalDto? CurrentArchival, IReadOnlyList<CaseClosureDto> Closures, IReadOnlyList<CaseArchivalDto> Archivals);

// Mutually exclusive buckets: every case is in exactly one of ActiveOpen, ActiveClosed and Archived (an archived case is counted only under Archived, whatever its workflow status).
public sealed record DashboardOpenByStatusDto(int Draft, int WaitingForSupplier, int ReviewNeeded, int ReadyForExport);
public sealed record DashboardClosedByOutcomeDto(int ExternalDdsCompleted, int OutsideRegulatoryScope, int UnsupportedFlow);
public sealed record DashboardDto(int Total, int ActiveOpen, DashboardOpenByStatusDto ActiveOpenByStatus, int ActiveClosed, DashboardClosedByOutcomeDto ActiveClosedByOutcome, int Archived);
