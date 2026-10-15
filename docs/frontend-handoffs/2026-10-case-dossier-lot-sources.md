# Case dossier: lot, producer contributions and pinned plot revisions

> **Update (Case Lifecycle v2):** `Completed` and `Archived` are no longer statuses and the 409 text for a non-editable case changed; "Completed/Archived" below now means a Closed or archived case, and `Complete` is replaced by an Owner-backed close. See [the lifecycle handoff](2026-10-case-lifecycle-v2.md).

> **Update (Quantity Reconciliation v1):** `reconciliation.status` on the dossier DTOs still compares the producer contributions with the lot only. The lot is now also compared with the shipment and the supplier-confirmed quantity by the completeness `quantityReconciliation`; `shipmentQuantity`/`shipmentUnit` remain display fields. See [quantity reconciliation](2026-10-quantity-reconciliation.md).

## 1. Summary

Three new internal endpoints let an operator record, per EUDR case, the facts about the case's **one physical lot**: the lot header (a business/batch reference, quantity, unit), the **producers** that contributed material with their quantities, and for each contribution the **production plots** it came from, each pinned to an **exact plot revision** with an optional **production period**. The lot is saved as one complete document (`PUT`); every material change creates a new immutable version, and earlier versions stay readable.

This is working evidence only. It is not a completeness, sufficiency, risk or compliance decision, it does not change requirement statuses or the case status, and it is not a reviewed snapshot.

**One existing endpoint changes behaviour (breaking for clients that offer shipment editing):** once a case has a dossier, `PUT /api/shipments/{id}` returns 409.

## 2. Changed endpoints or behaviour

All routes are authenticated, internal (`/api`), JSON only. There is no public/token route, no POST and no DELETE (405).

| Endpoint | Behaviour | Kind |
| --- | --- | --- |
| `GET /api/eudr-cases/{caseId}/dossier` | 200 `CaseDossierDto`; **204 with no body when the case has no dossier yet**; 404 unknown/foreign case | New |
| `PUT /api/eudr-cases/{caseId}/dossier/lot` | Complete replacement of the lot. Creates the dossier on first save. Returns 200 `CaseDossierDto` | New |
| `GET /api/eudr-cases/{caseId}/dossier/lot-history` | `CaseLotDto[]`, newest first, unpaginated; `[]` when there is no dossier | New |
| `PUT /api/shipments/{id}` | **409 once the case has a dossier** (see section 6) | **Changed (breaking)** |
| `GET /api/eudr-cases/{caseId}/export` | `manifest.json` gains `dossier`, `dossierScope`, `retainedNotIncluded.supersededLotVersions`; the ZIP gains `plots/*.geojson` | Additive |

## 3. Request/query contract

`PUT /api/eudr-cases/{caseId}/dossier/lot` takes the **whole lot**. Anything omitted is removed from the new version.

```json
PUT /api/eudr-cases/{caseId}/dossier/lot
{
  "lotReference": "LOT-2026-0042",
  "quantity": 1000.5,
  "unit": "Kg",
  "note": "Harvest batch B",
  "contributions": [
    { "producerId": "…", "quantity": 600.25, "unit": "Kg",
      "plots": [
        { "productionPlotId": "…", "plotRevisionId": "…",
          "productionStartDate": "2026-01-10", "productionEndDate": "2026-03-15" },
        { "productionPlotId": "…", "plotRevisionId": "…",
          "productionStartDate": null, "productionEndDate": null } ] },
    { "producerId": "…", "quantity": 400.25, "unit": "Kg", "plots": [] }
  ]
}
```

- `lotReference`: required, trimmed, max 100. It is a **versioned business/batch reference, not a permanent lot identity** (see below).
- `quantity` (lot and each contribution): > 0, < 1e14, at most 6 decimal places. `unit`: `Kg`, `Ton`, `Piece` or `Liter` as a string; `Other` and integer values are rejected. Every contribution `unit` must equal the lot `unit`; there is no unit conversion.
- `note`: optional, max 4000; blank becomes null.
- No duplicate `producerId` in the lot and no duplicate `productionPlotId` within a contribution. Limits: 200 contributions, 500 plot links per contribution, 5000 links per lot.
- `plotRevisionId` is **required**: the client chooses the exact revision (the server never picks "current"). A superseded revision may be pinned. The plot must belong to the contribution's producer and the revision to that plot.
- Production period: omit both dates or send both (`yyyy-MM-dd`) with start <= end. One date alone is 400; equal dates mean a single day. The dates are factual data only (no cut-off-date or sufficiency rule).
- `contributions` may be empty (an under-allocated lot).

## 4. Response contract

No response contains `organizationId` or geometry. Enums are strings, dates are `yyyy-MM-dd`, decimals are JSON numbers.

```json
200 CaseDossierDto
{ "id": "…dossier id…", "eudrCaseId": "…", "createdAtUtc": "…",
  "shipmentQuantity": 1000, "shipmentUnit": "Kg",
  "currentLot": {
    "id": "…lot version id…", "number": 2, "lotReference": "LOT-2026-0042",
    "quantity": 1000.5, "unit": "Kg", "note": "Harvest batch B",
    "reconciliation": { "allocatedQuantity": 1000.5, "remainingQuantity": 0, "status": "Reconciled" },
    "contributions": [
      { "id": "…", "producerId": "…", "producerName": "Coop Alpha", "producerIsActive": true,
        "quantity": 600.25, "unit": "Kg",
        "plots": [
          { "id": "…", "productionPlotId": "…", "plotName": "North field", "plotIsActive": true,
            "plotRevisionId": "…", "revisionNumber": 1, "countryCode": "BR", "geometryType": "Polygon",
            "areaHectares": 12.5, "revisionSupersededAtUtc": "…or null…",
            "productionStartDate": "2026-01-10", "productionEndDate": "2026-03-15" } ] } ],
    "createdByUserId": "…", "createdAtUtc": "…", "supersededAtUtc": null } }
```

- `id` of the dossier is the **stable anchor of the case's physical lot**; it never changes across versions. `currentLot.id` changes with every new version. Key the lot by case id / dossier id, never by `lotReference`.
- `reconciliation.status`: `Reconciled` (contributions sum exactly to the lot quantity) or `UnderAllocated` (less than the lot quantity, including no contributions). Over-allocation can never be stored (400). `shipmentQuantity`/`shipmentUnit` are for display only; they are not compared with the lot.
- Contributions are ordered by `producerName` then id; plots by `plotName` then id. Names and `...IsActive` flags are **live master data**, not snapshots; `revisionNumber`, `countryCode`, `geometryType`, `areaHectares` describe the pinned revision, and `revisionSupersededAtUtc` is non-null when a newer revision of that plot exists now (the pin is unchanged).
- `GET .../lot-history` returns `CaseLotDto[]` (the `currentLot` shape) newest first by `number`; exactly one entry has `supersededAtUtc: null`. Unpaginated by design (per-case, pilot scale; pagination may become a contract change).
- Geometry is not part of these DTOs. Load it with `GET /api/production-plots/{plotId}/revisions/{revisionId}`.

## 5. Error/status behaviour

| Situation | Status |
| --- | --- |
| No/invalid bearer token | 401 |
| Unknown or another organization's case in the route (on every dossier route, **even with an invalid PUT body**) | 404 |
| `GET dossier` for a case without a dossier | 204 (no body) |
| Case is `Completed` or `Archived` (PUT; checked before body validation) | 409 |
| Invalid body: missing/blank reference, quantity/precision, unit `Other`, unit mismatch, duplicates, caps, bad or one-sided period, start > end | 400 (ProblemDetails with `errors`) |
| Contributions sum to more than the lot quantity | 400, nothing saved |
| Unknown or other-organization `producerId` / `productionPlotId` / `plotRevisionId` in the body | 400 `... was not found in your organization.` (non-disclosing) |
| Plot not owned by the contribution's producer; revision not of the submitted plot | 400 |
| **New** producer or plot inactive (see below) | 409 |
| `lotReference` already used by another case's **current** lot in the organization (also when two requests race) | 409 `Lot reference is already used by another case in your organization.` |
| `PUT /api/shipments/{id}` when the case has a dossier | 409 |
| DELETE / POST on dossier routes | 405 |
| Syntactically malformed JSON, or integer enum values | 400 (framework binding) |

Order of checks for PUT: route case (404), case editable (409), body validation (400), then references/business rules. Invalid body on a terminal case is therefore 409, not 400.

**Active / carry-over.** A producer, plot or pinned revision that is *new compared with the current version* must be active (otherwise 409): adding a producer, adding a plot, or pinning a different revision (even of an inactive plot). Sources already present in the current version (same producer; same producer + plot + exact revision) remain acceptable after the producer or plot is deactivated, and their contribution quantity and production period can still be corrected. Clients should not offer inactive producers/plots for new attribution, but should keep existing ones editable.

**`lotReference` uniqueness.** It is unique only among *current* lots of the organization. The same case can keep it across versions; after a case changes its reference, the old value can be used by another case; another organization is independent. Treat it as an editable label, not an identifier.

## 6. Breaking-change notes

- **Shipment editing is locked once a dossier exists.** `PUT /api/shipments/{id}` already returned 409 after a supplier request or evidence submission; it now also returns 409 once the case has any saved lot, with the message `Shipment cannot be edited after a supplier request, evidence submission or case dossier lot. Archive and create a new shipment to preserve reviewed evidence.` Clients must stop offering shipment editing (quantity, supplier, product, reference…) for a case that has a dossier (`GET .../dossier` returns 200). Shipment creation and archiving (`DELETE`) are unchanged.
- No existing response shape changed. `GET /api/eudr-cases/{id}` (case details) does not include the dossier.

## 7. Required frontend adaptations (contract level)

- Add a dossier view/editor per case: read with `GET .../dossier` (treat 204 as "not started") and save the **entire** lot with `PUT`; build the body from the last response, changing only what the operator edited.
- Offer producers and plots from the existing provenance endpoints and send the exact `plotRevisionId` chosen (typically the plot's current revision at selection time). Show when a pinned revision has since been superseded (`revisionSupersededAtUtc`) without silently changing it.
- Show `reconciliation.status` and `remainingQuantity`; surface 400 for over-allocation and unit mismatch inline. `UnderAllocated` is a valid saved state, not an error.
- Handle 409 as a business conflict with distinct messages (inactive new producer/plot, duplicate current lot reference, terminal case).
- Re-sending an unchanged lot (in any order) is safe: it returns 200 with the same `currentLot.id` and creates no version.
- Disable shipment editing when a dossier exists. Show history from `lot-history`.
- Do not present stored lot attribution as EUDR-complete, sufficient or compliant.

## 8. Compatibility risks

- The shipment lock is a status-code change on an existing endpoint; existing clients that edit shipments after a dossier exists will start receiving 409.
- Names and active flags in the DTO are live and change when master data changes; a cached dossier may show old names.
- The dossier id is stable but `currentLot.id` changes on every material save; do not store the lot id as the lot's identity.
- Plot geometry now appears in the export ZIP (internal download), which may matter for download/sharing UX.

## 9. Out-of-scope items

Applicability, Article 9 completeness (including whether production periods are required), cut-off-date checks, 4 ha/point rules, risk and mitigation, release/approval, reviewed dossier snapshots and review-decision history, DDS/EU Information System, supplier-producer relations, supplier-portal provenance editing, multi-lot cases, split/merge/processing, unit conversion, quantity tolerance, per-plot quantities, converting case geolocation into plots, and any frontend code.

Export additions (additive): `manifest.json` gains `dossier` (the `CaseDossierDto` shape or `null`), `dossierScope` ("Current operator dossier lot at export time; not a reviewed dossier snapshot and not a completeness or compliance determination.") and `retainedNotIncluded.supersededLotVersions`; `plots/{plotRevisionId:N}.geojson` holds the normalized geometry of each distinct revision pinned by the current lot (superseded lot versions are not exported). `evidenceScope` and the `summary.csv` header are unchanged.

## 10. Backend verification performed

`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1`: Release build 0 warnings / 0 errors, 62 unit tests and 94 integration tests passed, 0 failed, 0 skipped, exit 0 (PostgreSQL 17 via Testcontainers). Covered by `CaseDossierTests` (lifecycle, pinning, active/carry-over, reconciliation, units/periods, identifier errors, tenant isolation, route-before-body ordering, reference uniqueness and race, idempotency, shipment lock, terminal cases, concurrency including a deterministic `FOR SHARE` vs in-flight deactivation check, export, GeoSubmission/evidence-history regression, id-only audit), `DossierDomainTests`, `EndpointInventoryTests` and `HttpContractTests` (401s, OpenAPI paths 40, serialization). Client compatibility was not tested (no frontend).
