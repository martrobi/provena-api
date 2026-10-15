# ADR 0012: Post-completion Concern & Corrective Action v1 (a retained layer above immutable case history)

- Date: 2026-10-16
- Decision status: Accepted
- Implementation status: **Implemented** (backend only; the separate client has not adapted). Post-completion Concern & Corrective Action v1 was introduced by backend implementation commit `fcd1c39` (`feat(api): add post-completion concerns`), directly on top of `f671a63`. This documentation slice follows it; the frontend handoff is [2026-10-post-completion-concerns](../frontend-handoffs/2026-10-post-completion-concerns.md).
- Decision owner / approval evidence: the owner stated the rules in the Post-completion Concern & Corrective Action v1 implementation request and its one follow-up correction (remove `EscalatedExternally` as a resolution outcome before the first commit). The plan artifact itself is not retained in the repository.
- Supersedes / superseded by: supersedes nothing. It adds a layer next to [ADR 0009](0009-case-lifecycle-v2.md) (case lifecycle) and [ADR 0011](0011-external-dds-record-v1.md) (External DDS record) and answers the "post-closure corrective action" revisit trigger of ADR 0011 only for the single-case, manual scope below. It changes none of their rules.

## Context and evidence

Before this decision (observed at `f671a63`):

- A `Closed` case and, independently, an archived case are read-only: every case-scoped business write goes through `CaseService.EnsureMutable` (closed or archived -> 409). Closure, reopening, review and External DDS rows are immutable retained history ([ADR 0008](0008-completeness-facts-and-reviewed-history.md), [ADR 0009](0009-case-lifecycle-v2.md), [ADR 0011](0011-external-dds-record-v1.md)).
- The only way to record anything new about a completed case was an Owner reopening it with a reason (ADR 0009). Reopening makes the case editable again and requires a new review to close, which is the wrong tool for "we learned something after we finished": it changes the live status of a case whose historical decision is still true as of its date.
- There was no place to record a concern discovered after completion, what was done about it, who was told, or how it ended. See [EUDR_GAP_ANALYSIS](../EUDR_GAP_ANALYSIS.md) G23.

## Decision

**What it is.** A *concern* is a manually recorded note that something relevant to an already Completed (`Closed`) or archived case was found out afterwards, plus the corrective actions taken, the recipients notified and the Owner's recorded disposition. It is a **new retained layer on top of the completed case**. Provena sends nothing, contacts no one and calls no external system. **It is not a regulatory approval, a compliance statement, a recall, a recall completion, an authority acceptance or an EU acceptance.** `NoFurtherActionRequired` is the Owner's recorded disposition inside Provena, not an external legal determination.

**Key invariant: a concern never mutates historical truth.** Creating, extending or resolving a concern does not change the `CaseStatus`, any `CaseClosure`, any `ReviewRecord`, the reviewed snapshot, `ReviewFreshness`, the completeness fingerprint, any `ExternalDdsRecord`, the quantity reconciliation, supplier-link state (close and archive revoked the links; nothing restores them) or the case row (`UpdatedAtUtc` included). A concern is therefore never a reopening: no `CaseReopening` is written and no status transition happens. The Owner-only reopen of ADR 0009 is a separate action that this decision neither uses nor replaces; a concern record is independent of it and stays readable whatever later happens to the case.

**Why Completed/Archived cases are not reopened.** The completed case is the retained, reviewed account of what was known and decided at the time. Reopening to attach a later finding would make that account editable again, force a new review to close, and blur "what we decided then" with "what we found later". A concern keeps the two apart: the case history stays exactly as it was, and the later finding has its own append-only history with its own timestamps and actors.

**Entities** (tenant-owned, `Domain/Concerns/CaseConcern.cs`; all fully immutable and never deleted; no PUT, PATCH, DELETE or supersede flow in v1):

- `CaseConcern`: `Id`, `OrganizationId`, `EudrCaseId`, `Number` (per-case sequence from 1, unique per case), `Category`, `Summary` (<= 200, required, non-blank), `Details` (<= 4000, required, non-blank), `RaisedAtUtc` (required, not in the future, no tolerance), `RecordedByUserId`, `CreatedAtUtc` (shown as `recordedAtUtc`).
- `ConcernAction`: `ConcernId`, `Description` (<= 4000, required, non-blank), `PerformedAtUtc` (not in the future), `RecordedByUserId`, optional `EvidenceDocumentId`. A record that something was done, nothing more: no assignee, due date, priority or mutable status.
- `ConcernNotification`: `ConcernId`, `CaseRecipientId`, `Channel`, `NotifiedAtUtc` (not in the future), optional `Note` (<= 1000, non-blank when present), optional `EvidenceDocumentId`, `RecordedByUserId`. A record that a recipient was notified outside Provena.
- `ConcernResolution`: `ConcernId` (unique: at most one per concern), `Outcome`, `Rationale` (<= 4000, required, non-blank), `ResolvedByUserId`, `ResolvedAtUtc` (set by the server clock; a client value is ignored, truncated to the microsecond PostgreSQL stores).

**Enums** (names as serialized; integer storage follows declaration order):

- `ConcernCategory`: `NewInformation`, `SupplierNotification`, `AuthorityNotification`, `InternalFinding`, `Other`.
- `ConcernNotificationChannel`: `Email`, `Phone`, `Letter`, `Portal`, `Other`.
- `ConcernResolutionOutcome`: `CorrectiveActionCompleted`, `NoFurtherActionRequired`. **Only these two.** An external escalation does not by itself end a concern, because follow-up may still be required; when it happens it is recorded as a `ConcernAction` and/or a `ConcernNotification`, and the concern stays open. (An earlier draft had a third outcome for external escalation; it was removed before the first commit and is not a v1 value.)

**Derived status, no mutable field.** `Open` = no `ConcernResolution` exists for the concern; `Resolved` = one exists. There is no status column and no transition machinery. Resolution is terminal for that concern.

**Case-state rule.** A concern can be **created only** when the case is Completed (`Status == Closed`) or archived (a current `CaseArchival`); otherwise 409 `CaseNotCompleted`. Creating it changes nothing about the case. Once a concern exists, actions and notifications are governed by **the concern's resolution state, not by re-checking the case status**: an open concern accepts them whatever the case state later becomes, and a resolved concern accepts none. Reads of the concern history are always allowed.

**Authorization.** Any authenticated member of the tenant may read concerns and create a concern, record an action and record a notification. **Only an Owner may resolve**, through the shared `OwnerGate` (live `OrganizationUsers` check before any case lookup or body read; a Member gets 403 whatever the case id, concern id or body, and the JWT role is not trusted). No new role exists. Tenant boundaries follow the rest of the API: a foreign case or concern is 404, and no request or response carries `organizationId`.

**Locking and concurrency.** Every write runs in one transaction under the existing tenant-scoped case lock (`CaseService.Lock`, `FOR UPDATE`). The resolved check is made **under that lock**, immediately before the insert. Two simultaneous resolutions therefore serialize: one 201, the other 409 `ConcernResolved`, exactly one row. An action or notification racing a resolution serializes with it the same way, so nothing can be written after the resolution commits. The unique `ConcernId` index on `ConcernResolutions` is a database backstop, not the concurrency mechanism. Per-case numbers are computed under the same lock (the unique `(EudrCaseId, Number)` index is again a backstop). Order inside each write: transaction (Owner check first for resolution), case lock (404), concern lookup within the case (404), concern creation only: case-state rule (409), body shape (400/415), validation and clock rules (400), then state rules (409), then the write.

**Evidence-document retention.** `EvidenceDocumentId` on an action or notification must be a **current** (not withdrawn) document of the **same case and tenant** when it is linked; otherwise 409 `EvidenceDocumentNotCurrent`, identical for withdrawn, unknown, other-case and other-tenant ids (nothing leaks). The database enforces the same-case/same-tenant relation with a composite Restrict FK to `Documents (OrganizationId, EudrCaseId, Id)`. Later withdrawal of the document sets only `WithdrawnAtUtc`: the action or notification and its link stay and stay readable, and the existing document download semantics apply. No document bytes are copied into concern tables.

**Recipient identity model.** `ConcernNotification` stores `CaseRecipientId`, not a copy of the recipient's personal data. `CaseRecipient` rows are fully immutable and never deleted (ADR 0008 guard), so the row id is a stable identity that reconstructs exactly who was named, even after the case's recipient statement is superseded. The foreign key is `(OrganizationId, CaseRecipientId)` to `CaseRecipients (OrganizationId, Id)` with Restrict. `CaseRecipient` carries no case id, so **the service verifies that the recipient belongs to a recipient statement of the same case**; an unknown, other-tenant or other-case recipient id all answer with the same 409 `RecipientNotFound`. **A notification may reference a `CaseRecipient` from any historical recipient statement version of that case, not only the current one.** That is intentional: a recipient who is no longer in the latest statement may still have been notified, and the historical notification must stay reconstructable. The read model joins the stored name, postal address and e-mail of that immutable row.

**Audit.** Four events with ids and numbers only: `ConcernRecorded`, `ConcernActionRecorded`, `ConcernNotificationRecorded`, `ConcernResolved`. They name the concern id and number, the new row's id and (where relevant) the category, channel, outcome and recipient id. They never contain `Details`, an action `Description`, a notification `Note`, a resolution `Rationale` or recipient personal data, and they make no compliance, approval, recall or regulatory claim. The case-scoped audit trail (`CaseDetails.auditEvents`) shows them like any other event.

**Database retention and rollback.** Migration `20261015224634_PostCompletionConcerns` is purely additive: it creates `CaseConcerns`, `ConcernActions`, `ConcernNotifications` and `ConcernResolutions` and changes no existing table. All foreign keys are composite and tenant-aware with Restrict. Per-case `(EudrCaseId, Number)` is unique and `ConcernId` is unique on resolutions; four CHECKs reject blank text. The four types are fully immutable under the application-level `EnforceConcernHistoryImmutable` guard in `SaveChangesAsync(CancellationToken)` (any modified or deleted entry throws "Concern history is immutable; record a new concern fact."); raw SQL and bulk writes bypass it, and the FKs, indexes and CHECKs are the database backstop. **Down** starts with a `DO $$` guard that raises `Concern records exist; rollback would destroy history` if **any** of the four tables has a row (the retained rows survive); with all four empty it drops them. Details: [DATABASE](../DATABASE.md).

## Alternatives considered

- **Reopen the case to record the concern.** Rejected: it mutates the live status of a case whose decision is still historically true, forces a new review, and mixes the original account with later findings.
- **Edit the closure, review or snapshot, or add concern fields to the case.** Rejected: those are immutable history; a concern is a separate layer.
- **A mutable concern with a status column and transitions.** Rejected: status is derived from the single resolution; no transition machinery is needed.
- **Allow editing, deleting or superseding concerns, actions or notifications.** Rejected for v1: facts are append-only; a later issue is a new concern. A supersede flow can be added if a real correction need appears.
- **Copy recipient name/address/e-mail into each notification.** Rejected: the recipient row is already immutable, so a copy would duplicate personal data without adding history.
- **Restrict notifications to the current recipient statement.** Rejected: a historical recipient must remain notifiable and the notification reconstructable.
- **Re-check the terminal case status on every action, notification and resolution.** Not done: the concern's own resolution state is the gate; coupling it to the case state would make an open concern unwritable after any later case change.
- **A third resolution outcome for external escalation.** Rejected by the owner: escalation is not necessarily the end of the matter; it is an action and/or notification.
- **Task management (assignee, due date, priority, action status) or an automatic notification channel.** Rejected: out of scope; Provena records what was done and sends nothing.

## Consequences

- A completed or archived case can accumulate any number of concerns; each is open until an Owner resolves it, and a resolved concern is never reopened. If a new issue appears after resolution, record **another** `CaseConcern`.
- A concern on a case that is later reopened (ADR 0009) keeps its record and stays readable; nothing in this decision disables or destroys it, and v1 defines no reopen behaviour for concerns.
- Six internal routes (below), one migration and four audit event types; no change to existing DTOs, `CaseClosure`, review or export shapes.
- Lists are unpaginated per-case collections (pilot scale, like the other per-case histories).
- Guards are application-level; the database backstop is the composite Restrict FKs, the unique indexes and the CHECKs.
- Nothing here is a legal-compliance, risk, recall or release decision.

## Deferred scope

Not implemented in v1: a generic or concern-driven case reopen; a recall workflow; automatic notification of downstream recipients (e-mail, portal or letter delivery); an authority connector; cross-case incidents and supplier-wide or product-wide impact discovery; enforced holds on future shipments or any automatic blocking; affected-quantity subsets and multi-lot incident scope; EU DDS amendment or withdrawal; automation of any post-resolution regulatory workflow; editing, superseding or deleting concern facts; per-recipient delivery confirmation; concern pagination or search across cases.

## Implementation and verification

- Implementation: commit `fcd1c39`. Sources: `Domain/Concerns/CaseConcern.cs` (four entities and the three enums), `Contracts/Concerns.cs` (+ `CreateConcernValidator`, `RecordConcernActionValidator`, `RecordConcernNotificationValidator`, `ResolveConcernValidator` in `Contracts/Validators.cs`), `Services/ConcernService.cs`, `Endpoints/Cases/ConcernEndpoints.cs` (registered by `MapProvenaEndpoints`), mapping and `EnforceConcernHistoryImmutable` in `Data/AppDbContext.cs`, migration `Data/Migrations/20261015224634_PostCompletionConcerns.cs`.
- Tests: `ConcernDomainTests` (unit, validators), `ConcernTests` (HTTP and PostgreSQL), `ConcernModelTests` (guards, constraints), the concern migration tests in `MigrationTests` (clean schema, additive upgrade, populated Down refused per table, empty Down), and the route rows in `EndpointInventoryTests` (see [TESTING](../TESTING.md)). Full verification at implementation: **231 unit and 291 integration tests passed**, Release build with warnings as errors, PostgreSQL via Testcontainers.
- The local client compatibility suite has not been run.

## Revisit trigger

Any need to correct or withdraw a recorded concern fact; reopen behaviour tied to a concern; cross-case or supplier-/product-wide incidents; holds or blocking of future shipments; affected-quantity or multi-lot scope; automatic or connector-based notification; authority or Information System interaction; an assignment or deadline model; a retention or erasure policy for these rows; pagination if concern volume grows.
