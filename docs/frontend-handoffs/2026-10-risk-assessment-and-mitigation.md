# Risk Assessment & Mitigation v1: a separate, retained risk decision before External DDS closure

Status: **backend contract implemented; the client has not adapted yet.** This document is the input for the client's adaptation. Canonical sources are the code (`Contracts/Risk.cs`, `Services/RiskAssessmentService.cs`), the tests, [API](../API.md) ("Risk assessment and mitigation", "Case lifecycle"), [EUDR_DOMAIN](../EUDR_DOMAIN.md) ("Risk Assessment & Mitigation v1") and [ADR 0013](../adr/0013-risk-assessment-and-mitigation-v1.md). Backend implementation commit: `e0836e0d338b539f1da761259188f7412c72606c` (`e0836e0`, `feat(api): add risk assessment and mitigation`), on top of `9ac0f08`.

## 1. Summary

- Two new retained, append-only records: a **risk assessment** (an Owner's conclusion `Negligible` or `NotNegligible` on one exact review, with a structured finding per criterion of a versioned catalogue and a rationale) and a **mitigation measure** (something a member already performed in response to a `NotNegligible` assessment).
- **Completeness accepted is not Negligible risk.** `AcceptedAsCompleteUnderPolicy` means the facts satisfy the implemented completeness policy. **Negligible is an internal Provena risk conclusion recorded by a person; it is not regulatory approval and not a general compliance certificate.** The catalogue and the conclusion support the operator's documented human process and are not an automated legal determination. Provena computes no score and no conclusion.
- Four routes. There is no PUT, PATCH or DELETE.
- **Breaking for the close flow:** closing as `ExternalDdsCompleted` now also needs the latest risk assessment to rest on the latest review and be `Negligible`. A `NotNegligible` conclusion blocks `ExternalDdsCompleted`. The other outcomes (`OutsideRegulatoryScope`, `UnsupportedFlow`) and recording the External DDS record itself have no risk gate.
- `CaseClosureDto` gains `riskAssessmentId`; the existing "review already backs a closure" conflict becomes machine-readable as `ReviewAlreadyBacksClosure`.
- Wording: use "risk assessment recorded", "Negligible risk conclusion", "NotNegligible risk conclusion", "internal decision recorded in Provena". Never "compliant", "approved", "regulator approved", "legally safe" or "risk free".

## 2. Changed endpoints and behaviour

| Route | Who | Success |
| --- | --- | --- |
| `GET /api/eudr-cases/{caseId}/risk` | any authenticated member | **200** `RiskOverviewDto` |
| `GET /api/eudr-cases/{caseId}/risk-assessments/{assessmentId}` | any authenticated member | **200** `RiskAssessmentDto` (404 for an unknown/foreign case or assessment) |
| `POST /api/eudr-cases/{caseId}/risk-assessments` | **Owner only** (live check; the JWT role is not trusted; before case lookup and before the body is read) | **201** `RiskAssessmentDto`, `Location` = `GET .../risk-assessments/{assessmentId}` |
| `POST /api/eudr-cases/{caseId}/risk-mitigations` | any authenticated member | **201** `RiskMitigationDto`, `Location` = `GET .../risk` |
| `POST /api/eudr-cases/{caseId}/close` | Owner | Shape unchanged; `CaseClosureDto.riskAssessmentId` added; new 409 reasons for `ExternalDdsCompleted` (section 7) |

No new role exists. Supplier and public routes expose no risk data. No request or response contains `organizationId`. Histories are unpaginated (pilot scale). A closed or archived case keeps both GETs and refuses both POSTs with 409.

## 3. Request contract

Property names are camelCase and case-insensitive. Enums are strings (integers are rejected).

### POST `/risk-assessments`

```json
{
  "reviewId": "7c1f...",
  "expectedLatestRiskAssessmentId": null,
  "expectedLatestMitigationMeasureId": null,
  "criteriaCatalogueVersion": "1",
  "conclusion": "NotNegligible",
  "rationale": "Why the Owner reached this conclusion.",
  "findings": [
    { "criterionCode": "PRODUCTION_COUNTRY_AND_AREA", "finding": "Concern", "note": "Plot lies in an area with recent clearing reports." },
    { "criterionCode": "EVIDENCE_RELIABILITY", "finding": "NoConcern", "note": null }
  ]
}
```

| Field | Rules |
| --- | --- |
| `reviewId` | Required, non-empty. Must be the **latest** review of the case (409 `ReviewNotLatest`), must not already back a closure (409 `ReviewAlreadyBacksClosure`), must have decision `AcceptedAsCompleteUnderPolicy` (409 `ReviewNotAccepted`) and be Current (409 `StaleReview` with `changedAreas`). Take it from `RiskOverviewDto.latestReviewId`. |
| `expectedLatestRiskAssessmentId` | `null`/omitted when no assessment existed; otherwise the exact `assessmentId` of the latest assessment you loaded. A non-null empty Guid is 400. Mismatch is 409 `RiskAssessmentHistoryChanged`. |
| `expectedLatestMitigationMeasureId` | `null`/omitted when no mitigation existed; otherwise the exact `mitigationId` of the latest (highest `number`) measure you loaded. A non-null empty Guid is 400. Mismatch is 409 `MitigationHistoryChanged`. |
| `criteriaCatalogueVersion` | Required, at most 50 characters. Must equal the current catalogue version (`catalogue.version`), else 409 `RiskCriteriaCatalogueChanged`. |
| `conclusion` | Required: `Negligible` or `NotNegligible`. |
| `rationale` | Required, trimmed, non-blank, at most 4000 characters. |
| `findings` | Required list: **every catalogue criterion exactly once**; unknown or duplicate codes are 400. Each item: `criterionCode` (required), `finding` (required: `NoConcern`, `Concern` or `InsufficientInformation`), `note` (at most 2000 characters; a blank note is treated as null). `Concern` and `InsufficientInformation` **require a nonblank note**. A `Negligible` conclusion together with any `InsufficientInformation` finding is 400 (`errors.Conclusion`). `Concern` with `Negligible` is **valid**. Validation messages never echo your values. |

### POST `/risk-mitigations`

```json
{
  "expectedRiskAssessmentId": "a91e...",
  "description": "Requested and received a land-title extract from the supplier.",
  "performedAtUtc": "2026-10-16T08:00:00Z",
  "evidenceDocumentId": "5d3a..."
}
```

| Field | Rules |
| --- | --- |
| `expectedRiskAssessmentId` | Required, non-empty: the `assessmentId` of the latest assessment the measure answers. Not the latest, or none exists: 409 `RiskAssessmentChanged`. |
| `description` | Required, trimmed, non-blank, at most 4000 characters. |
| `performedAtUtc` | Required ISO 8601 instant (normalised to UTC). **Must not be in the future** (400 `errors.PerformedAtUtc`). |
| `evidenceDocumentId` | Optional (`null`/omitted); a non-null empty Guid is 400. Must be a **current (not withdrawn) document of this case** at the time of recording, else 409 `EvidenceDocumentNotCurrent`. |

A measure is allowed only while the latest assessment is `NotNegligible` (409 `MitigationNotExpected` otherwise).

## 4. Response contract

camelCase, enums as strings, ISO 8601 timestamps with a UTC offset.

`RiskOverviewDto` (`GET .../risk`):

```json
{
  "catalogue": {
    "id": "provena-pilot-risk-criteria",
    "version": "1",
    "criteria": [ { "code": "PRODUCTION_COUNTRY_AND_AREA", "title": "Production country and area", "guidance": "..." } ]
  },
  "freshness": "Current",
  "staleReason": null,
  "changedAreas": [],
  "latestReviewId": "7c1f...",
  "latestReviewBacksClosure": false,
  "latestAssessment": { "...": "RiskAssessmentDto or null" },
  "assessments": [ "RiskAssessmentDto, newest first (highest number first)" ],
  "mitigations": [ "RiskMitigationDto, newest first (highest number first)" ]
}
```

| Field | Meaning |
| --- | --- |
| `catalogue` | `{ id, version, criteria[] { code, title, guidance } }`, the current catalogue (always present, also when no assessment exists). |
| `freshness` | `None` (no assessment), `Current` (the latest assessment rests on the latest review and that review is Current) or `Stale`. Derived, never stored. |
| `staleReason` | `null` unless `Stale`: `ReviewSuperseded` (a newer review exists) or `ReviewStale` (the review's facts changed). |
| `changedAreas` | `[]` unless `staleReason` is `ReviewStale`; then the review's own changed-area names (same vocabulary as the review/completeness freshness). |
| `latestReviewId` | Id of the latest review of the case; `null` when the case has no review. |
| `latestReviewBacksClosure` | `true` when the latest review already backs a closure (after a reopen a new review is required). |
| `latestAssessment` | The newest `RiskAssessmentDto`, or `null` when none exists. |
| `assessments` | Full assessment history, newest first. |
| `mitigations` | Full mitigation history, newest first. |

> The overview carries the latest assessment as the object `latestAssessment`; there is **no** `latestAssessmentId` property on `RiskOverviewDto` (read `latestAssessment.assessmentId`). The working export's `risk` section does carry `latestAssessmentId` (section 9).

`RiskAssessmentDto`:

```json
{
  "assessmentId": "a91e...",
  "number": 1,
  "reviewId": "7c1f...",
  "reviewRevisionNumber": 3,
  "criteriaCatalogueId": "provena-pilot-risk-criteria",
  "criteriaCatalogueVersion": "1",
  "conclusion": "NotNegligible",
  "rationale": "...",
  "findings": [ { "criterionCode": "EVIDENCE_RELIABILITY", "finding": "NoConcern", "note": null } ],
  "consideredMitigationThroughNumber": null,
  "assessedByUserId": "2b6d...",
  "assessedAtUtc": "2026-10-16T08:05:00+00:00"
}
```

`findings` are ordered by `criterionCode` (ordinal). `consideredMitigationThroughNumber` is `null` when no measure existed at assessment time. `note` is `null` for most `NoConcern` findings.

`RiskMitigationDto`:

```json
{
  "mitigationId": "c03b...",
  "number": 1,
  "riskAssessmentId": "a91e...",
  "description": "...",
  "performedAtUtc": "2026-10-16T08:00:00+00:00",
  "evidenceDocumentId": null,
  "recordedByUserId": "2b6d...",
  "recordedAtUtc": "2026-10-16T08:10:00+00:00"
}
```

`CaseClosureDto` (lifecycle responses, close response) now includes `riskAssessmentId` (nullable Guid): the exact assessment that backed an `ExternalDdsCompleted` closure; `null` for legacy closures and for the other outcomes. The closure also keeps `reviewId`; the database guarantees the assessment was made on that same review.

### Meaning of `consideredMitigationThroughNumber`

It proves only that mitigation records 1..N existed when the assessment was recorded (under the case lock and the expected-id contract). It does **not** prove that the Owner read them, their relevance, effectiveness or sufficiency, or that a measure mitigated a particular criterion. No conclusion is derived from it. Do not display it as "mitigated" or "addressed"; show it as "assessed with mitigation records up to #N existing".

## 5. Semantics the UI must respect

1. **Completeness accepted != Negligible risk. Negligible != regulatory approval.** Never merge the review badge, the risk conclusion, the External DDS record and the case completion into one status or one progress bar.
2. `NotNegligible` blocks `ExternalDdsCompleted`. Reassessment (a new assessment, possibly after mitigation) is the way forward; a later `Negligible` assessment on a Current review is the recorded residual-risk decision. There is no separate residual-risk status.
3. **`Concern` with `Negligible` is valid** (the Owner explains it in the note and rationale). **`InsufficientInformation` with `Negligible` must not be offered** (the backend rejects it with 400). Do not build a frontend scoring or policy engine; the backend remains authoritative and re-checks every rule.
4. A mitigation measure is a **record of something already performed**, not a task: no assignee, due date, priority, reminder, status, Kanban or work queue. A correction is another measure; nothing can be edited or deleted.
5. Mitigation can be recorded by **any member**; the assessment only by an **Owner** (also expect 403 for a demoted Owner).
6. Freshness is derived from the review only (there is no second risk fingerprint). A newly uploaded mitigation evidence document is a normal case document and **can stale the current review**. Then the normal path is: mitigation/evidence -> **new review** -> **new risk assessment**. If only already-reviewed evidence is used, a reassessment may stay on the same Current review when all other preconditions hold. Do not hide the staleness by silently refetching or resubmitting; show it and let the user act.
7. After a reopen the old review may still be latest and Current, but `latestReviewBacksClosure` is `true`: a new assessment on it, and closing on it, fail `ReviewAlreadyBacksClosure`. The user must record a **new review**, then a **new assessment**, then pass the quantity/risk/DDS gates and close again. Risk never creates or triggers a reopen.
8. No new conclusion is possible on a closed or archived case (409); the history stays readable.

## 6. Optimistic concurrency

| Write | Expected ids sent | Mismatch |
| --- | --- | --- |
| Create assessment | `expectedLatestRiskAssessmentId` (latest assessment, `null` if none) and `expectedLatestMitigationMeasureId` (latest measure, `null` if none) | 409 `RiskAssessmentHistoryChanged` / `MitigationHistoryChanged` |
| Record mitigation | `expectedRiskAssessmentId` (the latest assessment) | 409 `RiskAssessmentChanged` |

The client must **not** silently retry or refetch-and-resubmit. On any of these reasons: refresh the risk data, show what changed, and let the user consciously decide to retry.

## 7. Error contract

Errors are RFC 7807 ProblemDetails. Use **`errors.reason`** for 409s and **`errors.<Property>`** for 400s; never match `detail` text (it is descriptive English and may change). ProblemDetails never contains the rationale, finding notes, mitigation description or an External DDS verification number, and no `organizationId`.

| Route | Status | `errors.reason` | Notes |
| --- | --- | --- | --- |
| POST assessment | 403 | (none) | Member, or a demoted Owner; before any case lookup or body read |
| POST assessment | 409 | `RiskCriteriaCatalogueChanged` | carries `currentVersion`; reload the catalogue |
| POST assessment | 409 | `RiskAssessmentHistoryChanged` | |
| POST assessment | 409 | `MitigationHistoryChanged` | |
| POST assessment | 409 | `ReviewNotLatest` | also when the case has no review |
| POST assessment | 409 | `ReviewAlreadyBacksClosure` | typed; detail text "This review already backs a closure; record a new review before closing the case again." |
| POST assessment | 409 | `ReviewNotAccepted` | only `AcceptedAsCompleteUnderPolicy` reviews can be assessed |
| POST assessment | 409 | `StaleReview` | carries `changedAreas`; record a new review |
| POST mitigation | 409 | `RiskAssessmentChanged` | also when no assessment exists |
| POST mitigation | 409 | `MitigationNotExpected` | the latest assessment is `Negligible` |
| POST mitigation | 409 | `EvidenceDocumentNotCurrent` | withdrawn, unknown, other-case and other-tenant ids look the same |
| POST close (`ExternalDdsCompleted`) | 409 | `RiskAssessmentMissing` | no assessment exists |
| POST close | 409 | `RiskAssessmentReviewMismatch` | the latest assessment is on a different review than the latest one |
| POST close | 409 | `RiskNotNegligible` | the latest assessment is `NotNegligible` |
| POST close | 409 | `ReviewAlreadyBacksClosure` | **now typed** on the existing close conflict; detail text unchanged |

All three close risk reasons carry `riskAssessment { assessmentId, number, reviewId, conclusion }` when an assessment exists (never the rationale or notes). Other conditions: 400 body/validation, 415 non-JSON, 404 unknown/foreign case or assessment, 409 (no reason) for a closed or archived case, 405 for PUT/PATCH/DELETE.

**Close precedence for `ExternalDdsCompleted`**, as implemented: Owner check; case lock; not archived / not already closed; body validation; expected review matches the latest review (409 "Review history changed", no reason); `ReviewAlreadyBacksClosure`; a review that returned the dossier cannot close; reference rule (400); `StaleReview`; `QuantityNotReconciled`; `RiskAssessmentMissing`; `RiskAssessmentReviewMismatch`; `RiskNotNegligible`; `ExternalDdsRecordMissing`; `ExternalDdsReviewMismatch`; `ExternalDdsReferenceMismatch`; then the closure write. Route a user hitting any risk reason or `ReviewAlreadyBacksClosure` back to the **risk section**. There is no timestamp rule between the External DDS submission time and `assessedAtUtc`.

## 8. The criteria catalogue

`GET .../risk` returns it; render it from the response rather than hard-coding. Current content (id `provena-pilot-risk-criteria`, version `1`):

| Code | Title |
| --- | --- |
| `PRODUCTION_COUNTRY_AND_AREA` | Production country and area |
| `INDIGENOUS_PEOPLES_AND_LAND_RIGHTS` | Indigenous peoples and land rights |
| `GOVERNANCE_AND_LEGALITY_CONTEXT` | Governance and legality context |
| `EVIDENCE_RELIABILITY` | Reliability of the collected information |
| `SUPPLY_CHAIN_COMPLEXITY_AND_MIXING` | Supply-chain complexity and mixing |
| `CONCERNS_AND_PAST_NONCOMPLIANCE` | Concerns and past non-compliance |
| `OTHER_RISK_INDICATORS` | Other risk indicators |

It is a **structured prompt list for human judgement**, not an automated Article 10 legal-compliance engine; its wording has not been legally reviewed; it does not compute the conclusion. Each criterion's `guidance` text comes from the response. Keep the catalogue `version` you rendered and send it back; on `RiskCriteriaCatalogueChanged` reload the form.

## 9. Working export and reviewed export

- The working export `manifest.json` has a new `risk` section: `freshness`, `staleReason`, `changedAreas`, `latestReviewId`, `latestReviewBacksClosure`, `latestAssessmentId`, `assessments` (with findings) and `mitigations`, each **oldest first**, plus a `riskScope` text. It contains full rationale, finding notes and mitigation descriptions because it is the retained operator record. It does not contain the catalogue.
- The **reviewed export and snapshot are unchanged**: no risk data is added to them. Do not describe risk as part of the reviewed package.

## 10. Required frontend adaptations (contract level)

Implement **one coherent risk section** on the case, before the External DDS / closure completion steps:

1. Show the **catalogue** and the **latest risk freshness** (`None`, `Current`, `Stale` with `staleReason` and `changedAreas`), plus `latestReviewBacksClosure`.
2. Show the **assessment history** (newest first, with review revision number, conclusion, rationale, findings, `consideredMitigationThroughNumber`, actor and time) and the **mitigation history**; history stays readable on closed and archived cases and after withdrawal of a linked document.
3. **Owner action "record risk assessment"**: structured finding per catalogue criterion (all seven, exactly once), notes where required, rationale, conclusion; send `reviewId` = `latestReviewId`, the loaded expected ids and the rendered `criteriaCatalogueVersion`. Hide or disable it for Members, when no accepted Current review exists or when `latestReviewBacksClosure` is true, but still handle 403 and 409.
4. **Member action "record mitigation"** only while the latest assessment is `NotNegligible`: description, performed time (not in the future), optional **current** evidence document from the case's documents; tolerate a document that is withdrawn later.
5. **Owner reassessment** through the same action after mitigation or new information.
6. Make the stale/current state visible and explain the path: new evidence can stale the review -> record a new review -> record a new assessment.
7. **Closure errors** (`RiskAssessmentMissing`, `RiskAssessmentReviewMismatch`, `RiskNotNegligible`, `ReviewAlreadyBacksClosure`) route the user back to the risk section; `StaleReview` and `QuantityNotReconciled` keep their existing handling.
8. Offer the `ExternalDdsCompleted` close action only after the risk section shows a `Current`, `Negligible` latest assessment (as a convenience; the backend remains authoritative).
9. Prevent the `InsufficientInformation` + `Negligible` combination in the form, allow `Concern` + `Negligible`, and send no client-computed conclusion.
10. Use the neutral wording of section 1. No task manager: no due dates, assignees, priorities, reminders or automatic mitigation workflows.

### Error and cache guidance

- Map 409s through `errors.reason` and 400s through `errors.<Property>`; unknown reasons fall back to `detail`. Handle `ReviewAlreadyBacksClosure` as a typed reason on both the assessment and the close actions.
- Use **dedicated risk query keys** (for example one for the overview per case). Risk mutations should **refresh the risk data**; do not broadly invalidate unrelated case state unless objectively required. After a close, refresh the lifecycle data (it now carries `riskAssessmentId`). A mitigation that links a newly uploaded document also changes the case documents and the review freshness: refresh those through their own existing keys when that document was just uploaded, and show the resulting staleness rather than hiding it.
- Never silently retry any of the three history-changed reasons (section 6).

## 11. Implemented, deferred and out of scope

**Already implemented backend:** the four routes and DTOs; Owner-only assessment under the case lock with the documented conflict order; any-member mitigation; versioned catalogue; derived freshness through the existing review machinery; the `ExternalDdsCompleted` close gate with its three reasons and the pinned `riskAssessmentId`; the typed `ReviewAlreadyBacksClosure`; events `RiskAssessmentRecorded` and `RiskMitigationRecorded` (and `riskAssessmentId` in `CaseClosed`; safe metadata only); migration `20261016000117_RiskAssessmentAndMitigation`; the `risk` section of the working export.

**Required frontend adaptation:** section 10.

**Deferred (not implemented, do not simulate):** legally reviewed Article 10 catalogue wording; automatic official country-risk data (G19) and structured per-country classification; Article 13 simplified due-diligence eligibility; automated risk scoring; cross-case and supplier-wide aggregation; an annual organization-level due-diligence review; a separation-of-duties role; automated mitigation workflow or tasks; risk data in the reviewed export; a regulatory approval determination.

## 12. Backend acceptance criteria

1. `GET .../risk` before any record is 200 with the catalogue, `freshness: "None"`, `latestAssessment: null` and empty histories; any member can read it; foreign cases are 404.
2. A Member gets 403 on `POST risk-assessments` whatever the case or body; a demoted Owner with a valid token is denied; a Member can record a mitigation.
3. Findings must cover every criterion exactly once; missing notes, unknown or duplicate codes and `Negligible` + `InsufficientInformation` are 400; `Concern` + `Negligible` is 201.
4. Each assessment conflict has its typed reason in the documented order and writes nothing.
5. Freshness is `Current`, `Stale`/`ReviewSuperseded` or `Stale`/`ReviewStale` with the review's `changedAreas`; External DDS records and closing do not stale an assessment.
6. A mitigation needs the latest `NotNegligible` assessment and a current same-case document when linked; a later withdrawal keeps the link.
7. `ExternalDdsCompleted` closing follows stale -> quantity -> risk -> External DDS gate; `NotNegligible` is refused with safe metadata; the closure pins `riskAssessmentId`; the other outcomes need no assessment.
8. After a reopen the used review fails `ReviewAlreadyBacksClosure` for both assessment and close; the path new review -> new assessment -> new closure succeeds and both closures keep their own assessments.
9. Two simultaneous assessments serialize; a closure racing a new `NotNegligible` assessment never closes on a non-latest assessment.
10. Audit text carries ids, numbers, the catalogue version and the conclusion name, never free text or document names.
11. The working export carries the risk history; the reviewed export is unchanged.

## 13. Frontend acceptance criteria (next slice)

1. The risk section renders the catalogue, freshness, histories and the `latestReviewBacksClosure` state from `GET .../risk` only.
2. Only Owners are offered the assessment action; only a `NotNegligible` latest assessment offers the mitigation action; both send exactly the documented fields (no `organizationId`).
3. The form requires all seven findings, notes where required, blocks `InsufficientInformation` + `Negligible`, and allows `Concern` + `Negligible`.
4. Each documented 400/409 reason has a specific message; history-changed reasons refresh and ask the user to retry, never auto-retry.
5. Closure risk errors and `ReviewAlreadyBacksClosure` route the user to the risk section (and to a new review when needed).
6. UI wording never claims compliance, approval or "risk free".
7. The cross-repository browser suite is run against this backend revision (section 15).

## 14. Compatibility risks

- A client that closes as `ExternalDdsCompleted` without a risk assessment will start failing with 409 `RiskAssessmentMissing`; one that closes after a `NotNegligible` assessment gets `RiskNotNegligible`.
- A client that matched the old "already backs a closure" detail text should switch to `errors.reason`; the text itself is unchanged.
- Closure DTO consumers see one additional nullable property (`riskAssessmentId`).
- Mitigation evidence uploads can stale the review and therefore require a new review before a new assessment.
- `detail` strings are descriptive English text, not identifiers.
- Rolling the migration back is refused while any risk record exists or a closure references an assessment (guarded `Down`).

## 15. Backend verification performed and local compatibility evidence

`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1` on the implementation commit `e0836e0` plus this documentation slice (no code changed): restore, Release build with warnings as errors (0 warnings, 0 errors), **250 unit and 329 integration tests passed** (PostgreSQL via Testcontainers). This documentation slice changed no behaviour.

- **Local frontend/backend compatibility: not run.** The companion client has not adapted to this contract and the cross-repository browser suite described in [CHANGE_WORKFLOW](../CHANGE_WORKFLOW.md) was not executed against this checkout. When it is, record both repository revisions (with dirty state), the environment, the command and the result here.
