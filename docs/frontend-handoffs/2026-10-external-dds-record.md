# External DDS Record v1: a structured, retained record of a submission made outside Provena

Status: **backend contract implemented; the client has not adapted yet.** This document is the input for the client's adaptation. Canonical sources are the code, the tests, [API](../API.md) ("External DDS records", "Case lifecycle"), [EUDR_DOMAIN](../EUDR_DOMAIN.md) ("External DDS Record v1") and [ADR 0011](../adr/0011-external-dds-record-v1.md). Backend implementation commit: `5644224` (`feat(api): add external DDS records`), on top of `a06df65`.

## 1. Summary

- A new retained record, `ExternalDdsRecord`, states that the dossier version captured by one immutable Owner review was submitted to the EU Information System **outside Provena**. Provena makes no network call and verifies nothing.
- **It records an external fact. It does not mean EU approval, regulatory approval, compliance, or verification by Provena.** Do not label it "approved", "accepted", "verified" or "compliant" anywhere in the UI. The reference number and the verification number are not proof of acceptance.
- Three routes: read the current record, read the full history, and record a first or replacement record. Records are append-only: a correction creates a new record that supersedes the previous one. A replacement is a correction of **Provena's recorded fact**; it is **not** an amendment in the EU system.
- **Breaking for the close flow:** closing as `ExternalDdsCompleted` now needs a current External DDS record tied to the latest Current accepted review. The free-text `externalDdsReference` on the close request is no longer authoritative and cannot close a case by itself.
- `verificationNumber` is sensitive (access-enabling). It is returned **only** by the External DDS endpoints.

## 2. Changed endpoints and behaviour

| Route | Who | Success |
| --- | --- | --- |
| `GET /api/eudr-cases/{caseId}/external-dds` | any authenticated member of the tenant | **200** `ExternalDdsRecordDto` (the current, non-superseded record) or **204** with no body when none exists |
| `GET /api/eudr-cases/{caseId}/external-dds-history` | any authenticated member | **200** `ExternalDdsRecordDto[]`, **newest first** (highest `number` first), all versions; `[]` when none |
| `POST /api/eudr-cases/{caseId}/external-dds` | **Owner only** (live check; the JWT role is not trusted) | **201** `ExternalDdsRecordDto`, `Location` = `GET .../external-dds` |
| `POST /api/eudr-cases/{caseId}/close` | Owner | Contract unchanged in shape; new 409 reasons for `ExternalDdsCompleted` (section 6) and `externalDdsReference` is now optional there |

There is **no PUT, PATCH or DELETE** on these paths (405). History is unpaginated (pilot scale). No response contains `organizationId`.

## 3. Request contract (POST)

```json
{
  "expectedLatestExternalDdsRecordId": null,
  "reviewId": "7c1f...",
  "referenceNumber": "EU-REF-2026-0001",
  "verificationNumber": "AB12CD34",
  "submittedAtUtc": "2026-10-15T09:30:00Z",
  "receiptDocumentId": "5d3a...",
  "replacementReason": null
}
```

| Field | Rules |
| --- | --- |
| `expectedLatestExternalDdsRecordId` | `null` or omitted for the **first** record; the **exact id of the current record** (`recordId` you loaded) for a replacement. A mismatch is 409 `ExternalDdsHistoryChanged`. A non-null empty Guid is 400. |
| `reviewId` | Required, non-empty. Must be the **latest** review of this case, with decision `AcceptedAsCompleteUnderPolicy`, and **Current** (not stale) for the present dossier. Take it from `GET .../completeness` `latestReview.reviewId`. |
| `referenceNumber` | Required, trimmed, non-blank, at most 200 characters. |
| `verificationNumber` | Required, trimmed, non-blank, at most 200 characters. A **separate** value from the reference number; never interchangeable. |
| `submittedAtUtc` | Required ISO 8601 instant (any offset; it is normalised to UTC). **Must not be in the future** and **must be at or after the referenced review's `reviewedAtUtc`** (equality is valid; there is no tolerance). The review time is available as `latestReview.reviewedAtUtc` on the completeness response and on the review read routes. |
| `receiptDocumentId` | Optional. When given it must be a **current (not withdrawn) document of this same case** at the time of recording. |
| `replacementReason` | Required and non-blank (max 4000 after trimming) when replacing; **must be omitted/null for the first record** (400 otherwise). |

The request never carries `organizationId`. Property names are case-insensitive camelCase.

## 4. Response contract

`ExternalDdsRecordDto` (camelCase, ISO 8601 timestamps):

```json
{
  "recordId": "a91e...",
  "number": 1,
  "reviewId": "7c1f...",
  "referenceNumber": "EU-REF-2026-0001",
  "verificationNumber": "AB12CD34",
  "submittedAtUtc": "2026-10-15T09:30:00+00:00",
  "recordedByUserId": "0b6f...",
  "recordedAtUtc": "2026-10-15T09:41:12.345+00:00",
  "receiptDocumentId": "5d3a...",
  "supersedesRecordId": null,
  "replacementReason": null,
  "supersededAtUtc": null,
  "isCurrent": true
}
```

- `number` is the per-case sequence (1, 2, ...). `isCurrent` is true exactly for the one record with `supersededAtUtc: null`.
- In the history, a superseded row keeps every original value and has `supersededAtUtc` set; the newer row has `supersedesRecordId` pointing at it and carries the `replacementReason`.
- `receiptDocumentId` is a historical link: if the document is withdrawn later the record and the link stay. Resolve the document through the existing document routes and tolerate a withdrawn one.
- No other DTO (case details, list, lifecycle, completeness, reviews, exports) contains the verification number or an External DDS record.

## 5. What the UI can derive

- **Current panel:** `GET .../external-dds`; 204 means "no external submission recorded". Show `referenceNumber`, `submittedAtUtc`, `recordedByUserId`/`recordedAtUtc`, the review it refers to and the receipt link.
- **History:** `GET .../external-dds-history`, already newest first. Mark the row with `isCurrent`; show `replacementReason` on replacement rows.
- **Offer "Record external submission" (Owner):** the case is not `Closed` and not archived (`GET .../lifecycle`: `status`, `archived`), `latestReview` exists with `decision = AcceptedAsCompleteUnderPolicy` and `reviewFreshness = Current`. Offer "Replace" instead when a current record exists, passing its `recordId` as `expectedLatestExternalDdsRecordId`. The server re-checks everything; these are conveniences, not a policy engine.
- A record whose `reviewId` is no longer the latest review (a newer review was recorded) is historical for closing: closing needs a new review and a new record.
- Quantity reconciliation does **not** gate recording; it still gates closing (see section 6).

## 6. Error and status behaviour

All errors are `application/problem+json` with `status`, `title`, `detail`, `traceId` and, where noted, `errors`. `errors.reason` strings are stable machine identifiers; `detail` is descriptive English text, not an identifier.

### POST `.../external-dds` (in this order)

| Status | When | `errors` |
| --- | --- | --- |
| 401 | anonymous | |
| 403 | a Member, for **any** case id (existing, nonexistent, foreign) and any body, valid or not | |
| 404 | unknown or foreign case (Owner) | |
| 409 | case `Closed`: `Closed cases cannot be changed; reopen the case first.`; archived: `Archived cases cannot be changed; unarchive the case first.` | none |
| 415 | non-JSON content type (after the state checks) | |
| 400 | malformed JSON | |
| 400 | validation: missing/empty `reviewId`, blank or over-long numbers, missing `submittedAtUtc`, empty-Guid optional ids, `replacementReason` over 4000 | `errors.ReviewId`, `ReferenceNumber`, `VerificationNumber`, `SubmittedAtUtc`, `ExpectedLatestExternalDdsRecordId`, `ReceiptDocumentId`, `ReplacementReason` (arrays of messages) |
| 400 | `submittedAtUtc` in the future | `errors.SubmittedAtUtc` |
| 409 | `expectedLatestExternalDdsRecordId` is not the current record id (including `null` while a record exists, an unknown id, or a stale id) | `{ reason: "ExternalDdsHistoryChanged" }` |
| 400 | replacement without a non-blank `replacementReason`; or a `replacementReason` on the first record | `errors.ReplacementReason` |
| 409 | `reviewId` is not the latest review of this case. An unknown, foreign or absent review gives the **identical, non-disclosing** response | `{ reason: "ReviewNotLatest" }` |
| 409 | the latest review's decision is not `AcceptedAsCompleteUnderPolicy` | `{ reason: "ReviewNotAccepted" }` |
| 400 | `submittedAtUtc` is earlier than the referenced review's `reviewedAtUtc` | `errors.SubmittedAtUtc` |
| 409 | the latest review is stale for the present dossier | `{ reason: "StaleReview", changedAreas: [...] }` (same section names as `latestReview.changedAreas`) |
| 409 | `receiptDocumentId` is not a current document of this case. Withdrawn, unknown, other-case and other-tenant ids give the **identical, non-disclosing** response | `{ reason: "ReceiptDocumentNotCurrent" }` |

A rejected POST writes nothing: no row, no supersession of the previous record, no audit event. In particular a failed replacement leaves the previous record current. Two simultaneous first records give one 201 and one 409 `ExternalDdsHistoryChanged`.

The GET routes answer 401 (anonymous) and 404 (unknown or foreign case). A Member may read.

### POST `.../close` as `ExternalDdsCompleted`

The outcome is derived from the review decision (`AcceptedAsCompleteUnderPolicy` -> `ExternalDdsCompleted`); the client never sends it. **Precedence inside close, unchanged up to the review checks:** archived (409) -> already closed (409) -> body shape (400) -> no review / `expectedReviewId` is not the latest review / the review already backs a closure / `ReturnedForCorrection` (409) -> a legacy reference sent for a **non-DDS** outcome (400) -> **1. `StaleReview`** (409) -> **2. `QuantityNotReconciled`** (409, ADR 0010) -> **3. the External DDS gate** (409):

| `errors.reason` | When |
| --- | --- |
| `ExternalDdsRecordMissing` | no current External DDS record exists |
| `ExternalDdsReviewMismatch` | the current record points at a review other than the latest Current one |
| `ExternalDdsReferenceMismatch` | `externalDdsReference` was supplied, and after trimming it differs (exact, case-sensitive) from the current record's `referenceNumber` |

```json
{
  "status": 409, "title": "Request failed",
  "detail": "The recorded external DDS submission belongs to a different review than the latest one; record the submission of the latest review.",
  "traceId": "...",
  "errors": { "reason": "ExternalDdsReviewMismatch",
              "externalDds": { "recordId": "a91e...", "reviewId": "3e02...", "referenceNumber": "EU-REF-2026-0001", "submittedAtUtc": "2026-10-15T09:30:00+00:00" } }
}
```

`errors.externalDds` is `null` for `ExternalDdsRecordMissing`. It **never** contains the verification number. A failed close writes no closure and no audit event, changes no status and does not revoke the supplier links. `StaleReview` and `QuantityNotReconciled` always win over every External DDS reason. Success is unchanged: **201** `CaseClosureDto`, `Location` = `GET .../lifecycle`.

**Other outcomes are unaffected.** `OutsideRegulatoryScope` and `UnsupportedFlow` need no External DDS record and still reject a supplied `externalDdsReference` (400 `errors.ExternalDdsReference`). `ReturnedForCorrection` cannot close.

### Legacy `externalDdsReference` on close (precise)

- Still accepted (trimmed, at most 200 characters; blank means absent). **No longer required** for `ExternalDdsCompleted`.
- **Not authoritative:** it cannot authorise `ExternalDdsCompleted` by itself. The current record is the source of truth.
- If supplied for `ExternalDdsCompleted` it must match the current record's `referenceNumber`; otherwise `ExternalDdsReferenceMismatch`.
- A successful close stores the record's `referenceNumber` in `CaseClosure.ExternalDdsReference`, exposed as `closure.externalDdsReference` (shape unchanged). `EudrCase` has **no** `externalDdsReference` property (removed by Case Lifecycle v2) and none is reintroduced.

## 7. Closed and archived cases

Both reads keep working. Recording and replacing are refused with 409 (section 6). There is no reopen behaviour added here and post-closure corrective action is a separate future slice.

## 8. Sensitive handling of `verificationNumber`

- It is access-enabling data. It appears **only** in the three External DDS endpoints, for authenticated tenant members.
- It must not appear in generic case DTOs, lifecycle DTOs, completeness, reviews, exports, ProblemDetails or audit text; the backend guarantees this for its own responses, and the client must not copy it into other views, URLs, query strings, analytics, error reports or browser logs.
- Do not log request or response bodies of the External DDS routes. Do not put it in telemetry. Do not display it by default in lists and summaries; show it only in the dedicated detail view (a mask-with-reveal control is a UI choice).
- The audit events written are `ExternalDdsRecorded` and `ExternalDdsReplaced`; they carry ids only.

## 9. Breaking-change notes

- A close of an `AcceptedAsCompleteUnderPolicy` review that worked with only `externalDdsReference` now returns 409 `ExternalDdsRecordMissing`.
- A client that always sends a free-text reference must either stop sending it or send exactly the recorded `referenceNumber`.
- New 409 reasons on close must be recognised: `ExternalDdsRecordMissing`, `ExternalDdsReviewMismatch`, `ExternalDdsReferenceMismatch`.
- 400 for a missing `externalDdsReference` on `ExternalDdsCompleted` no longer occurs.

## 10. Required frontend adaptations (contract level)

1. Add a **current External DDS panel** on the case (204 = none recorded).
2. Add the **history** view (newest first, `isCurrent`, replacement reasons, historical receipt link).
3. Add an **Owner-only "record external submission" action** with the fields in section 3; hide or disable it for Members and handle 403 anyway.
4. Add a **replacement flow** that sends the current `recordId` as `expectedLatestExternalDdsRecordId` and requires a replacement reason; for the first record send `null`. Reload and let the user retry on `ExternalDdsHistoryChanged`.
5. Offer **receipt-document selection** from the case's current documents; tolerate a receipt that was withdrawn later.
6. Treat `verificationNumber` as sensitive (section 8): no generic display, no logging.
7. Show **structured validation and conflict messages**: map `errors.<Field>` for 400 and `errors.reason` for the 409 set (`ExternalDdsHistoryChanged`, `ReviewNotLatest`, `ReviewNotAccepted`, `StaleReview` with `changedAreas`, `ReceiptDocumentNotCurrent`), and the close-gate reasons.
8. Base the **`ExternalDdsCompleted` close flow on the structured record**: record the submission first, then close; stop using the free-text `externalDdsReference` as the primary close input (omit it, or send the record's reference).
9. Use neutral wording: "External submission recorded", never "approved", "accepted by EU", "verified" or "compliant"; label replacement as "correction of the recorded submission", not as an EU amendment.

No frontend policy engine is prescribed: the backend re-checks every rule.

## 11. Implemented, deferred and out of scope

**Already implemented backend:** the three routes and DTOs above; Owner-only recording under the case lock with optimistic concurrency; append-only retention with a supersede chain; exact review linkage with latest/Current/accepted checks and the `submittedAtUtc` chronology; optional receipt-document relation; the `ExternalDdsCompleted` close gate with its three reasons; the legacy-reference compatibility rules; audit events; migration `20261015203340_ExternalDdsRecord`.

**Required frontend adaptation:** section 10.

**Deferred (not implemented, do not simulate):** an EU Information System connector, credentials, automatic submission, status polling, official amendment/withdrawal/rejection lifecycle, outage/contingency handling, grouped DDS, incoming supplier DDS references, simplified declarations, post-completion corrective action and any reopen behaviour tied to records, multi-lot coverage.

## 12. Backend acceptance criteria

1. `GET .../external-dds` is 204 before any record, then 200 with the current record; the history lists every version newest first.
2. First `POST` with `expectedLatestExternalDdsRecordId: null` is 201; a replacement with the exact current id and a reason is 201, keeps the old row (superseded) and makes the new one current; exactly one row per case is current.
3. Wrong, null-while-existing, unknown or stale expected id is 409 `ExternalDdsHistoryChanged` with no side effects; a replacement without a reason, or a reason on the first record, is 400.
4. A Member gets 403 on POST whatever the case or body; both reads are open to members; foreign cases are 404.
5. Non-latest/unknown/foreign review is 409 `ReviewNotLatest`; non-accepted decision 409 `ReviewNotAccepted`; stale review 409 `StaleReview`; only a Current `AcceptedAsCompleteUnderPolicy` latest review succeeds. Mismatching quantities do not block recording.
6. `submittedAtUtc` in the future or before `reviewedAtUtc` is 400 `errors.SubmittedAtUtc` and writes nothing; equality with `reviewedAtUtc` is accepted.
7. A non-current or foreign receipt document is 409 `ReceiptDocumentNotCurrent`; a later withdrawal keeps the record and link.
8. Closed and archived cases keep the reads and refuse POST with 409.
9. Closing as `ExternalDdsCompleted` follows stale -> quantity -> External DDS gate, returns the three reasons, writes nothing on failure, and on success copies the record's reference number to the closure; the other outcomes do not need a record.
10. The verification number never appears in ProblemDetails, audit text or any non-External-DDS DTO.

## 13. Frontend acceptance criteria (next slice)

1. The panel shows "none recorded" on 204 and the record on 200 without requiring any field other than those in section 4.
2. History renders newest first and distinguishes the current record from superseded ones with their reasons.
3. The record and replace actions are offered only to Owners on open, non-archived cases with a Current accepted latest review, and send exactly the field set in section 3 (no `organizationId`).
4. A replacement sends the loaded `recordId`; an `ExternalDdsHistoryChanged` response reloads and asks the user to retry.
5. Each documented 400/409 reason is shown as a specific message; unknown reasons fall back to `detail`.
6. The verification number is not shown in lists, not logged and not in URLs or telemetry.
7. The `ExternalDdsCompleted` close action requires a current record and surfaces the three close reasons and `QuantityNotReconciled`; it does not send a free-text reference of its own.
8. UI wording never claims approval, verification or compliance.
9. The cross-repository browser suite is run against this backend revision (section 15).

## 14. Compatibility risks

- A client that closes with free text only will start failing with 409 (section 9).
- A client that treats `submittedAtUtc` as "now" must send a timestamp at or after the review time; a clock-skewed client can hit the future-time 400.
- `detail` strings are descriptive English text, not identifiers; use `errors.reason` and `errors.<Field>`.
- `submittedAtUtc` is returned with a UTC offset (`+00:00`), not necessarily the offset that was sent.
- Rolling the migration back is refused while any External DDS record exists (guarded `Down`).

## 15. Backend verification performed and local compatibility evidence

- `bash scripts/verify.sh` (`pwsh` is not installed on this machine) on the implementation commit `5644224`: Release build with warnings as errors, **227 unit and 266 integration tests passed** (PostgreSQL via Testcontainers). This documentation slice changed no behaviour.
- **Local frontend/backend compatibility: not run.** The companion client has not adapted to this contract and the cross-repository browser suite described in [CHANGE_WORKFLOW](../CHANGE_WORKFLOW.md) was not executed against this checkout. When it is, record both repository revisions (with dirty state), the environment, the command and the result here.
