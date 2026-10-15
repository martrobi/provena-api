# Post-completion Concern & Corrective Action v1: concerns recorded after a case is completed or archived

Status: **backend contract implemented; the client has not adapted yet.** This document is the input for the client's adaptation. Canonical sources are the code, the tests, [API](../API.md) ("Post-completion concerns"), [EUDR_DOMAIN](../EUDR_DOMAIN.md) ("Post-completion Concern & Corrective Action v1") and [ADR 0012](../adr/0012-post-completion-concerns-v1.md). Backend implementation commit: `fcd1c39` (`feat(api): add post-completion concerns`), on top of `f671a63`.

## 1. Summary

- A **concern** records that something relevant to an already **Completed (`Closed`) or archived** case was found out afterwards. It carries its own append-only history: **corrective actions** that were performed, **recipient notifications** that were made (outside Provena), and finally one Owner **resolution**.
- **A concern never reopens the case and changes nothing about it.** Case status, closure, reviews, review freshness, the completeness fingerprint, the External DDS record, quantity reconciliation and supplier links are untouched. Show concern history as a separate section, never as a case status change.
- **Nothing here is a compliance, approval, recall-completion, authority-acceptance or EU-acceptance statement.** Provena sends nothing and contacts no one. `NoFurtherActionRequired` is the Owner's recorded disposition inside Provena, not a legal determination; do not label a resolved concern "approved", "compliant", "recall completed" or "accepted".
- Everything is append-only. There is **no PUT, PATCH or DELETE** (405). A resolved concern is immutable and never reopened; if a new issue appears later, create **another** concern.
- No new breaking change to existing routes or DTOs.

## 2. Endpoints

All routes are authenticated (`401` when anonymous), tenant-scoped, and never carry or return `organizationId`.

| Route | Who | Success |
| --- | --- | --- |
| `GET /api/eudr-cases/{caseId}/concerns` | any member | **200** `ConcernSummaryDto[]`, **newest first** (highest `number` first), unpaginated; `[]` when none |
| `POST /api/eudr-cases/{caseId}/concerns` | any member | **201** `ConcernDto`, `Location` = `GET .../concerns/{concernId}` |
| `GET /api/eudr-cases/{caseId}/concerns/{concernId}` | any member | **200** `ConcernDto` |
| `POST /api/eudr-cases/{caseId}/concerns/{concernId}/actions` | any member | **201** `ConcernActionDto`, `Location` = the concern |
| `POST /api/eudr-cases/{caseId}/concerns/{concernId}/notifications` | any member | **201** `ConcernNotificationDto`, `Location` = the concern |
| `POST /api/eudr-cases/{caseId}/concerns/{concernId}/resolution` | **Owner only** (live check; the JWT role is not trusted) | **201** `ConcernResolutionDto`, `Location` = the concern |

The reads work for a case in any state. After every write, re-read the concern (or the list) to refresh the screen.

## 3. Request bodies (camelCase, case-insensitive; enums as strings)

Create a concern:

```json
{ "category": "NewInformation", "summary": "Supplier reported new information", "details": "A new deforestation alert was reported for the sourcing region.", "raisedAtUtc": "2026-10-16T08:00:00Z" }
```

| Field | Rules |
| --- | --- |
| `category` | Required. `NewInformation`, `SupplierNotification`, `AuthorityNotification`, `InternalFinding` or `Other`. |
| `summary` | Required, trimmed, non-blank, at most 200 characters. |
| `details` | Required, trimmed, non-blank, at most 4000 characters. |
| `raisedAtUtc` | Required ISO 8601 instant (normalised to UTC); **must not be in the future** (no tolerance). The time the concern was raised, not the time it is recorded. |

Record a corrective action:

```json
{ "description": "Quarantined the remaining stock.", "performedAtUtc": "2026-10-16T09:00:00Z", "evidenceDocumentId": null }
```

| Field | Rules |
| --- | --- |
| `description` | Required, trimmed, non-blank, at most 4000 characters. |
| `performedAtUtc` | Required instant; **must not be in the future**. |
| `evidenceDocumentId` | Optional. When given it must be a **current (not withdrawn) document of this same case** at the time of recording. A non-null empty Guid is 400. |

Record a notification (Provena records that someone was notified; it sends nothing):

```json
{ "recipientId": "3b9c...", "channel": "Email", "notifiedAtUtc": "2026-10-16T09:30:00Z", "note": null, "evidenceDocumentId": null }
```

| Field | Rules |
| --- | --- |
| `recipientId` | Required, non-empty. The id of a recipient **of this case**: take it from `recipients[].id` of the case's recipient statements (`GET .../recipients` and `GET .../recipients-history`). Any recipient of any historical statement version of the case is accepted, not only the current statement's. |
| `channel` | Required. `Email`, `Phone`, `Letter`, `Portal` or `Other`. |
| `notifiedAtUtc` | Required instant; **must not be in the future**. |
| `note` | Optional, trimmed, at most 1000 characters; a blank note is stored as null. |
| `evidenceDocumentId` | Optional; same rule as for an action. |

Resolve (Owner only):

```json
{ "outcome": "CorrectiveActionCompleted", "rationale": "All affected parties were informed and the stock was withdrawn." }
```

| Field | Rules |
| --- | --- |
| `outcome` | Required. Only `CorrectiveActionCompleted` or `NoFurtherActionRequired`. Any other value (including the removed `EscalatedExternally`) is 400. |
| `rationale` | Required, trimmed, non-blank, at most 4000 characters. |

There is no `resolvedAtUtc` in the request: the **server** sets it, and a client value is ignored. No request carries `organizationId`, `status` or a concern number.

## 4. Responses

`ConcernDto` (create, detail):

```json
{
  "concernId": "d4e1...", "number": 1, "category": "NewInformation", "summary": "...", "details": "...",
  "raisedAtUtc": "2026-10-16T08:00:00+00:00", "recordedByUserId": "a1b2...", "recordedAtUtc": "2026-10-16T08:05:00+00:00",
  "status": "Open",
  "actions": [ { "actionId": "...", "description": "...", "performedAtUtc": "...", "recordedByUserId": "...", "recordedAtUtc": "...", "evidenceDocumentId": null } ],
  "notifications": [ { "notificationId": "...", "recipient": { "recipientId": "...", "name": "Importer GmbH", "postalAddress": "Hauptstrasse 1, Berlin", "email": "importer@example.test" },
      "channel": "Email", "notifiedAtUtc": "...", "note": null, "evidenceDocumentId": null, "recordedByUserId": "...", "recordedAtUtc": "..." } ],
  "resolution": null
}
```

- `status` is `Open` while `resolution` is null and `Resolved` once it exists. It is derived; there is no field to set it.
- `actions` are ordered by `performedAtUtc`, then by recording time (oldest first); `notifications` by `notifiedAtUtc`, then recording time.
- `notifications[].recipient` is the **stored immutable recipient row** that was named, shown as it was recorded (name, postal address, e-mail). It stays correct even if the case's recipient statement is later replaced by one without that recipient. Treat this data as personal data.
- `resolution` is `ConcernResolutionDto { resolutionId, outcome, rationale, resolvedByUserId, resolvedAtUtc }` or null.

`ConcernSummaryDto` (list item, no `details`): `{ concernId, number, category, summary, raisedAtUtc, status, actionCount, notificationCount, resolvedAtUtc }` (`resolvedAtUtc` is null while open).

`ConcernActionDto { actionId, description, performedAtUtc, recordedByUserId, recordedAtUtc, evidenceDocumentId }`.
`ConcernNotificationDto { notificationId, recipient: { recipientId, name, postalAddress, email }, channel, notifiedAtUtc, note, evidenceDocumentId, recordedByUserId, recordedAtUtc }`.
`ConcernResolutionDto { resolutionId, outcome, rationale, resolvedByUserId, resolvedAtUtc }`.

`recordedAtUtc` is when the backend stored the row; `raisedAtUtc`, `performedAtUtc` and `notifiedAtUtc` are the times the user stated. Show both kinds of time with distinct labels. User ids are ids only (the same convention as the rest of the API).

## 5. Errors and order of checks

Errors are ProblemDetails (`application/problem+json`); a 409 carries `errors.reason`, validation errors are grouped by PascalCase property in `errors` (for example `errors.RaisedAtUtc`). Values are not echoed.

| Status | When |
| --- | --- |
| 401 | anonymous |
| 403 | a **Member** posts a resolution (`Only an organization owner can resolve a concern.`). Raised before anything else, so a Member gets 403 for an existing, unknown or foreign case or concern and for an invalid body alike |
| 404 | unknown or other-organization case; unknown concern, or a concern that belongs to another case |
| 409 `CaseNotCompleted` | creating a concern while the case is neither `Closed` nor archived (an unarchived, never-closed case is open again). Nothing is written |
| 409 `ConcernResolved` | an action, notification or second resolution for an already resolved concern. Nothing is written |
| 409 `EvidenceDocumentNotCurrent` | `evidenceDocumentId` is withdrawn, unknown, from another case or from another organization (identical response for all four) |
| 409 `RecipientNotFound` | `recipientId` is unknown, from another case or from another organization (identical response) |
| 400 | validation: missing or blank required fields, over-long text, undefined enum value, empty Guid, `raisedAtUtc`/`performedAtUtc`/`notifiedAtUtc` in the future; malformed JSON |
| 415 | a body without a JSON `Content-Type` |
| 405 | PUT, PATCH or DELETE on any concern path |

Order inside each write: (resolution only) live Owner check; case lock (404); concern lookup (404); (create only) case-state rule (409); body shape (415/400); validation and clock rules (400); then the state rules (409: resolved, then recipient, then evidence). A 409 does not close or change anything; refresh and show the current state. Simultaneous resolutions by two Owners give one 201 and one `409 ConcernResolved`; an action or notification racing a resolution either lands before it or is refused with `409 ConcernResolved`.

## 6. Case lifecycle interaction

- A concern can be **created only** on a case that is Completed (`status == "Closed"`) or archived (`archive != null` on the case details). Offer "Record a concern" only then; otherwise the backend answers `409 CaseNotCompleted`.
- Creating a concern does **not** reopen the case, change its status or revoke or restore anything. It is not an alternative to the Owner's existing reopen action and must not be presented as one.
- For an **existing** concern, actions and notifications depend only on whether the concern is resolved, not on the case status. Do not hide the concern section or disable existing concern history because the case state changes; v1 defines no reopen behaviour for concerns, and concern history stays readable.
- Existing document, recipient and review routes keep their current closed/archived behaviour. Evidence documents and recipients must already exist on the case; a closed or archived case cannot receive new ones through the normal routes, so offer only what the case already has (current documents from `GET /api/eudr-cases/{id}` `documents`; recipients from the recipient statement routes).

## 7. Authorization

Any authenticated organization member may read concerns, create a concern, record an action and record a notification. **Only an Owner may resolve**, using the same live Owner check as the other Owner-only APIs (close, reopen, archive, unarchive, reviews, External DDS). Show the resolve control to Owners only, but still handle `403` (a demoted Owner, a stale session). No new role exists.

## 8. Required client adaptation

On Completed or archived cases, add a distinct "Post-completion concerns" area that is clearly separate from the original completed-case history (status, closure, reviews) and from the External DDS record:

- a **concern list** (newest first) with category, summary, `raisedAtUtc`, a derived Open/Resolved indicator and action/notification counts;
- a **concern detail** with the full text, the chronological actions and notifications (each with its optional evidence document, which stays reachable even after the document is later withdrawn, following the existing document-download behaviour) and the resolution when present;
- **create concern**, **record corrective action** and **record recipient notification** forms (member level), with optional evidence selection limited to current documents of the case, and recipient selection from the case's recipient statements including historical statement versions;
- an **Owner-only resolve** form (outcome, rationale; the time is the server's);
- a **read-only** view of a resolved concern: no action, notification or resolve controls, no edit or delete anywhere.

Copy rules: do not call a concern "reopening"; do not call a resolution "approved", "compliant", "recall completed" or "accepted"; if a new issue appears after resolution, the UI creates another concern (offer that, never "reopen concern"). External escalation is recorded as an action and/or notification and does not resolve the concern. Do not build a client policy engine: the backend decides which writes are allowed; map the 409 reasons to messages and refresh.

## 9. Backend acceptance criteria (already implemented and tested)

- Concern creation: 201 for a Completed case and for an archived case, 409 `CaseNotCompleted` for an open case; any member may create; another organization gets 404; future `raisedAtUtc` is 400; the case status, review freshness, completeness fingerprint, closure, External DDS record and supplier links are unchanged by a full concern lifecycle.
- Actions and notifications: any member may record; future times are 400; current same-case evidence may be linked; unknown, foreign, other-case and withdrawn documents are refused alike; a later withdrawal keeps the link and the read; the recipient (own case, any statement version) stays reconstructable after the statement is superseded; refused with 409 once resolved.
- Resolution: Member 403, Owner 201; rationale required; exactly one resolution; second resolution 409; concurrent double resolution gives one 201 and one 409 with one row; `resolvedAtUtc` is set by the server; all facts stay readable afterwards; a second concern can be opened on the same case.
- Migration `20261015224634_PostCompletionConcerns`: additive; Down refused while any of the four tables has a row (rows survive), succeeds when all are empty.
- Verification at implementation: 231 unit and 291 integration tests passed. The local client compatibility suite has not been run.

## 10. Deferred (do not build or imply)

Reopening a case or a concern; a recall workflow; automatic notification or delivery to downstream recipients; an authority or EU Information System connector or any DDS amendment/withdrawal; cross-case incidents and supplier-wide or product-wide impact discovery; holds on future shipments or automatic blocking; affected-quantity subsets and multi-lot scope; assignees, due dates, priorities or action statuses; editing, superseding or deleting any concern fact; cross-case concern search or pagination.
