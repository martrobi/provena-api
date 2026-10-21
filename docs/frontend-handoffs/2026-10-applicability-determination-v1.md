# Applicability Determination v1 hardening: Owner-only PUT, optimistic concurrency, `RequiresLegalReview`

Status: **backend contract implemented (commit `08fc434`); the client has not adapted yet.** This document is the input for the client's adaptation. Canonical sources are the code (`Contracts/Requests.cs`, `Services/CaseApplicabilityService.cs`, `Services/ApplicabilityDerivation.cs`, `Endpoints/Compliance/CaseFactEndpoints.cs`), the tests (`ApplicabilityDeterminationTests`, `ApplicabilityHardeningDomainTests`), [API](../API.md) ("Completeness facts"), [EUDR_DOMAIN](../EUDR_DOMAIN.md) and [ADR 0008](../adr/0008-completeness-facts-and-reviewed-history.md) (amendment). Backend code, tests and OpenAPI win over this summary.

## 1. Summary

- There is still exactly one applicability model per case: the versioned, immutable `CaseApplicability`. **No route, table or column was added and no migration was needed.** Existing routes keep their paths.
- `PUT /api/eudr-cases/{caseId}/applicability` is now **Owner-only** (live server check). Members keep reading it (current and history).
- The PUT body gains **`expectedCurrentApplicabilityId`** (optimistic concurrency) and a stale value is a typed 409 `ApplicabilityHistoryChanged`.
- The existing "refresh the case master data first" 409 now also carries the machine-readable reason `MasterDataRefreshRequired` (same detail text).
- `regulatoryScope` and the derived `status` gain the value **`RequiresLegalReview`**: an Owner-recorded statement that applicability cannot currently be resolved without further legal/domain review. It is **unresolved/escalated**, not "in scope", not "outside scope", not "unsupported flow" and not "non-compliant".
- Fingerprint version (1) and the reviewed snapshot schema are unchanged. A new applicability version makes the latest review `Stale` through the existing fingerprint.
- Wording: use "applicability determination recorded", "recorded by an Owner", "requires legal review", "internal retained decision". Never "compliant", "legally approved", "legal advice", or "Provena determined the scope". Provena never resolves `RequiresLegalReview` by itself and never infers scope from CN code, commodity, `Product.IsActive` or `CommodityType.Other`.

## 2. Changed endpoints and behaviour

| Route | Who | Success |
| --- | --- | --- |
| `GET /api/eudr-cases/{caseId}/applicability` | any authenticated member | **200** `CaseApplicabilityDto`, **204** when nothing is recorded, 404 unknown/foreign case |
| `PUT /api/eudr-cases/{caseId}/applicability` | **Owner only** (live check; the JWT role claim is not trusted; before the case lookup and before the body is read) | **200** `CaseApplicabilityDto` (new or, for identical content, the unchanged current version) |
| `GET /api/eudr-cases/{caseId}/applicability-history` | any authenticated member | **200** `CaseApplicabilityVersionDto[]`, newest first, unpaginated |

Versions recorded earlier by Members stay valid, immutable and visible; the Owner-only rule is not retroactive and nothing is rewritten. No request or response contains `organizationId`.

## 3. Request contract (PUT)

Property names are camelCase and case-insensitive. Enums are strings (integers are rejected). Dates are ISO `yyyy-MM-dd`. The body is the **complete** assessment.

```json
{
  "productDescription": "Green coffee beans",
  "singleRelevantCommodity": true,
  "activityType": "PlacingOnMarketByImport",
  "operatorRole": "Operator",
  "operatorRoleDescription": null,
  "activityDate": "2026-06-01",
  "activityDateKind": "Planned",
  "scopeCitation": "Regulation (EU) 2023/1115, Annex I",
  "qualifications": null,
  "legalBasisVersion": "2023/1115 as applicable",
  "effectiveDate": "2026-01-01",
  "reasoning": "Assessed by the operator",
  "regulatoryScope": "InScope",
  "flowSupport": "Supported",
  "expectedCurrentApplicabilityId": "3f2c...-id-of-the-version-you-loaded-or-null"
}
```

| Field | Rules |
| --- | --- |
| `activityType` | `PlacingOnMarketByImport`, `PlacingOnMarketDomestic`, `Export` |
| `operatorRole` | `Operator`, `OtherUnsupported`. A recorded case-level fact, **not** the Owner/Member permission role |
| `operatorRoleDescription` | max 500; required and nonblank for `OtherUnsupported`; 400 for `Operator` |
| `activityDateKind` | `Planned`, `Actual` |
| `regulatoryScope` | `Pending`, `InScope`, `OutsideRegulatoryScope`, **`RequiresLegalReview`** |
| `flowSupport` | `Pending`, `Supported`, `UnsupportedFlow`. Ignored for `OutsideRegulatoryScope` and `RequiresLegalReview` |
| `productDescription` | max 2000; required when `regulatoryScope` is `InScope` |
| `scopeCitation` / `legalBasisVersion` / `reasoning` / `qualifications` | max 1000 / 200 / 4000 / 2000. `legalBasisVersion` stays free text (no catalogue) |
| `scopeCitation`, `legalBasisVersion`, `reasoning`, `effectiveDate`, `activityDate` | all required when `regulatoryScope` is `InScope` or `OutsideRegulatoryScope` |
| `reasoning` for `RequiresLegalReview` | **required, nonblank** (400 otherwise). The other fields are optional |
| `Pending` | requires none of the decision fields |
| `expectedCurrentApplicabilityId` | `null`/omitted when the client loaded a case with **no** current applicability (204); otherwise the exact `current.id` that was loaded. See section 6 |

Text is trimmed and blank optionals become null. The assessed product identity (`assessedProductId`, `assessedCnCode`, `assessedCommodity`) and the decision stamp (`decidedByUserId`, `decidedAtUtc`, `createdByUserId`) are **server-set**; any such client field is ignored.

## 4. Response contract

`CaseApplicabilityDto`:

```json
{
  "status": "RequiresLegalReview",
  "current": {
    "id": "…", "number": 2,
    "assessedProductId": "…", "assessedCnCode": "0901", "assessedCommodity": "Coffee",
    "productDescription": null, "singleRelevantCommodity": true,
    "activityType": "PlacingOnMarketByImport", "operatorRole": "Operator", "operatorRoleDescription": null,
    "activityDate": null, "activityDateKind": "Planned",
    "scopeCitation": null, "qualifications": null, "legalBasisVersion": null, "effectiveDate": null,
    "reasoning": "Counsel must confirm the CN heading.",
    "regulatoryScope": "RequiresLegalReview", "flowSupport": "Pending",
    "decidedByUserId": "…", "decidedAtUtc": "…", "createdByUserId": "…", "createdAtUtc": "…", "supersededAtUtc": null
  }
}
```

- `status` is **derived on read, never stored**: `NotRecorded | ProductChanged | Pending | OutsideRegulatoryScope | UnsupportedFlow | WithinSupportedScope | RequiresLegalReview`. Order: `NotRecorded`, `ProductChanged`, `RequiresLegalReview`, then the previous order. The GET and the PUT return the same shape; `current` is a `CaseApplicabilityVersionDto` (the history returns the same version shape, including `supersededAtUtc`).
- A `RequiresLegalReview` version counts as decided (it carries `decidedByUserId`/`decidedAtUtc`); `Pending` does not.
- `GET .../completeness`: under `RequiresLegalReview`, `applicability.status` is `RequiresLegalReview`, `APPLICABILITY_DECIDED`, `APPLICABILITY_IN_SCOPE` and `FLOW_SUPPORTED` are `Missing` (not `Invalid`), `APPLICABILITY_RECORDED` is `Satisfied`, `APPLICABILITY_PRODUCT_MATCH` keeps its rule and `dataCoverage` is `Incomplete`.
- The status enum in `GET completeness` (`applicability.status`) and in the reviewed snapshot can now be `RequiresLegalReview`; clients with a closed list of statuses must add it.

## 5. Error/status behaviour

| Status | When |
| --- | --- |
| 401 | anonymous |
| **403** | the caller is not a live Owner of the organization. Returned for an existing, nonexistent or foreign case and for an invalid, malformed or missing body alike (nothing about the case or body is revealed). Detail: `Only an organization owner can record the applicability determination.` |
| 404 | unknown/foreign case (Owners only reach this) |
| 409 | Closed or archived case (existing, untyped), `MasterDataRefreshRequired`, `ApplicabilityHistoryChanged` |
| 415 / 400 | non-JSON content type / missing, malformed or invalid body (Owners only) |

Order inside one transaction: Owner check, case lock (404), closed/archived (409), body read and validation (400), **master-data compatibility (409 `MasterDataRefreshRequired`)**, load current, **expected id comparison (409 `ApplicabilityHistoryChanged`)**, identical-resubmission no-op, supersede and insert.

```json
// 409 MasterDataRefreshRequired
{ "status": 409, "detail": "Refresh the case master data first.", "errors": { "reason": "MasterDataRefreshRequired" } }

// 409 ApplicabilityHistoryChanged
{ "status": 409, "detail": "Applicability history changed since it was loaded",
  "errors": { "reason": "ApplicabilityHistoryChanged", "currentApplicabilityId": "…", "currentApplicabilityNumber": 2 } }
```

`currentApplicabilityId` and `currentApplicabilityNumber` are `null` when no version exists (the client sent a non-null expected id for a case that has none). Match on `errors.reason`, never on the detail text. A client with both problems sees `MasterDataRefreshRequired` first, because that check runs before the expected-id comparison.

Review consequences (existing endpoints, existing shape): while the current applicability is `RequiresLegalReview`, `POST .../reviews` with `AcceptedAsCompleteUnderPolicy`, `ConfirmedOutsideRegulatoryScope` or `ConfirmedUnsupportedFlow` answers 409 whose `errors.reason` text names `RequiresLegalReview`, with `failingItems` including `APPLICABILITY_DECIDED`; `ReturnedForCorrection` stays possible, and a returned review cannot close the case.

## 6. Optimistic concurrency

- Load `GET .../applicability`. If 200, send `current.id` unchanged as `expectedCurrentApplicabilityId`; if 204, send `null` (or omit it).
- Identical content with the correct id is a 200 no-op (same version, no new row, no audit event). Identical content with a wrong or missing id is **not** a no-op: it is the 409 conflict.
- The comparison happens under the case lock, so of several simultaneous writers exactly one gets 200 and the others get `ApplicabilityHistoryChanged`. There is no silent retry on the server.
- After a successful PUT, use `current.id` of the response as the next expected id.

## 7. Breaking-change notes

- **Breaking for Members:** `PUT .../applicability` returns 403 for non-Owners. The first-write behaviour is otherwise unchanged (`null` expected id).
- **Breaking for every writer:** a PUT without the id of the existing current version now fails with 409 `ApplicabilityHistoryChanged` (previously it replaced silently). A client that omits the field only works for the first write on a case.
- Additive: the `RequiresLegalReview` enum value (also in `GET completeness` and the reviewed snapshot), and `errors.reason` on the existing master-data 409.
- Unchanged: routes, DTO shapes, fingerprint version 1, the snapshot schema, the working and reviewed exports, close/reopen/archive contracts and all other applicability rules.

## 8. Required frontend adaptations at contract level

1. Members can read but not edit applicability; show a read-only view. Owners can save. The role claim in the JWT is only a UI hint: the server decides with a live check and can still answer 403 (for example after a demotion); handle 403 by switching to read-only and refreshing the user's role.
2. Load the current version, keep its `id`, and send it unchanged as `expectedCurrentApplicabilityId` (null when 204).
3. On **`ApplicabilityHistoryChanged`**: refresh the current applicability (`currentApplicabilityId`/`currentApplicabilityNumber` say what changed), keep the values the user entered where practical, show what changed, and require a **conscious** retry with the new id. Never retry silently.
4. On **`MasterDataRefreshRequired`**: guide the user to refresh the case master data (`POST .../master-data/refresh`), then to re-check and save again.
5. Support `RequiresLegalReview` explicitly as a scope choice: `reasoning` is required (validate before sending); other decision fields are optional; `flowSupport` is irrelevant for it. Show it as **unresolved/escalated for legal review**, never as out of scope, unsupported flow, or non-compliance, and never imply Provena or its users have decided anything legally.
6. Treat the new status value in the completeness read model, the applicability panel, the review precondition messages and the reviewed snapshot view. Explain that a returned-for-correction review is possible while the others are blocked.
7. After a successful PUT, refresh everything derived from the dossier through the existing invalidation paths: completeness (fingerprint, `reviewFreshness`, `changedAreas` containing `Applicability`), the latest review, the risk overview and the External DDS record. They are not mutated; they simply no longer match the dossier or the latest review.
8. Do not infer scope from the CN code, the commodity, `Product.IsActive` or `CommodityType.Other` anywhere in the client. Do not derive `operatorRole` from the Owner/Member role.
9. Closed/archived cases: the PUT is 409 (untyped); the existing read-only handling applies. After a reopen the history is kept and a correction is a new version.

## 9. Compatibility risks

- Old clients that PUT applicability repeatedly without an expected id fail from the second write; old Member-facing "edit applicability" actions get 403.
- Closed-set status/scope renderers or generated types that reject unknown enum strings break on `RequiresLegalReview`.
- Review, risk and External DDS panels show stale data if applicability changes are not wired to their invalidation.
- Matching on `detail` text instead of `errors.reason` is unsupported (the detail texts are stable but not the contract).

## 10. Lifecycle and review implications

- A new applicability version stales the latest review (`reviewFreshness` `Stale`, `changedAreas` `["Applicability"]`). Closing then fails with `StaleReview`. A new review is required, then (for `ExternalDdsCompleted`) a new risk assessment and External DDS record of that review: the old ones fail the existing gates (`RiskAssessmentReviewMismatch`, `ExternalDdsReviewMismatch`).
- `RequiresLegalReview` cannot reach any closure outcome: a later version must resolve it, a new review must be recorded and the normal gates (quantity, risk, External DDS) apply. `OutsideRegulatoryScope` and `UnsupportedFlow` closures still follow the matching applicability and review decision.
- Reopen keeps the applicability history; a correction is a new immutable version; the review that backed the earlier closure still answers `ReviewAlreadyBacksClosure`.

## 11. Out of scope

Legal-basis catalogue (`legalBasisVersion` is still free text), comparison of `activityDate` with shipment dates, activity-date or transition-law rules, reviewed Annex I/CN mapping, tariff classification, trader/representative/SME role models, country risk and Article 13, automatic scope determination, any change to the fingerprint, risk, External DDS, quantity or concern semantics, and any new route.

## 12. Backend verification performed and local compatibility evidence

- Backend: `scripts/verify.ps1` passed on the implementation commit `08fc434` (restore, Release build with warnings as errors, 264 unit and 344 integration tests against PostgreSQL), including `ApplicabilityDeterminationTests` (14 test methods, 15 cases) and `ApplicabilityHardeningDomainTests` (10 test methods). The route inventory and OpenAPI path count were unchanged.
- No migration; model snapshot unchanged (see [DATABASE](../DATABASE.md)).
- Local frontend/backend compatibility: **not run.** The client checkout was neither inspected nor changed, and no client live suite was executed for this change. Client adaptation remains to be done and verified per [CHANGE_WORKFLOW](../CHANGE_WORKFLOW.md).
