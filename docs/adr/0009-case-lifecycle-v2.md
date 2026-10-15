# ADR 0009: Case Lifecycle v2 (Owner-backed closure, reopen and independent archive)

- Date: 2026-10-15
- Decision status: Accepted
- Implementation status: **Implemented** (backend only; the separate client has not adapted). Slice 1: persistence model and migration `CaseLifecycleV2` (commit `2fad51f`). Slice 2: services, routes, dashboard/list/export changes (commit `034f73c`). Slice 3: this documentation and the frontend handoff.
- Decision owner / approval evidence: the owner stated the final lifecycle rules in the Case Lifecycle v2 planning request and in the Slice 1, 2 and 3 implementation requests (Owner-only close/reopen/archive/unarchive, a Current Owner review as the only closure gate, the decision-to-outcome mapping, immutable closure/reopening history, independent archive with reasons, mutually exclusive dashboard buckets, a clean pre-production model). The plan artifact itself is not retained in the repository.
- Supersedes / superseded by: **supersedes [ADR 0008](0008-completeness-facts-and-reviewed-history.md) only where ADR 0008 says that Owner reviews do not gate completion/closure** (the statements "`Complete` is not gated by a review", "no `Complete` eligibility" changes, "Reviews do not gate `Complete`", and the pending lifecycle question D6). Every other ADR 0008 decision (case-owned facts, the completeness read model and fingerprint, the review record, the Owner gate and review order, freshness, the reviewed export) remains in force and is relied upon here. It also removes shipment archival (`DELETE /api/shipments/{id}`), which [ADR 0007](0007-case-dossier-lot-attribution.md) mentions as unchanged in its shipment-lock bullet; that remark is historical. Archive now belongs to the case ("Decision" below).

## Context and evidence

Before this decision (observed in `CaseService.Complete`, `ShipmentService.Archive`, `EudrCaseStatusService` and `docs/EUDR_DOMAIN.md` at commit `85593a8`):

- `POST /api/eudr-cases/{id}/complete` closed a case when the legacy status was `ReadyForExport`. That status is derived only from sent requests and the six legacy requirement decisions, so a case could be completed with no dossier review, with an incomplete dossier, or after waiving every requirement. Any member could do it. The case stored a mutable `CompletedAtUtc` and one unverified `ExternalDdsReference` string.
- `DELETE /api/shipments/{id}` overwrote the workflow status with `Archived`, so a Completed case lost that fact, and nothing could undo it. There was no reopen.
- ADR 0008 recorded, as a deliberately pending decision (D6), that Owner reviews did not gate completion.
- Owner reviews are already immutable, Owner-only, pinned to exact evidence and freshness-checked against a fingerprint of the facts (ADR 0008).

The owner wants a clean pre-production model rather than preserving legacy behaviour: closure must rest on an Owner review of the present dossier, closing and reopening must leave a reconstructable history, and archive must not destroy or rewrite lifecycle history. No CI/CD, hosting, CQRS, MediatR, microservices, event buses or background workers are introduced.

## Decision

Two independent dimensions.

**Workflow status** is `Draft`, `WaitingForSupplier`, `ReviewNeeded`, `ReadyForExport` or `Closed` (stored as integers 0-4). `Completed` and `Archived` no longer exist. The four open statuses are derived by `EudrCaseStatusService.CalculateOpen` from requests and requirements; `ReadyForExport` stays an operational hint and is **not** a closure gate. A `Closed` case keeps that status until it is explicitly reopened.

**Archive** is a separate, reversible dimension (a `CaseArchival` period), never a status and never a rewrite of the workflow status.

**Closing** (`POST .../close`, Owner-only):
- The Owner names `expectedReviewId`. It must be the **latest** review (highest `CaseRevision.Number`), the review must be **Current** for the present dossier (fingerprint freshness under the review's stored fingerprint version) and it must not already back a closure. One review backs at most one closure; this is also a unique index.
- The outcome is derived from the review decision and cannot be chosen: `AcceptedAsCompleteUnderPolicy` -> `ExternalDdsCompleted`, `ConfirmedOutsideRegulatoryScope` -> `OutsideRegulatoryScope`, `ConfirmedUnsupportedFlow` -> `UnsupportedFlow`. `ReturnedForCorrection` cannot close.
- `ExternalDdsCompleted` requires an external DDS reference (trimmed, 1-200 characters), stored on the immutable closure and not verified. The other outcomes reject a reference. *Amended by [ADR 0011](0011-external-dds-record-v1.md):* the free-text request field is no longer the source. `ExternalDdsCompleted` requires a current structured External DDS record tied to the latest Current review; the closure stores that record's reference number; a supplied request value is optional and must equal it. The other outcomes still reject a reference.
- Closing revokes active supplier requests. It does not change any requirement, review or fact.
- A stale review cannot close. A new review is the only way to make a changed dossier closable.
- *Amended by [ADR 0010](0010-quantity-reconciliation-v1.md):* closing as `ExternalDdsCompleted` additionally requires the current quantity reconciliation to be `Matched`, also for a Current review made under an older policy version (`409` with `errors.reason = "QuantityNotReconciled"`). The other outcomes have no quantity gate and a successful close is still 201.
- *Amended by [ADR 0011](0011-external-dds-record-v1.md):* after the stale-review and quantity checks, `ExternalDdsCompleted` also requires the External DDS record gate (409 `ExternalDdsRecordMissing`, `ExternalDdsReviewMismatch` or `ExternalDdsReferenceMismatch`).

**Reopening** (`POST .../reopen`, Owner-only) names the current closure (`expectedClosureId`) and requires a non-blank reason. It writes a `CaseReopening`, re-derives the open workflow status, and changes nothing else: closures, reviews, exports, DDS references and audit history are preserved. Reopening does not restore revoked supplier links. After a reopen the old review cannot close again (it already backs a closure); a new review is required even if no fact changed.

**Archive and unarchive** (`POST .../archive`, `.../unarchive`, Owner-only) each require a non-blank reason. Archive revokes active supplier requests; unarchive restores nothing. An archived case rejects every business mutation, including close and reopen. A closed case can be archived and stays `Closed`. Each archive period is a history row; an unarchive only retires the current period one way.

**Blocked while Closed or archived:** every case-scoped business write (requirement review, supplier-request create/regenerate, public token routes, document and geometry changes, lot, master-data refresh, applicability, recipients, evidence links, reviews, shipment edit). Reads, both exports and supplier-request revoke remain available.

**History** is reconstructable from `CaseClosure` (immutable, with its review), `CaseReopening` (immutable, unique per closure) and `CaseArchival` (retire-only). `GET .../lifecycle` and the working export expose it.

**Lists and dashboard:** the case list excludes archived cases by default (`archived=exclude|only|include`). The dashboard has three mutually exclusive buckets: `activeOpen`, `activeClosed` (by outcome) and `archived` (any status), so `total = activeOpen + activeClosed + archived`.

**Removed:** `CompletedAtUtc` and the case-level `ExternalDdsReference` (the reference lives on the closure), `POST .../complete` and `DELETE /api/shipments/{id}`.

Deliberately undecided: whether a Member may ever archive; verification of a DDS reference or any DDS submission; retention and erasure of lifecycle history; post-closure "new concern" handling beyond the manual records of [ADR 0012](0012-post-completion-concerns-v1.md) (Article 4(5) style follow-up: impact discovery, recall, delivery); automatic notification of suppliers; a "closed under a newer policy version" rule (a policy-version change is informational and never stales a review, as in ADR 0008).

## Alternatives considered

- **Keep `Completed` as the closure and only add a review precondition.** Rejected: it keeps a mutable single completion timestamp and DDS string, and gives no history across reopen.
- **Keep `Archived` as a status.** Rejected: it overwrote the workflow state and could not be undone; independent archive preserves both.
- **Store closure data on the case row and overwrite on reopen.** Rejected: closure history, references and the backing review would be lost.
- **An event stream, mediator or background worker for transitions.** Rejected by the owner; plain scoped services under the case lock are sufficient (ADR 0001).
- **Let one Current review back repeated closures.** Rejected by the owner: after a reopen the Owner records a new review.
- **Gate closing on `ReadyForExport`.** Rejected: it is derived from six legacy requirement decisions only, and outside-scope or unsupported-flow cases never need them. The Current Owner review is the gate. (Inference: coupling the hint to the gate would contradict "Supplier submission is not acceptance".)
- **Let any member archive.** Rejected by the owner: archive and unarchive are Owner-only.
- **Fingerprint version bump to include lifecycle state.** Not needed: lifecycle state is excluded from the fingerprint, so closing, reopening and archiving never stale a review.

## Consequences

- **Breaking client changes:** `Completed`/`Archived` status values, `completedAtUtc` and `externalDdsReference` on the case, `POST .../complete`, `DELETE /api/shipments/{id}` and the old dashboard shape are gone; new routes, `closure`/`archive` on case details, `archived`/`closureOutcome` on list items and the `archived` list filter exist. See the [frontend handoff](../frontend-handoffs/2026-10-case-lifecycle-v2.md).
- Closing is not a legal-compliance, risk, release or DDS-submission decision; `ExternalDdsCompleted` records an Owner acceptance under the pilot policy plus an unverified reference.
- A closed case with an `OutsideRegulatoryScope` or `UnsupportedFlow` outcome can still report `dataCoverage: Incomplete`; completeness stays independent of status.
- Review rows pinned by a closure can never be deleted (Restrict foreign keys). Lifecycle history is retained; retention and erasure remain part of the open legal item.
- Legacy local data: the migration invalidates legacy completions (no closure and no reopening is invented), recalculates the status of former Completed/Archived cases and turns former Archived cases into archive periods. Down is refused once any lifecycle row exists.
- Guards are application-level, like ADR 0005-0008: raw SQL and other save overloads bypass them; the unique indexes and Restrict foreign keys are the database backstop. "A Closed status means the highest-Number closure has no reopening" is a service invariant, not a SQL constraint.

## Implementation and verification

`CaseService.EnsureEditable` (named in ADR 0008) was replaced by the async `CaseService.EnsureMutable`, which rejects a Closed case and, independently, an archived one.

Source: `Domain/Lifecycle/CaseLifecycle.cs`, `Data/AppDbContext.cs` (mapping, `EnforceLifecycleHistoryImmutable`, archival retirement), `Data/Migrations/*CaseLifecycleV2*`, `Services/CaseLifecycleService.cs`, `CaseLifecycleReadService.cs`, `CaseClosurePolicy.cs`, `OwnerGate.cs`, `CaseService.cs` (`EnsureMutable`, list, dashboard), `EudrCaseStatusService.cs`, `ExportService.cs`, `Endpoints/Cases/CaseLifecycleEndpoints.cs`, `Contracts/Lifecycle.cs`. Tests: unit `LifecycleDomainTests`, `DomainTests`; integration `CaseLifecycleModelTests`, `CaseLifecycleTests`, `MigrationTests`, `EndpointInventoryTests`, `HttpContractTests`.

Result recorded for commit `034f73c` (`bash scripts/verify.sh`; `pwsh` was not installed): Release build with 0 warnings, 205 unit tests and 236 integration tests passed. The documentation slice changed no code and did not re-run them. `scripts/smoke.py` was updated but not executed (no Python in that environment); `CaseLifecycleTests.The_minimal_smoke_sequence_closes_reopens_archives_and_unarchives_a_case` covers the same sequence. The local client compatibility suite has not been run.

## Revisit trigger

A decision on Member archive rights, DDS verification or submission, retention/erasure of lifecycle history, post-closure corrective action, a release/approval status, or a change to the review policy that should invalidate an existing closure.
