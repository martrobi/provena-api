# ADR 0011: External DDS Record v1 (a retained, structured, manually recorded external submission fact)

- Date: 2026-10-15
- Decision status: Accepted
- Implementation status: **Implemented** (backend only; the separate client has not adapted). External DDS Record v1 was introduced by backend implementation commit `5644224` (`feat(api): add external DDS records`), directly on top of `a06df65`. This documentation slice follows it; the frontend handoff is [2026-10-external-dds-record](../frontend-handoffs/2026-10-external-dds-record.md).
- Decision owner / approval evidence: the owner stated the rules in the External DDS Record v1 implementation request and its follow-up: an append-only record tied to the exact immutable Owner review; reference and verification numbers kept separate; Owner-only recording with optimistic concurrency; replacement as a correction; the `ExternalDdsCompleted` close gate; `submittedAtUtc >= review.ReviewedAtUtc`; no EU Information System integration. The plan artifact itself is not retained in the repository.
- Supersedes / superseded by: **amends [ADR 0009](0009-case-lifecycle-v2.md) only where it makes the free-text `externalDdsReference` of the close request the source of the DDS reference** ("`ExternalDdsCompleted` requires an external DDS reference ... The other outcomes reject a reference"). The structured current `ExternalDdsRecord` is now authoritative for `ExternalDdsCompleted`; the request field is a backward-compatible optional echo. Everything else in ADR 0009 (Owner-only lifecycle, the review gate, outcome derivation, reopen, archive) and in [ADR 0008](0008-completeness-facts-and-reviewed-history.md) and [ADR 0010](0010-quantity-reconciliation-v1.md) remains in force; the quantity gate still comes first.

## Context and evidence

Before this decision (observed at `a06df65`):

- A DDS reference existed only as a free-text string on the close request, copied onto the immutable `CaseClosure` at close time (`CaseClosure.ExternalDdsReference`, max 200). `ExternalDdsCompleted` required it; the other outcomes rejected it. Nothing tied the string to the Provena version that was submitted, nothing recorded who submitted it or when, there was no place for the access-enabling verification number, a receipt, or a correction, and any old or careless client could close a case by typing text.
- `EudrCase` has no DDS reference property (`CompletedAtUtc` and the case-level `ExternalDdsReference` were removed by `CaseLifecycleV2`, ADR 0009). Only `CaseClosure.ExternalDdsReference` remains.
- The EU Information System is not integrated and no credentials are stored. Provena can therefore only be told about a submission made outside it. See [EUDR_GAP_ANALYSIS](../EUDR_GAP_ANALYSIS.md) G21 and G25 (reference versus verification number, preserved submitted version, receipt, actor, time).

## Decision

**What it is.** `ExternalDdsRecord` records an **external fact**: an Owner states that the dossier version captured by one immutable Owner review was submitted to the EU Information System outside Provena, with the reference number, the verification number, the submission time and an optional receipt document. **It does not mean EU approval, regulatory approval, compliance, risk clearance or verification by Provena.** Neither number is proof of acceptance; Provena stores what it is told and verifies nothing. No network call exists.

**Domain invariants.**
- Entity `ExternalDdsRecord` (tenant-owned, `Domain/ExternalDds/ExternalDdsRecord.cs`): `Id`, `OrganizationId`, `EudrCaseId`, `Number` (per-case sequence, from 1), `ReviewId`, `ReferenceNumber`, `VerificationNumber`, `SubmittedAtUtc`, `RecordedByUserId`, `CreatedAtUtc` (shown as `recordedAtUtc`), optional `ReceiptDocumentId`, optional `SupersedesExternalDdsRecordId`, optional `ReplacementReason`, `SupersededAtUtc`.
- `ReferenceNumber` and `VerificationNumber` are required, trimmed, non-blank, at most 200 characters, stored in separate columns and never interchangeable.
- The record points at the **exact immutable `ReviewRecord`** (`ReviewId`). That review is the Provena version associated with the submission. No dossier fact is copied into the record, the review is never modified, and `FingerprintVersion` 1, the review snapshot schema and the reviewed export are unchanged. Recording a DDS record does not change any dossier fact, so it never makes a review stale.
- At most one **current** (non-superseded) record per case.

**Immutability and replacement.** Rows are never edited or deleted. A correction inserts a new record that supersedes the current one: `SupersedesExternalDdsRecordId` is set automatically to the previous current row, a non-blank `ReplacementReason` is required, and the previous row's `SupersededAtUtc` goes from null to a timestamp (the only permitted update, enforced by the retained-evidence retirement guard). The old row is retained forever and stays readable through the history. A first record must not carry a `ReplacementReason`. **A replacement is only a correction of Provena's recorded fact; it is not an EU Information System amendment, withdrawal or any other official statement lifecycle step.** The supersession is saved first and the successor second inside one transaction (EF cannot order the writes around the filtered unique index).

**Optimistic concurrency.** The request carries `expectedLatestExternalDdsRecordId`: `null` for the first record, the exact current record id for a replacement. Under the case lock a mismatch is `409` with `errors.reason = "ExternalDdsHistoryChanged"` and nothing is written (no row, no supersession, no audit). A competing second first-record request loses the same way.

**Owner authorization.** Recording is Owner-only through the shared `OwnerGate` (live `OrganizationUsers` check before any case lookup or body read; a Member gets `403` whatever the case id or body). Reads (`GET`) are open to any authenticated member of the tenant. Order inside POST: transaction, Owner check, case lock (`404`), mutable case (`409` Closed or archived), body shape (`400`), future `submittedAtUtc` (`400`), optimistic check (`409`), replacement-reason rule (`400`), review rules (`409`/`400`), receipt rule (`409`), write.

**Linkage to the exact review.** `reviewId` must belong to the same case and tenant, be the **latest** review (highest revision number), have decision `AcceptedAsCompleteUnderPolicy` and be **Current** for the present dossier, re-evaluated under the case lock. Reasons: `ReviewNotLatest` (also for an unknown, foreign or no review: the response is identical and discloses nothing), `ReviewNotAccepted`, `StaleReview` (with `changedAreas`). `QuantityReconciliation` is deliberately **not** required to record: the submission happened outside Provena. Quantity reconciliation remains a close gate.

**Chronology.** `submittedAtUtc` is required, normalised to UTC, must not be in the future (`400`, `errors.SubmittedAtUtc`) and must satisfy **`submittedAtUtc >= review.ReviewedAtUtc`** against the referenced review loaded under the lock (`400`, `errors.SubmittedAtUtc`; equality is valid, no tolerance). A violation writes and supersedes nothing: a failed replacement leaves the previous record current. The chronology message never contains a submitted value.

**Receipt document.** `receiptDocumentId` is optional. When supplied it must be a **current (not withdrawn) document of the same case and tenant** at recording time, otherwise `409` `ReceiptDocumentNotCurrent` (missing, foreign, other-case and withdrawn ids answer identically). The database enforces the same-tenant/same-case relation by a composite foreign key. Later withdrawal of the document does not destroy the record or the historical link (withdrawal only sets `WithdrawnAtUtc`).

**Close-gate interaction.** For the `ExternalDdsCompleted` outcome (a review with decision `AcceptedAsCompleteUnderPolicy`) the order is: (1) the existing review checks, including `StaleReview`; (2) the Quantity Reconciliation v1 gate (`QuantityNotReconciled`, ADR 0010); (3) the External DDS gate: a current record must exist (`ExternalDdsRecordMissing`), it must point at exactly the latest Current review (`ExternalDdsReviewMismatch`) and a supplied legacy reference must equal its `ReferenceNumber` (`ExternalDdsReferenceMismatch`). All three are `409` with `errors.reason`, and a safe `errors.externalDds` summary (`recordId`, `reviewId`, `referenceNumber`, `submittedAtUtc`; never the verification number). A failed close writes no closure and no audit event and does not revoke supplier links. `OutsideRegulatoryScope` and `UnsupportedFlow` closes never require or look at an External DDS record.

**Backward compatibility with `externalDdsReference`.** `CloseCaseInput.externalDdsReference` (the *close request* field; there is no "CompleteInput" in the current code) is still accepted, trimmed and limited to 200 characters. It is **not authoritative** and can never authorise `ExternalDdsCompleted` by itself. If supplied for `ExternalDdsCompleted` it must equal the current record's `ReferenceNumber`; for the other outcomes it is still rejected (`400`). A successful close stores the **record's** `ReferenceNumber` in `CaseClosure.ExternalDdsReference`. That closure column is the **only** legacy compatibility field: `EudrCase` has no `ExternalDdsReference` property and none is reintroduced. `CaseClosureDto.externalDdsReference` keeps its shape.

**Sensitive `VerificationNumber`.** The verification number is access-enabling data. It is returned **only** by the three dedicated, authenticated External DDS endpoints. It is never part of the case, lifecycle, completeness, review or export DTOs, never in ProblemDetails (including the close-gate summary and validation messages, which do not echo values), never in audit text and never logged. The audit events `ExternalDdsRecorded` and `ExternalDdsReplaced` carry ids only and make no approval or compliance claim; `CaseClosed` additionally names the record id. At rest it is stored unencrypted like every other column; this ADR does not add field-level encryption (see revisit triggers).

**Terminal cases.** Closed and archived cases keep both reads but reject a new record or replacement (`409`, `CaseService.EnsureMutable`). Reopening is [ADR 0009](0009-case-lifecycle-v2.md); a manually recorded concern or corrective action after closure is [ADR 0012](0012-post-completion-concerns-v1.md) and never changes an External DDS record.

## Alternatives considered

- **Keep the free-text close reference.** Rejected: not tied to a review, actor or time, trivially forgeable by a client, no verification number, no correction path.
- **Add `ExternalDdsReference` back to `EudrCase`.** Rejected: ADR 0009 removed it; one structured record is the source of truth and the closure keeps an immutable copy.
- **Edit the record in place, or allow PUT/PATCH/DELETE.** Rejected: external facts are evidence; history must be reconstructable. Corrections are new rows.
- **Copy dossier facts or the fingerprint into the record.** Rejected: the immutable review already is the exact version; copying would duplicate the snapshot and risk drift.
- **Require `Matched` quantities to record.** Rejected: it would block recording something that already happened outside Provena. Reconciliation stays a release/close gate.
- **Tolerance for `submittedAtUtc` before the review.** Rejected by the owner: no arbitrary tolerance.
- **Model amendments, withdrawals, statuses, polling or an Information System connector now.** Deferred (below): they need credentials, a status model and legal decisions that are not made.
- **Store the verification number hashed or encrypted.** Not done: the Owner must be able to read it back; a key-management decision is open.

## Consequences

- **Breaking for clients that close with free text:** `ExternalDdsCompleted` now answers `409 ExternalDdsRecordMissing` unless a current record exists. `CloseCaseInput` is unchanged. An old client that only sends `externalDdsReference` can no longer close; one that sends a different value than the record gets `ExternalDdsReferenceMismatch`.
- A new reason set, three new routes and a new sensitive field; see the handoff.
- A case can hold a record for a review that later becomes stale; closing then needs a new review and a new record (the record points at an older review: `ExternalDdsReviewMismatch`, or `StaleReview` first).
- The history read is an unpaginated per-case list (pilot scale, like the other per-case histories).
- Migration `20261015203340_ExternalDdsRecord` is additive and its `Down` is guarded like the earlier retention migrations: it raises `External DDS records exist; rollback would destroy history` while any record exists and drops the empty table otherwise.
- Application-level guards (`SaveChangesAsync(CancellationToken)`) enforce row immutability; raw SQL and bulk writes bypass them. The database backstop is the composite Restrict foreign keys, the partial/unique indexes and the two check constraints.
- Nothing here is a legal-compliance, risk, release or DDS-approval decision, and nothing verifies a DDS reference against the Information System.

## Implementation and verification

- Implementation: commit `5644224`. Sources: `Domain/ExternalDds/ExternalDdsRecord.cs`, `Contracts/ExternalDds.cs` (+ `CreateExternalDdsRecordValidator` in `Contracts/Validators.cs`), `Services/ExternalDdsService.cs`, `Endpoints/Cases/ExternalDdsEndpoints.cs`, the gate in `Services/CaseClosurePolicy.cs` (`ExternalDdsError`) and `Services/CaseLifecycleService.cs` (`Close`), mapping and guard in `Data/AppDbContext.cs`, migration `Data/Migrations/20261015203340_ExternalDdsRecord.cs`.
- Tests: `ExternalDdsDomainTests` (unit), `ExternalDdsModelTests`, `ExternalDdsTests`, the External DDS migration tests in `MigrationTests`, and the updated lifecycle and quantity tests (see [TESTING](../TESTING.md)). Full verification at implementation: **227 unit and 266 integration tests passed**, Release build with warnings as errors, PostgreSQL via Testcontainers.
- The local client compatibility suite has not been run.

## Revisit trigger

An Information System connector or credential storage; official amendment, withdrawal, rejection or status polling; outage/contingency handling; grouped DDS or incoming/simplified identifiers; post-closure corrective action or reopen-with-record rules; multi-lot cases (several records or coverage links per case); a decision to encrypt or mask the verification number; a retention or erasure policy for these rows.
