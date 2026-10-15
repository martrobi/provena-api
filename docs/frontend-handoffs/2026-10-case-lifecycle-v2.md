# Case Lifecycle v2: Owner-backed closure, reopen and independent archive

Status: **complete backend contract (Slices 1-3).** The client has not adapted yet; this document is the input for the client's Slice 4. Canonical sources are the code, the tests, [API](../API.md) ("Case lifecycle"), [EUDR_DOMAIN](../EUDR_DOMAIN.md) ("Case lifecycle v2") and [ADR 0009](../adr/0009-case-lifecycle-v2.md). It supersedes the lifecycle statements of the earlier handoffs ([retained evidence](2026-10-retained-evidence.md), [case dossier](2026-10-case-dossier-lot-sources.md), [completeness and reviews](2026-10-completeness-reviewed-history.md)): wherever they say a `Completed` or `Archived` case, read "Closed or archived", and wherever they say `Complete` is not gated by a review, read the closing rules below.

## 1. Summary

- A case has a **workflow status**: `Draft`, `WaitingForSupplier`, `ReviewNeeded`, `ReadyForExport` or `Closed`. `Completed` and `Archived` no longer exist as statuses.
- **Archive** is a separate, reversible flag-like dimension with its own history. An archived case keeps its workflow status.
- An organization **Owner** closes a case, and only on the latest Owner review when that review is `Current` for the present dossier and has not already closed a case. The outcome follows from the review decision. `ReadyForExport` is only an operational hint, not the gate.
- Close, reopen, archive and unarchive are **Owner-only**. Reopen needs the current closure id and a reason; archive and unarchive need reasons.
- A closed or archived case is **read-only**. Closing and archiving revoke the supplier links; reopening and unarchiving never restore them.
- Closing is not a legal-compliance, risk, release or DDS-submission decision, and the external DDS reference is recorded but never verified.

## 2. Changed endpoints and behaviour

**New** (all authenticated, under `/api/eudr-cases/{caseId}`):

| Route | Who | Success |
| --- | --- | --- |
| `POST /close` | Owner | **201** `CaseClosureDto` (Location: `GET .../lifecycle`) |
| `POST /reopen` | Owner | **201** `CaseReopeningDto` |
| `POST /archive` | Owner | **201** `CaseArchivalDto` |
| `POST /unarchive` | Owner | **200** `CaseArchivalDto` |
| `GET /lifecycle` | any member | **200** `CaseLifecycleDto` |

**Removed:**

| Removed | Now |
| --- | --- |
| `POST /api/eudr-cases/{caseId}/complete` | 404. Use review + `close`. |
| `DELETE /api/shipments/{id}` (archived the case) | 405. Use `POST .../archive`. |
| case fields `completedAtUtc`, `externalDdsReference` | gone; the reference lives on `closure.externalDdsReference` |
| status values `Completed`, `Archived` | gone; `Closed` replaces `Completed`, archive is `archive`/`archived` |
| dashboard field `totalOpenCases` and the flat dashboard shape | replaced (section 4) |
| `status=Completed` / `status=Archived` list filter | 400 |

**Changed:**

- `GET /api/eudr-cases/{id}` (case details) gains `closure` and `archive` (section 4).
- `GET /api/eudr-cases` items gain `archived` and `closureOutcome`; the list takes `archived` (section 4) and **hides archived cases by default**.
- `GET /api/dashboard` has a new response (section 4).
- `GET .../export` (working export) manifest gains `lifecycle` and loses `externalDdsReference` and `completedAtUtc`; summary.csv `status` shows `Closed`.
- The 409 wording for every write on a non-editable case changed (section 6). The supplier-request **revoke** route is unchanged and stays allowed.

## 3. Request contracts

All four write bodies are JSON (`Content-Type: application/json`), enums are strings, and unknown properties are ignored. The handler reads the body only after the live Owner check and the state checks (section 6).

```json
// POST /api/eudr-cases/{caseId}/close
{ "expectedReviewId": "7c1f...", "externalDdsReference": "DDS-2026-0001" }
```
- `expectedReviewId` (required, non-empty GUID): the `latestReview.reviewId` the Owner loaded from `GET .../completeness`.
- `externalDdsReference` (optional string): trimmed; max 200; blank is treated as absent. **Required** when the review decision is `AcceptedAsCompleteUnderPolicy` (outcome `ExternalDdsCompleted`); **rejected with 400** for `ConfirmedOutsideRegulatoryScope` and `ConfirmedUnsupportedFlow`.

```json
// POST /api/eudr-cases/{caseId}/reopen
{ "expectedClosureId": "b2e9...", "reason": "Supplier sent corrected geometry." }
```
- `expectedClosureId` (required): `lifecycle.currentClosure.closureId`.
- `reason` (required): trimmed, non-blank, max 4000.

```json
// POST /api/eudr-cases/{caseId}/archive   and   POST /api/eudr-cases/{caseId}/unarchive
{ "reason": "Retention hold." }
```
- `reason` (required): trimmed, non-blank, max 4000.

There is no outcome in any request: the client never chooses it.

## 4. Response contracts

Enums: workflow `status` is `Draft | WaitingForSupplier | ReviewNeeded | ReadyForExport | Closed`. Closure `outcome` is `ExternalDdsCompleted | OutsideRegulatoryScope | UnsupportedFlow`. `reviewDecision` is the existing `AcceptedAsCompleteUnderPolicy | ConfirmedOutsideRegulatoryScope | ConfirmedUnsupportedFlow | ReturnedForCorrection`. Timestamps are ISO 8601. No response contains `organizationId`.

Mapping: `AcceptedAsCompleteUnderPolicy` -> `ExternalDdsCompleted`; `ConfirmedOutsideRegulatoryScope` -> `OutsideRegulatoryScope`; `ConfirmedUnsupportedFlow` -> `UnsupportedFlow`; `ReturnedForCorrection` -> cannot close.

```json
// CaseClosureDto
{ "closureId": "b2e9...", "number": 1, "outcome": "ExternalDdsCompleted", "reviewId": "7c1f...", "reviewRevisionNumber": 2,
  "reviewDecision": "AcceptedAsCompleteUnderPolicy", "externalDdsReference": "DDS-2026-0001",
  "closedByUserId": "…", "closedAtUtc": "2026-10-15T09:30:00+00:00", "reopening": null }

// CaseReopeningDto
{ "reopeningId": "…", "closureId": "b2e9...", "reason": "Supplier sent corrected geometry.", "reopenedByUserId": "…", "reopenedAtUtc": "…" }

// CaseArchivalDto  (the three unarchive fields are null while the period is current)
{ "archivalId": "…", "number": 1, "reason": "Retention hold.", "archivedByUserId": "…", "archivedAtUtc": "…",
  "unarchiveReason": null, "unarchivedByUserId": null, "unarchivedAtUtc": null }
```

`archivedByUserId` is null only for periods created by the data migration from the legacy `Archived` status (reason `Archived before case lifecycle v2.`).

**Lifecycle history** `GET /api/eudr-cases/{caseId}/lifecycle` (404 for an unknown or foreign case; unpaginated):

```json
{ "status": "Closed", "archived": false,
  "currentClosure": { /* CaseClosureDto, set only while status is Closed */ },
  "currentArchival": null,
  "closures":  [ /* every CaseClosureDto oldest first, each with its "reopening" (or null) */ ],
  "archivals": [ /* every CaseArchivalDto oldest first */ ] }
```

**Case details** `GET /api/eudr-cases/{id}`: `status` is the workflow status; **added** `closure` (the current `CaseClosureDto`, only while `Closed`, else null) and `archive` (the current `CaseArchivalDto`, else null); **removed** `completedAtUtc` and `externalDdsReference`. All other members are unchanged.

**Case list** `GET /api/eudr-cases` (still `PageResult<T>`): items gain `archived` (boolean) and `closureOutcome` (the current closure's outcome while `Closed`, else null).
- `archived=exclude` (default when omitted), `only` or `include`; names are case-insensitive; any other value is **400**.
- `status` takes a workflow status; `Completed` and `Archived` are **400**. The status filter applies to the workflow status of archived cases too (an archived case that is `Draft` matches `status=Draft` with `archived=include|only`).
- Archived cases are **absent from the default list**, so the client needs an explicit archived view.

**Dashboard** `GET /api/dashboard`:

```json
{ "total": 6,
  "activeOpen": 2, "activeOpenByStatus": { "draft": 1, "waitingForSupplier": 1, "reviewNeeded": 0, "readyForExport": 0 },
  "activeClosed": 2, "activeClosedByOutcome": { "externalDdsCompleted": 1, "outsideRegulatoryScope": 1, "unsupportedFlow": 0 },
  "archived": 2 }
```
The three buckets are mutually exclusive and cover every case once: an archived case counts only under `archived`, whatever its status. `total = activeOpen + activeClosed + archived`; each breakdown sums to its bucket. `totalOpenCases` no longer exists, and `draft` is now inside `activeOpenByStatus`.

**Working export:** manifest `lifecycle` has the same shape as `GET .../lifecycle`.

## 5. Owner-only behaviour and what the UI can derive

- Close, reopen, archive and unarchive are Owner-only. The backend checks the live membership, so a Member (or a demoted Owner with a still-valid token) gets **403** whatever the case id or body. A UI should hide or disable these actions for non-Owners (use the same Owner role the client already uses for recording a review) but must still handle 403.
- Reads (`GET lifecycle`, details, list, dashboard, exports) are open to every member.
- To offer **Close** the client can use data it already loads: the case is not archived and not `Closed`; `GET .../completeness` has `latestReview` with a `decision` other than `ReturnedForCorrection` and `reviewFreshness` of `Current`; and the review has not already closed a case (no entry in `lifecycle.closures` has `reviewId` equal to `latestReview.reviewId`). Send `expectedReviewId = latestReview.reviewId`; ask for the DDS reference only when the decision is `AcceptedAsCompleteUnderPolicy`, and do not show the field otherwise. Because the backend re-checks all of this, a stale screen produces a 409, never a wrong closure.
- To offer **Reopen**: Owner, `status` is `Closed` and the case is not archived. Send `expectedClosureId = lifecycle.currentClosure.closureId`.
- To offer **Archive**: Owner and not archived (any workflow status, `Closed` included). To offer **Unarchive**: Owner and archived.
- After a reopen the previous review still shows `Current` (if nothing changed) but is **used**; the Owner must record a **new** review before closing again, even when no fact changed. After a change to the dossier the existing freshness rules apply (`Stale`, `changedAreas`).
- `ReadyForExport` must not be presented as the condition for closing.

## 6. Error and status behaviour

There is no error-code enum: match on the HTTP status and, where needed, the stable message text below (ProblemDetails `detail`). The old client matcher for the 409 message (words such as "complete" or "archiv") **no longer matches the Closed message**.

Order of checks for the four writes: live Owner check (**403**) -> unknown or foreign case (**404**) -> state preconditions (**409**) -> body shape (**400**; **415** for a non-JSON content type) -> optimistic and business checks (**409**/**400**).

| Status | When |
| --- | --- |
| 401 | anonymous |
| 403 | Member (also for a nonexistent or foreign case, an invalid body or no body): `Only an organization owner can close a case.` / `reopen a case.` / `archive a case.` / `unarchive a case.` |
| 404 | unknown or foreign case |
| 400 | blank/missing/over-long `reason`, missing or empty `expectedReviewId`/`expectedClosureId`, `externalDdsReference` over 200 characters; DDS reference required (`ExternalDdsCompleted`) or not allowed (other outcomes): `errors.ExternalDdsReference`; malformed JSON; invalid `archived` or `status` filter |
| 415 | a request with a non-JSON content type, or with no body and no Content-Type, that reached the body (the state preconditions passed) |
| 409 | see below |

409 messages (`detail`):
- Closed case written to by any business write: `Closed cases cannot be changed; reopen the case first.`
- Archived case written to by any business write, including `close`/`reopen`/`archive`: `Archived cases cannot be changed; unarchive the case first.`
- `close`: `The case is already closed.`; `No Owner review exists for this case; record a review before closing it.`; `Review history changed since it was loaded` (the id is not the latest review); `This review already backs a closure; record a new review before closing the case again.`; `A review that returned the dossier for correction cannot close the case.`; `The latest review is stale; record a new review of the current dossier before closing the case.` with `errors: { "reason": "StaleReview", "changedAreas": ["Recipients", ...] }`.
- `reopen`: `The case is not closed.`; `The case has no open closure to reopen.`; `Closure history changed since it was loaded`.
- `archive`: `The case is already archived.`. `unarchive`: `The case is not archived.`

Two identical simultaneous requests yield one success and one 409.

## 7. Closed and archived editability

While a case is **Closed** or **archived** these return **409** (no change is recorded): requirement review (`PATCH .../requirements/{id}`), `POST .../supplier-request`, `POST /api/supplier-requests/{id}/regenerate`, internal `POST`/`DELETE .../documents`, `POST .../geolocation`, `PUT .../dossier/lot`, `POST .../master-data/refresh`, `PUT .../applicability`, `PUT .../recipients`, `POST`/`DELETE .../evidence-links`, `POST .../reviews` and `PUT /api/shipments/{id}`; every public supplier route also refuses such a case (a revoked link answers 404 first).

Still available: every read, `GET .../export`, `GET .../reviews/{id}/export`, the completeness report (`reviewFreshness` of the last review stays `Current`), the evidence history, supplier-request **revoke**, and the lifecycle actions that apply (archive a Closed case, reopen a Closed case that is not archived, unarchive an archived case).

A client-side `editable` flag is therefore `status !== "Closed" && archive == null` (or `!lifecycle.archived`).

## 8. Supplier-link behaviour

- `close` and `archive` **revoke** all active supplier requests of the case. The public link then answers 404 (not found), like any revoked link.
- `reopen` and `unarchive` **do not restore** anything. After a reopen/unarchive a member creates a new link with `POST .../supplier-request` (or regenerates a revoked request with `POST /api/supplier-requests/{id}/regenerate`, which is allowed again once the case is editable).
- Revoking a link is always allowed.

## 9. Breaking-change notes

- Status enum: `Completed` renamed to `Closed`; `Archived` removed. Persisted integers of the open statuses are unchanged; JSON uses names.
- Removed routes and fields as in section 2; dashboard and list semantics changed (archived hidden by default).
- 409 message text changed (section 6).
- Legacy local data was converted by migration: a legacy Completed case is **not** Closed any more; it was recalculated to an open status and received a System audit event `LegacyCompletionInvalidatedByMigration` (the old completion time and DDS reference are kept only in that event's metadata, which the case details do not expose). A legacy Archived case is now an archived case with one archive period.

## 10. Required frontend adaptations (contract level)

1. Treat `status` as the five workflow values; drop `Completed`/`Archived` labels, filters and badge styles; show archive as a separate indicator driven by `archive`/`archived`.
2. Replace the "mark completed" action with Close (Owner, review-gated), and add Reopen, Archive and Unarchive (Owner) with reason inputs and the DDS reference only for the `ExternalDdsCompleted` outcome.
3. Read the DDS reference from `closure.externalDdsReference`; stop reading `completedAtUtc` and the case-level reference.
4. Add a lifecycle history view from `GET .../lifecycle` (closures with reviews and reopenings, archive periods).
5. Add an archived view to the case list (`archived=only|include`); update the dashboard to the three buckets.
6. Derive `editable` from `Closed`/archive state; update 409 handling to the new messages; do not rely on the old keyword matching.
7. Remove the unused shipment-archive API call.

## 11. Compatibility risks

- Any client code that switches over the status enum or reads the removed fields will break or show wrong data.
- A case that is archived is missing from the default list; deep links to it still work through details.
- `GET /api/dashboard` consumers see different keys.
- `reviewFreshness` stays `Current` on closed cases (lifecycle state is not part of the fingerprint), so "Current" alone does not mean the case can be closed; the used-review rule applies.

## 12. Out of scope

Frontend design, DDS verification or submission, a release or approval status, Member archive rights, retention and erasure of lifecycle history, post-closure corrective-action workflows, notifications to suppliers and any new roles. The public supplier portal contract itself is unchanged.

## 13. Backend verification performed

The backend gate (`bash scripts/verify.sh`; `pwsh` was not installed) passed for commit `034f73c`: Release build with 0 warnings, 205 unit tests and 236 integration tests (real PostgreSQL via Testcontainers), including `CaseLifecycleTests` (Owner gate, review gate, outcomes, DDS rules, reopen, archive, write blocks, dashboard buckets, concurrency, OpenAPI) and the `CaseLifecycleV2` migration tests. Slice 3 changed documentation only and did not re-run them; its documentation checks are reported in the delivery report, not asserted here.

## 14. Local frontend/backend compatibility evidence

**Not run.** The companion client has not been adapted, so no client suite was executed against this backend. Backend revision at the time of writing: `034f73c` plus the uncommitted Slice 3 documentation. Client revision: not inspected. Command: none. When Slice 4 is done, run the client's live suite (`npm run test:e2e`, see the client's `docs/LOCAL_TESTING.md`) against this exact backend checkout and record both revisions, the dirty state, the command and the result here. This handoff is not a passing compatibility report.
