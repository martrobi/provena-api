# Completeness facts: operator profile, case master data, applicability, recipients and evidence links

Status: **partial handoff.** This covers only the working-fact APIs that exist now (Slice 5, commit 1). The completeness evaluation API (`GET .../completeness`), review records, the Owner review gate, review freshness and the reviewed export are **not implemented**; they will extend this document when they land. Do not build against them yet.

## 1. Summary

The backend can now record, per organization and per case, the facts a later completeness check will need. They are working facts entered or assessed by people. **None of them is a completeness, sufficiency, compliance or review result**, and none changes a requirement status, the case status or `Complete` eligibility.

- An organization **operator profile** (one per organization).
- A **case master-data copy**: the operator, supplier and product values a case relies on, copied from the shared catalogue and refreshed only on request. Catalogue edits never change it; they only show up as advisory **drift**.
- A **case applicability** assessment (versioned) with a derived status.
- A **recipient statement** (versioned): identified recipients, or not applicable with a basis. No statement means unresolved.
- **Evidence links**: a retained document of the case offered for a purpose (presence only).
- Suppliers gain an optional **postal address**.

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

## 8. Compatibility risks

- Any authenticated member of an organization can write these facts today; there is no owner-only distinction yet (the Owner review gate arrives with the review commit). Do not infer permissions from the UI.
- The master-data copy is separate from the live catalogue: displays that mix the two will disagree until a refresh.
- Supplier/product deletion is now blocked more often (see section 6).
- The derived applicability `status` can change without a new applicability version when the master-data copy is refreshed (`ProductChanged`).
- The later completeness and review APIs may add fields to the case-level views; treat unknown JSON fields as additive.

## 9. Out-of-scope items

Not implemented: completeness evaluation and `GET completeness`, review records and decisions, the Owner review gate, review freshness/staleness, reviewed export, fingerprinting, risk assessment and mitigation, release/approval, DDS and EU Information System submission, downstream/trader workflows, cattle establishments, wood species, composite products, Article 4a, split/merge/processing, coordinate-precision checks. EORI is stored but never evaluated. The working export (`GET .../export`) is unchanged by this commit.

## 10. Backend verification performed

`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1`: Release build 0 warnings / 0 errors, 101 unit tests and 144 integration tests passed, 0 failed, 0 skipped, exit 0 (PostgreSQL 17 via Testcontainers). `dotnet ef migrations has-pending-model-changes` reports none, and the idempotent migration script was reviewed: existing tables change only by `Suppliers.PostalAddress` and the `Documents` alternate key.

Covered by `CompletenessFactsTests` (operator profile, supplier postal address, master-data refresh/idempotency/drift/shipment binding, applicability, recipients, evidence links, terminal-case and ordering rules, serialization), `CompletenessFactsModelTests`, `CompletenessFactsDomainTests`, the `CompletenessFacts` cases in `MigrationTests`, `EndpointInventoryTests` and `HttpContractTests`. No frontend was exercised.
