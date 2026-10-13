# Completeness facts, completeness read model and immutable reviewed history (Slice 5)

Status: **complete Slice 5 backend contract.** It covers the working-fact APIs (commit 1), the read-only completeness API (commit 2) and the Owner-only immutable reviews, review freshness and the reviewed export (commit 3). Risk, mitigation, release, DDS and EU Information System are out of scope.

## 1. Summary

The backend can now record, per organization and per case, the facts a later completeness check will need. They are working facts entered or assessed by people. **None of them is a completeness, sufficiency, compliance or review result**, and none changes a requirement status, the case status or `Complete` eligibility.

- An organization **operator profile** (one per organization).
- A **case master-data copy**: the operator, supplier and product values a case relies on, copied from the shared catalogue and refreshed only on request. Catalogue edits never change it; they only show up as advisory **drift**.
- A **case applicability** assessment (versioned) with a derived status.
- A **recipient statement** (versioned): identified recipients, or not applicable with a basis. No statement means unresolved.
- **Evidence links**: a retained document of the case offered for a purpose (presence only).
- Suppliers gain an optional **postal address**.
- A read-only **completeness report** (data coverage, evidence readiness, advisories, fingerprint) and Owner-only **immutable reviews** with Current/Stale freshness and a reviewed export.

## 2. Changed endpoints or behaviour

New (all internal, authenticated, no `organizationId` in any body):

| Route | Success |
| --- | --- |
| GET `/api/operator-profile` | 200 profile, **204** when none |
| PUT `/api/operator-profile` | 200 profile (upsert) |
| GET `/api/eudr-cases/{caseId}/master-data` | 200 `{ current, shipmentMatch, drift[] }`, **204** when no copy |
| POST `/api/eudr-cases/{caseId}/master-data/refresh` | 200 same shape (no body) |
| GET `/api/eudr-cases/{caseId}/master-data-history` | 200 versions, newest first |
| GET `/api/eudr-cases/{caseId}/applicability` | 200 `{ status, current }`, **204** when none |
| PUT `/api/eudr-cases/{caseId}/applicability` | 200 `{ status, current }` |
| GET `/api/eudr-cases/{caseId}/applicability-history` | 200 versions, newest first |
| GET `/api/eudr-cases/{caseId}/recipients` | 200 statement, **204** when unresolved |
| PUT `/api/eudr-cases/{caseId}/recipients` | 200 statement |
| GET `/api/eudr-cases/{caseId}/recipients-history` | 200 statements, newest first |
| GET `/api/eudr-cases/{caseId}/evidence-links` | 200 all links, withdrawn included |
| POST `/api/eudr-cases/{caseId}/evidence-links` | **201** link |
| DELETE `/api/eudr-cases/{caseId}/evidence-links/{linkId}` | 204 (withdrawal) |
| GET `/api/eudr-cases/{caseId}/completeness` | 200 completeness report (read-only; see 5a) |
| POST `/api/eudr-cases/{caseId}/reviews` | **201** review (Owner only; see 5b) |
| GET `/api/eudr-cases/{caseId}/reviews`, `/reviews/{reviewId}` | 200 summaries newest first / 200 review detail |
| GET `/api/eudr-cases/{caseId}/reviews/{reviewId}/export` | 200 `application/zip` (reviewed export) |

Changed: `POST/PUT /api/suppliers` accept an optional `postalAddress`; supplier responses (list and single) include `postalAddress` (null when unset). The supplier CSV import header is **unchanged**.

There is no DELETE for the profile, master data, applicability or recipients (405). Evidence-link DELETE is a semantic withdrawal; the row is retained.

## 3. Request/query contract

Operator profile (`PUT`, complete replacement):

```json
{ "legalName": "Operator d.o.o.", "postalAddress": "Ilica 1, Zagreb", "email": "operator@example.test", "establishmentCountryCode": "HR", "eoriNumber": null }
```

Required: `legalName` (max 200), `postalAddress` (max 1000), `email` (valid, max 256), `establishmentCountryCode` (two uppercase letters). Optional `eoriNumber`: uppercase letters/digits, max 17; it is stored only and is not evaluated.

Applicability (`PUT`, complete replacement; text is trimmed and blank optionals become null):

```json
{
  "productDescription": "Green coffee beans", "singleRelevantCommodity": true,
  "activityType": "PlacingOnMarketByImport", "operatorRole": "Operator", "operatorRoleDescription": null,
  "activityDate": "2026-06-01", "activityDateKind": "Planned",
  "scopeCitation": "Regulation (EU) 2023/1115, Annex I", "qualifications": null,
  "legalBasisVersion": "2023/1115 as applicable", "effectiveDate": "2026-01-01",
  "reasoning": "Assessed by the operator", "regulatoryScope": "InScope", "flowSupport": "Supported"
}
```

- Enums: `activityType` `PlacingOnMarketByImport | PlacingOnMarketDomestic | Export`; `operatorRole` `Operator | OtherUnsupported`; `activityDateKind` `Planned | Actual`; `regulatoryScope` `Pending | InScope | OutsideRegulatoryScope`; `flowSupport` `Pending | Supported | UnsupportedFlow`. Strings only; numbers are rejected.
- `operatorRoleDescription`: required (max 500) when `operatorRole` is `OtherUnsupported`; must be absent/blank for `Operator` (400 otherwise).
- When `regulatoryScope` is not `Pending`: `scopeCitation` (max 1000), `legalBasisVersion` (max 200), `reasoning` (max 4000), `effectiveDate` and `activityDate` are required. `productDescription` (max 2000) is additionally required for `InScope`. `qualifications` max 2000.
- `flowSupport` may stay `Pending` for `OutsideRegulatoryScope`; it is ignored there.
- Do **not** send assessed product fields or decision stamps: the server copies the assessed product (id, CN code, commodity) from the case's master-data copy and stamps the decider. Such fields are ignored.

Recipients (`PUT`, complete replacement):

```json
{ "resolution": "RecipientsIdentified", "notApplicableBasis": null, "recipients": [ { "name": "Importer GmbH", "postalAddress": "Hauptstrasse 1, Berlin", "email": "importer@example.test" } ] }
{ "resolution": "NotApplicable", "notApplicableBasis": "Goods stay with the operator", "recipients": [] }
```

`RecipientsIdentified`: 1-50 recipients, every field required (name max 200, address max 1000, valid e-mail max 256), no basis. `NotApplicable`: no recipients, nonblank basis (max 2000). Anything else is 400.

Evidence link (`POST`): `{ "caseDocumentId": "...", "purpose": "Legality", "note": null }`. `purpose`: `Legality | DeforestationFree | Applicability | ProductionSource`; `note` max 1000.

Supplier: `postalAddress` max 1000.

## 4. Response contract

Master data:

```json
{
  "current": {
    "id": "…", "number": 2,
    "operator": { "legalName": "…", "postalAddress": "…", "email": "…", "establishmentCountryCode": "HR", "eori": null },
    "supplier": { "supplierId": "…", "name": "…", "postalAddress": null, "email": "…", "countryCode": "BR" },
    "product": { "productId": "…", "cnCode": "0901", "commodity": "Coffee", "name": "…", "catalogueDescription": null },
    "createdByUserId": "…", "createdAtUtc": "…", "supersededAtUtc": null
  },
  "shipmentMatch": true,
  "drift": [ { "area": "Supplier", "fields": ["email", "countryCode"] } ]
}
```

- `operator.*` is null when the organization had no profile at copy time.
- `shipmentMatch` is `false` when the copy's supplier or product is no longer the case's **current** shipment's (a shipment can change supplier/product until it is locked by a supplier request, evidence progress or a dossier). Refresh to adopt.
- `drift` lists, per area (`Operator | Supplier | Product`), the camelCase names of fields where the live catalogue now differs from the copy (operator: `legalName`, `postalAddress`, `email`, `establishmentCountryCode`, `eori`; supplier: `name`, `postalAddress`, `email`, `countryCode`; product: `cnCode`, `commodity`). Product `name` and `catalogueDescription` are context and never appear. `[]` means none. Drift is advisory and read-only.

Applicability: `{ "status": "WithinSupportedScope", "current": { …all input fields…, "id", "number", "assessedProductId", "assessedCnCode", "assessedCommodity", "decidedByUserId", "decidedAtUtc", "createdByUserId", "createdAtUtc", "supersededAtUtc" } }`. `status` is derived on every read: `NotRecorded` (never returned with a body), `ProductChanged`, `Pending`, `OutsideRegulatoryScope`, `UnsupportedFlow`, `WithinSupportedScope`.

- `ProductChanged`: the case's current master-data copy now describes a different product (id, CN code or commodity) than the one assessed. Re-save the assessment against the refreshed copy. A supplier-only or operator-only refresh never causes it.
- `UnsupportedFlow` is shown when the human marked it so, or when the pilot envelope rejects the flow (Wood, Cattle or Other commodity, `Export`, `singleRelevantCommodity: false`, or `operatorRole: OtherUnsupported`).
- `decidedByUserId`/`decidedAtUtc` are null while `regulatoryScope` is `Pending`.

Recipients: `{ "id", "number", "resolution", "notApplicableBasis", "recipients": [ { "id", "name", "postalAddress", "email" } ], "createdByUserId", "createdAtUtc", "supersededAtUtc" }`, recipients ordered by name, postal address, e-mail, then id (deterministic).

Evidence link: `{ "id", "caseDocumentId", "purpose", "note", "documentFileName", "documentWithdrawnAtUtc", "createdByUserId", "createdAtUtc", "withdrawnAtUtc", "withdrawnByUserId" }`. The list returns **all** links, withdrawn ones included, oldest first. `withdrawnAtUtc`/`withdrawnByUserId` are null while the link is current; `documentWithdrawnAtUtc` is a separate fact (the linked document was withdrawn) and does not withdraw the link. `POST` returns 201 with the link in the body and **no `Location` header** (there is no GET by id).

History routes return arrays newest first (`[]` when none), unpaginated. Dates are ISO `yyyy-MM-dd`; timestamps ISO 8601; enums are strings.

## 5. Error/status behaviour

- 401 anonymous. 404 unknown or foreign case, and an unknown, foreign or other-case evidence link. Case-scoped routes return 404 for a case of another organization.
- **Order of checks for writes:** route case (404), then terminal case (409), then body validation (400). A syntactically bad body (malformed JSON, unknown enum name, number instead of enum string) is 400 from the framework.
- 409 `Completed or archived cases cannot be changed.` for every write, **including master-data refresh**, on a Completed or Archived case (reads still work).
- 409 `Refresh the case master data first.` on applicability `PUT` when the case has no master-data copy or its copy no longer matches the case's current shipment.
- Evidence links: a document that is unknown, from another organization or from another case is the same non-disclosing **400**; a withdrawn document is 409; a second current link for the same document and purpose is 409. The same document may be linked for several purposes. Withdrawing an already withdrawn link is an idempotent 204.
- Identical re-submission of applicability/recipients, or a refresh with unchanged values, is 200 and creates nothing (same version id).
- 405 for DELETE on the profile, master data, applicability and recipients.

## 5a. Completeness read model (`GET /api/eudr-cases/{caseId}/completeness`)

New, internal, authenticated and **read-only** (no write, no audit, no change to requirements or the case status). 401 anonymous, 404 unknown or foreign case. It reports whether the recorded facts satisfy a versioned pilot policy. It is **not** a compliance, safety, deforestation-free, risk or DDS-readiness result, and there is no combined "OK" flag: the client must show the three axes separately.

```json
{
  "policy": { "id": "provena-pilot-completeness", "version": "1", "limitations": ["…"] },
  "evaluatedAtUtc": "2026-10-14T09:00:00+00:00",
  "fingerprint": "<64 lower-case hex>", "fingerprintVersion": 1,
  "applicability": { "status": "WithinSupportedScope", "currentVersionId": "…" },
  "dataCoverage": "Incomplete",
  "evidenceReadiness": "AwaitingRequirementDecisions",
  "reviewFreshness": "None",
  "latestReview": null,
  "advisories": [ { "code": "CATALOGUE_DRIFT_SUPPLIER", "area": "Supplier", "subjectId": null, "message": "…" } ],
  "items": [ { "code": "PARTY_RECIPIENTS", "group": "Parties", "subjectType": null, "subjectId": null, "status": "Missing", "reason": "Recipients are unresolved: …" } ]
}
```

- `dataCoverage`: `Complete | Incomplete` from the groups `MasterData`, `Applicability`, `Product`, `Quantity`, `Production`, `Parties` (any `Missing` or `Invalid` item -> `Incomplete`). `Evidence` items never affect it.
- `evidenceReadiness`: `EvidenceMissing | AwaitingRequirementDecisions | ReadyForOwnerReview`, from the `Evidence` group only. There is no "Reviewed" value. `ReadyForOwnerReview` means decided requirements and linked documents exist, nothing more.
- `reviewFreshness` is `"None"` and `latestReview` is `null` until the first review is recorded; after that they describe the latest review (section 5b). The shape of both is stable.
- `items[].status`: `Satisfied | Missing | Invalid | AwaitingReview | NotApplicable`. `AwaitingReview` appears only for a `Submitted` requirement. `reason` is human-readable text that says what was checked and may be shown as-is. `group` is one of the seven names above. Item `code`s are stable identifiers (full list and rules in the API documentation); `REQUIREMENT_<Type>` uses the requirement type name as is, for example `REQUIREMENT_Geolocation`. `subjectType`/`subjectId` point at the row the item is about, or are null.
- `advisories` are informational (catalogue changed since the case copied it; a pinned plot revision has since been superseded). They never make a case incomplete and never change `fingerprint`.
- `fingerprint` is an opaque identifier of the case facts (lower-case SHA-256 hex) that changes exactly when a fact changes and **not** for catalogue edits, context-only master-data refreshes, producer/plot renames, a superseded pinned revision or case status changes. It is meant for optimistic comparison by the later review API; clients should treat it as an opaque string and `fingerprintVersion` as a number.
- Two properties to reflect in the UI: (1) completeness is independent of the operational status - a case can be `ReadyForExport` or `Completed` while `dataCoverage` is `Incomplete`; (2) `MASTER_DATA_SHIPMENT_MATCH` is `Invalid` after the shipment's supplier or product changed until the user refreshes the case master data.
- Evidence links count only while the link itself is not withdrawn **and** its document is current; the `EVIDENCE_*_PRESENT` reasons say "presence only; not reviewed and not a sufficiency finding" and the UI must not present them as verification.

Working export (`GET .../export`): the manifest gains `completeness` (the same object), `latestReview` (`null`) and `reviewScope` ("Working-current export. Not the reviewed record; use the review export for what was reviewed."). Nothing existing in the package changed.

## 5b. Reviews (`/api/eudr-cases/{caseId}/reviews`)

An organization **Owner** can record an immutable review of the dossier. A review is a human decision on exactly the facts the Owner loaded. It is **not** a legal-compliance, risk, safety, deforestation-free, release or DDS result, it changes no requirement and no case status, and `Complete` is not gated by it. The UI must label decisions with their exact meaning and never as "approved/compliant".

Routes (all authenticated; no PUT/PATCH/DELETE):

| Route | Who | Success |
| --- | --- | --- |
| POST `/api/eudr-cases/{caseId}/reviews` | **Owner only** | **201** `ReviewDetail` + `Location` of the GET-by-id route |
| GET `/api/eudr-cases/{caseId}/reviews` | any member | 200 summaries, newest first (by revision number) |
| GET `/api/eudr-cases/{caseId}/reviews/{reviewId}` | any member | 200 `ReviewDetail` |
| GET `/api/eudr-cases/{caseId}/reviews/{reviewId}/export` | any member | 200 `application/zip` (reviewed export) |

Create request:

```json
{ "decision": "AcceptedAsCompleteUnderPolicy", "rationale": "Checked the recorded facts against the policy.", "expectedFingerprint": "<completeness.fingerprint as loaded>", "expectedLatestReviewId": null }
```

- `decision` (strings only): `AcceptedAsCompleteUnderPolicy` ("accepted as complete under the versioned Provena pilot completeness policy"), `ConfirmedOutsideRegulatoryScope` (the recorded applicability places the case outside the regulatory scope), `ConfirmedUnsupportedFlow` (the recorded flow is outside the pilot's supported envelope, including the `OtherUnsupported` role), `ReturnedForCorrection` (sends the dossier back; always allowed).
- `rationale` required, max 4000. `expectedFingerprint` is the `fingerprint` from `GET completeness` that the user was looking at. `expectedLatestReviewId` is `completeness.latestReview.reviewId` as loaded, or `null` when there was no review. Send exactly what was loaded; do not re-fetch silently before posting.
- **Status codes:** 401 anonymous; **403 for any non-Owner, whatever the case id or body** (an existing, unknown or foreign case and an invalid body all answer 403; a user whose Owner role was removed is denied at once even with an unexpired token - there is nothing to disclose and no 404/400 to interpret); 404 unknown/foreign case; 400 validation (including malformed JSON); 415 non-JSON content type; **409** in these cases: the case is Completed/Archived, the fingerprint or the latest review changed since it was loaded (`Dossier or review history changed since it was loaded` - reload completeness and let the user decide again), or a precondition of a positive decision is not met.
- **Precondition failures** return 409 with `errors: { reason, failingItems: [{ code, subjectType, subjectId, status, reason }] }`. Show `reason` and the failing items; every failing item code is a real completeness item code from `GET completeness`. `AcceptedAsCompleteUnderPolicy` needs a satisfied `MASTER_DATA_SHIPMENT_MATCH`, applicability `WithinSupportedScope`, no `Missing`/`Invalid`/`AwaitingReview` item (`NotApplicable` is fine) and **no catalogue drift** (stated in `reason`, not as a failing item: refresh the case master data first). `ConfirmedOutsideRegulatoryScope` and `ConfirmedUnsupportedFlow` need a satisfied shipment match and the matching applicability status (a wrong status is explained in `reason`; `ProductChanged` blocks all three positive decisions). `ReturnedForCorrection` has no precondition.
- **`ReviewDetail`:** `{ reviewId, caseRevisionId, revisionNumber, decision, rationale, reviewerUserId, reviewedAtUtc, policyId, policyVersion, snapshotSchemaVersion, fingerprintVersion, fingerprint, sectionHashes, pins, snapshot }`. `snapshot` is `{ schemaVersion, facts, context, completeness }`: `facts` is what was relied on, `context` is frozen display context (producer/plot names, product name/description, whether a pinned plot revision was already superseded, drift at review time) and `completeness` is the item list evaluated at review time. All of it is stored: it never changes when the live catalogue, labels or documents change later, so show it as "as reviewed on <date>". `pins.documentIds` includes documents withdrawn afterwards.
- **Summary:** `{ reviewId, caseRevisionId, revisionNumber, decision, reviewerUserId, reviewedAtUtc, policyId, policyVersion, fingerprint, fingerprintVersion }`; order by `revisionNumber` (not by time: timestamps can be equal).
- **Freshness in `GET completeness`:** `reviewFreshness` is `None` (no review), `Current` or `Stale`; `latestReview` is `null` or `{ reviewId, caseRevisionId, decision, reviewedAtUtc, policyVersion, policyChanged, changedAreas }`. `Stale` means a reviewed fact changed since: `changedAreas` lists `Shipment`, `MasterData`, `Applicability`, `Recipients`, `Lot`, `Requirements` and/or `Evidence`. Not stale: catalogue edits without a case refresh (an advisory only), a context-only refresh, producer/plot renames or active flags, a pinned plot revision superseded later (advisory `PINNED_REVISION_SUPERSEDED`; using the newer revision needs a lot save and then does make the review stale), case status, exports, supplier-request changes. `policyChanged: true` is informational (the review was made under an older policy) and does not make it stale. The pair `latestReview.decision` + `reviewFreshness` is the only "reviewed" state; never present `evidenceReadiness: ReadyForOwnerReview` as reviewed.
- **Lifecycle:** after the case is Completed or Archived no new review (409), refresh or working-fact write is possible, but reviews, the reviewed export and freshness stay readable, and a review that was `Current` at completion stays `Current`.
- **Reviewed export** (`GET .../reviews/{reviewId}/export`): `case-{reference}-review-{revisionNumber}/` with `review.json` (the review, the stored snapshot, `exportedAtUtc` and a notice that it is a reviewed snapshot and not a compliance determination, risk assessment or DDS), `plots/{revisionId:N}.geojson` (the exact pinned plot revisions), `documents/...` (the pinned documents, also those withdrawn later) and `geolocation/original.geojson` (the pinned geolocation). It never reflects later changes. The existing working export remains the current working dossier and carries `completeness` and `latestReview`; offer both under clearly different names.

## 6. Breaking-change notes

- **No existing field or route is removed or renamed.** Supplier responses gain `postalAddress` (additive).
- Behavioural: a supplier or product that is referenced by any master-data copy or applicability can no longer be deleted, even if no shipment uses it any more (the existing generic 409 for a referenced record). Previously only shipment use blocked deletion.
- `PUT /api/suppliers/{id}` is a complete replacement: a client that does not send `postalAddress` clears it. Clients that edit suppliers should round-trip the field.

## 7. Required frontend adaptations (contract level)

- Add the operator profile form (treat 204 as "not set up"). Without a profile, master-data operator values are null.
- Per case, show the master-data copy (204 = not copied yet) with a clear **Refresh** action; show `drift` as an advisory ("catalogue changed since this case copied it") and `shipmentMatch: false` as a warning that requires a refresh before applicability can be saved. Do not refresh implicitly.
- Applicability editor: prefill `productDescription` from `current.product.catalogueDescription` if wanted, but it is the user's own assessed description. Disable saving with a hint when there is no copy or `shipmentMatch` is false (or handle the 409). Show `status`, including `ProductChanged` ("re-assess after a product change"). Do not present any status as legal compliance.
- Recipients: 204 means **unresolved**, which is different from `NotApplicable`; offer both choices explicitly.
- Evidence links: let users offer an existing, non-withdrawn case document for a purpose; show withdrawn-document links as such; use DELETE to withdraw. Label a link as "offered for this purpose", never as verified, sufficient or compliant.
- Handle 409 (terminal case) by making these editors read-only on Completed/Archived cases.
- Re-saving unchanged content is safe and idempotent.
- Add a completeness view per case from `GET .../completeness` (section 5a): show the two live axes separately, list the items grouped by `group` with their `reason`, show advisories as information, and never derive a single pass/fail or compliance badge from them. Refetch after any fact change; compare nothing yet by `fingerprint`.
- Add a review panel: show `reviewFreshness`, the latest review and its decision, and let an Owner record a review with the exact `fingerprint` and `latestReview.reviewId` that the screen was showing (section 5b); hide the action for non-Owners but still handle 403; on 409 reload completeness. List past reviews from `GET .../reviews` and open them read-only. Never render a review or an `AcceptedAsCompleteUnderPolicy` as "compliant"/"approved".

## 8. Compatibility risks

- Any authenticated member of an organization can write these facts today; the only Owner-only operation is recording a review (`POST ./reviews`); do not infer permissions from the UI, and note that no API creates a Member yet, so a Member must be provisioned outside the API for the 403 behaviour to be observable.
- The master-data copy is separate from the live catalogue: displays that mix the two will disagree until a refresh.
- Supplier/product deletion is now blocked more often (see section 6).
- The derived applicability `status` can change without a new applicability version when the master-data copy is refreshed (`ProductChanged`).
- Future backend changes may add fields to the case-level views; treat unknown JSON fields as additive.

## 9. Out-of-scope items

Not implemented: risk assessment and mitigation, any combined "compliant" verdict, membership management (no API creates a Member) and a general permission model, release/approval, DDS and EU Information System submission, downstream/trader workflows, cattle establishments, wood species, composite products, Article 4a, split/merge/processing, coordinate-precision checks. EORI is stored but never evaluated. The working export gained only the additive `completeness`, `latestReview` and `reviewScope` members (section 5a).

## 10. Backend verification performed

`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1`: Release build 0 warnings / 0 errors, 186 unit tests and 199 integration tests passed, 0 failed, 0 skipped, exit 0 (PostgreSQL 17 via Testcontainers). `dotnet ef migrations has-pending-model-changes` reports none, and the idempotent migration script was reviewed: existing tables change only by `Suppliers.PostalAddress` and the `Documents` alternate key.

Covered by `ReviewTests` (unit and HTTP: Owner gate, decisions, optimistic concurrency, PostgreSQL lock tests, freshness, snapshot immutability, reviewed export), `ReviewHistoryModelTests`, the `ReviewedDossierHistory` cases in `MigrationTests`, `CompletenessTests` and `CompletenessReadModelTests` (completeness read model, fingerprint, working export), `CompletenessFactsTests` (operator profile, supplier postal address, master-data refresh/idempotency/drift/shipment binding, applicability, recipients, evidence links, terminal-case and ordering rules, serialization), `CompletenessFactsModelTests`, `CompletenessFactsDomainTests`, the `CompletenessFacts` cases in `MigrationTests`, `EndpointInventoryTests` and `HttpContractTests`. No frontend was exercised.
