# Production Coverage & Lot Concurrency v1: derived `productionCoverage`, `expectedCurrentLotId`, country-discrepancy advisory

Status: **backend contract implemented (commit `a9ded9a`); the client has not adapted yet.** This document is the input for the client's adaptation. Canonical sources are the code (`Contracts/Requests.cs`, `Contracts/Reviews.cs`, `Services/CaseDossierService.cs`, `Services/ProductionCoverage.cs`, `Services/CompletenessService.cs`, `Services/ReviewedExportService.cs`), the tests (`CaseDossierCoverageTests`, `CaseDossierTests`, `ProductionCoverageTests`, `ReviewTests`) and [ADR 0007](../adr/0007-case-dossier-lot-attribution.md) (amendment). Contract detail also lives in [API](../API.md) ("Case dossier").

## 1. Summary

- The provenance model is unchanged: Producer, ProductionPlot, PlotRevision, CaseDossier, CaseLot, LotContribution and ContributionPlot. **No `ProductionSource` or `ProductionEvent` entity, no route, table or column was added and no migration was needed.**
- `GET /api/eudr-cases/{caseId}/dossier` gains **`productionCoverage`**: a derived, never stored summary of the current lot's pinned sources.
- `PUT /api/eudr-cases/{caseId}/dossier/lot` gains **`expectedCurrentLotId`** (optimistic concurrency). A stale, wrong or missing id is a typed 409 `LotHistoryChanged`.
- `GET .../completeness` can carry the new informational advisory **`PRODUCTION_COUNTRY_DIFFERS_FROM_SUBMISSION`**.
- The working export carries coverage only at `dossier.productionCoverage`; the reviewed export carries `productionCoverage` and `productionCoverageScope` derived from the retained snapshot.
- Fingerprint version (1) and the reviewed snapshot schema (1) are unchanged. A new lot version stales the latest review through the existing `Lot` section.
- Wording: "production coverage", "derived from the recorded lot sources", "differs from the supplier-submission country". Never "invalid", "non-compliant", "deforestation risk", "country risk" or "legal production country". Production country is **source-level** data of each pinned plot revision; there is no case-level production country, and the supplier country, the shipment origin and the supplier-submission country are not production countries.

## 2. Changed endpoints and behaviour

| Route | Who | Change |
| --- | --- | --- |
| `GET /api/eudr-cases/{caseId}/dossier` | any authenticated member | **200** `CaseDossierDto` now includes `productionCoverage`; **204** when no dossier exists (no coverage); 404 unknown/foreign case |
| `PUT /api/eudr-cases/{caseId}/dossier/lot` | any authenticated member (unchanged) | body gains `expectedCurrentLotId`; new typed 409 `LotHistoryChanged` |
| `GET /api/eudr-cases/{caseId}/dossier/lot-history` | any authenticated member | unchanged (`CaseLotDto[]`, newest first) |
| `GET /api/eudr-cases/{caseId}/completeness` | any authenticated member | `advisories` can contain `PRODUCTION_COUNTRY_DIFFERS_FROM_SUBMISSION` |
| `GET /api/eudr-cases/{caseId}/export` | any authenticated member | `manifest.json` `dossier` includes `productionCoverage` |
| `GET /api/eudr-cases/{caseId}/reviews/{reviewId}/export` | any authenticated member | `review.json` gains `productionCoverage` and `productionCoverageScope` |

No request or response contains `organizationId`. Authorization is unchanged.

## 3. Request contract (PUT lot)

Property names are camelCase and case-insensitive. Enums are strings. Dates are ISO `yyyy-MM-dd`. The body is the **complete** lot.

```json
{
  "lotReference": "LOT-2026-001",
  "quantity": 10,
  "unit": "Kg",
  "note": null,
  "contributions": [
    {
      "producerId": "…", "quantity": 10, "unit": "Kg",
      "plots": [ { "productionPlotId": "…", "plotRevisionId": "…", "productionStartDate": "2024-01-01", "productionEndDate": "2024-12-31" } ]
    }
  ],
  "expectedCurrentLotId": "7c1e…-the-currentLot.id-you-loaded-or-null"
}
```

| Field | Rules |
| --- | --- |
| `expectedCurrentLotId` | `null` or omitted: the client loaded a state with **no** current lot (the GET answered 204). Otherwise exactly the `currentLot.id` of the dossier that was loaded. |
| everything else | unchanged (see [API](../API.md): `lotReference` max 100, `quantity` > 0 with at most 6 decimals, `unit` not `Other`, contribution unit equals lot unit, period both-or-neither with start <= end, caps 200/500/5000) |

An absent property is read as `null`: a client that omits it can still create the first lot but can never replace an existing one.

## 4. Response contract

`CaseDossierDto` (GET and PUT return the same shape):

```json
{
  "id": "…", "eudrCaseId": "…", "createdAtUtc": "…",
  "shipmentQuantity": 10, "shipmentUnit": "Kg",
  "currentLot": { "id": "…", "number": 2, "lotReference": "LOT-2026-001", "quantity": 10, "unit": "Kg", "note": null,
                  "reconciliation": { "allocatedQuantity": 10, "remainingQuantity": 0, "status": "Reconciled" },
                  "contributions": [ "…unchanged…" ], "createdByUserId": "…", "createdAtUtc": "…", "supersededAtUtc": null },
  "productionCoverage": {
    "countries": ["BR", "CO"],
    "periodFrom": "2024-01-15",
    "periodTo": "2024-04-30",
    "sourcesWithoutPeriod": 1,
    "producerCount": 2,
    "plotCount": 3
  }
}
```

`productionCoverage` is **derived on read, never stored, never fingerprinted**:

| Member | Meaning |
| --- | --- |
| `countries` | distinct, ordinally sorted `countryCode`s of the **pinned** plot revisions of the current lot (`[]` when the lot has no plot link) |
| `periodFrom` | minimum `productionStartDate` among links that have one, else `null` |
| `periodTo` | maximum `productionEndDate` among links that have one, else `null` |
| `sourcesWithoutPeriod` | number of plot links missing a start or an end date |
| `producerCount` | number of contributing producers (a contribution without plot links still counts) |
| `plotCount` | number of distinct production plots |

It follows the lot version: a newer `PlotRevision` of a pinned plot does **not** change it until a new lot version pins that revision. It is evidence arithmetic; it is not a completeness item, not a legal-window check and not a risk indicator.

Completeness advisory (existing `advisories[]` shape `{ code, area, subjectId, message }`):

```json
{ "code": "PRODUCTION_COUNTRY_DIFFERS_FROM_SUBMISSION", "area": "Production", "subjectId": "…current supplier submission id…",
  "message": "The production countries of the pinned lot sources (BR, CO) differ from the country of production in the supplier submission (BR). Evidence inconsistency information only; it is not a compliance finding." }
```

## 5. Error/status behaviour

| Status | When |
| --- | --- |
| 401 | anonymous |
| 404 | unknown/foreign route case (whatever the body and the id) |
| 400 | invalid body (checked **before** the expected id: an invalid body with a stale id is 400) |
| 409 | Closed or archived case (existing, untyped), `lotReference` already used by another case (existing, untyped), **`LotHistoryChanged`**, active/carry-over conflicts (existing, untyped) |

Order inside one transaction: case lock (404), closed/archived (409), body validation (400), load the current lot, **expected id comparison (409 `LotHistoryChanged`)**, identical-resubmission no-op, `lotReference` uniqueness, source locks and checks, supersede and insert.

```json
// 409 LotHistoryChanged
{ "status": 409, "detail": "Lot history changed since it was loaded",
  "errors": { "reason": "LotHistoryChanged", "currentLotId": "…", "currentLotNumber": 2 } }
```

`currentLotId` and `currentLotNumber` are `null` when no lot exists (the client sent a non-null id for a case without one). Match on `errors.reason`, never on the detail text. A conflict writes nothing and records no audit event.

## 6. Optimistic concurrency

- Load `GET .../dossier`. If 200, send `currentLot.id` unchanged as `expectedCurrentLotId`; if 204, send `null`.
- Use the id of the **form's loaded state**. Do **not** re-fetch immediately before submitting to obtain a fresh id: that defeats the check.
- Identical content with the correct id is a 200 no-op (same lot, no new version, no audit event). Identical content with a wrong or missing id is **not** a no-op: it is the 409.
- The comparison happens under the case lock, so of several simultaneous writers exactly one gets 200 and the others `LotHistoryChanged`. There is no silent retry on the server.
- After a successful PUT use `currentLot.id` of the response as the next expected id.

## 7. Fingerprint, review and export semantics

- `FingerprintVersion` stays 1 and the snapshot schema stays 1. Nothing about coverage is fingerprinted or stored in the snapshot.
- A new lot version (any material change) makes the latest review `Stale` with `changedAreas` `["Lot"]`, through the existing fingerprint. Closing then fails with the existing `StaleReview`; a new review (and, for `ExternalDdsCompleted`, a new risk assessment and External DDS record of that review) is required.
- A later `PlotRevision` of a pinned plot or a producer rename changes neither the pinned `ContributionPlot` rows nor any stored snapshot, and does not stale a review (the existing `PINNED_REVISION_SUPERSEDED` advisory still appears).
- The advisory is **informational**: not a completeness item, outside `dataCoverage`, not a review precondition, not a compliance finding, not fingerprinted.
- **Working export** (`manifest.json`): coverage exists **only** at `dossier.productionCoverage`. There are **no** top-level `productionCoverage` or `productionCoverageScope` members. When `dossier` is `null` there is no coverage. `summary.csv` is unchanged.
- **Reviewed export** (`review.json`): `productionCoverage` (same member shape) is derived **only** from the retained snapshot's lot facts, never from live producer, plot, revision or lot rows, so it stays as reviewed after later edits. `productionCoverageScope` is the string "Derived production coverage of the recorded lot sources (distinct source countries, period envelope and counts). Evidence information only; not a legal determination, risk assessment or compliance finding." Both are siblings of `snapshot`; the snapshot JSON itself is unchanged.

## 8. Advisory semantics

- Code `PRODUCTION_COUNTRY_DIFFERS_FROM_SUBMISSION`, `area` `Production`, `subjectId` the current supplier-submission id; the message carries country codes only.
- It compares the derived source countries **only** with `SupplierSubmission.CountryOfProductionCode`. It never compares with `Shipment.OriginCountryCode` or the supplier country.
- Emitted when both sides exist and the distinct source countries are **not exactly** that one country. Example: sources BR + CO against a submission of BR is a discrepancy; sources BR against BR is not. Case and padding are ignored.
- Not emitted without a source country or without a submission country.
- It is information about inconsistent evidence, not a defect of the lot and not a finding: the supplier's statement is never overridden and the lot is never rejected for it.

## 9. Breaking-change notes

- **Breaking for every lot writer:** a PUT without the id of the existing current lot now fails with 409 `LotHistoryChanged` (previously it replaced silently). A client that omits the field only works for the first lot of a case. **Any existing client helper, script or test that writes a lot must follow the same `expectedCurrentLotId` contract.**
- Additive: `productionCoverage` on `CaseDossierDto`, the new advisory code, `dossier.productionCoverage` in the working manifest, `productionCoverage` and `productionCoverageScope` in `review.json`.
- Unchanged: routes, the lot input otherwise, `lot-history`, fingerprint version 1, the snapshot schema, `summary.csv`, authorization, `lotReference` uniqueness behaviour.

## 10. Required frontend adaptations at contract level

1. Keep `currentLot.id` from the dossier the form was loaded with and send it unchanged as `expectedCurrentLotId`; send `null` only when no current lot existed when the form loaded (204). Do not refetch right before submit.
2. On **`LotHistoryChanged`**: refresh the current dossier and lot (`currentLotId` and `currentLotNumber` say what changed), preserve the entered values where practical, show what changed, and require a **conscious** retry with the new id. Never retry silently or automatically.
3. Render `productionCoverage` as a **derived provenance summary** of the recorded sources (countries, period envelope, counts, sources without a period). Treat `null` period bounds as "not recorded". Do not present it as legal coverage, completeness, or compliance.
4. Show `PRODUCTION_COUNTRY_DIFFERS_FROM_SUBMISSION` as **informational** (for example a neutral note beside the coverage), not as an error. Do not label the lot invalid or non-compliant, do not block saving or review because of it, and do not recolor completeness or `dataCoverage`.
5. Do not infer country risk, deforestation risk or legality from `productionCoverage` or the advisory. Do not derive a case-level production country; do not use the supplier country or the shipment origin as one.
6. After a successful new lot version, refresh the case-scoped dependencies that surface stale backend state: completeness (fingerprint, `reviewFreshness`, `changedAreas` containing `Lot`, advisories), the latest review, the risk overview, the External DDS record and the lifecycle read. Do **not** broadly invalidate unrelated query families (other cases, the producer/plot catalogue, products, suppliers).
7. Closed or archived cases: the PUT is 409 (untyped); the existing read-only handling applies.
8. Update every existing client helper that writes a lot (forms, live-test helpers, scripts) to load and send the id.

## 11. Compatibility risks

- Old clients that PUT a lot repeatedly without an expected id fail from the second write.
- Treating the advisory as a blocking validation error would contradict the contract; treating `countries` as a single case-level country would be wrong when sources span several countries.
- Stale review, risk and External DDS panels if the lot save is not wired to their case-scoped refresh.
- Matching on `detail` text instead of `errors.reason` is unsupported.

## 12. Out of scope

The Supplier-Producer relationship, producer/plot duplicate identity rules, legal production-date windows, geometry precision and area policy, `GeoSubmission` to plot conversion, per-plot quantity allocation, country risk, legality and deforestation findings, Annex I scope, multi-lot, split, merge and processing lineage, any change to the fingerprint, snapshot, review, risk, External DDS or concern semantics, and any new route, entity or migration.

## 13. Backend verification performed and local compatibility evidence

- Backend: `scripts/verify.ps1` passed on the implementation commit `a9ded9a` (restore, Release build with warnings as errors, 277 unit and 357 integration tests against PostgreSQL), including `ProductionCoverageTests` (9 unit test methods, 13 cases), `CaseDossierCoverageTests` (12 integration test methods) and the reviewed-export coverage test in `ReviewTests`. The route inventory was unchanged.
- No migration; model snapshot unchanged (see [DATABASE](../DATABASE.md)).
- Local frontend/backend compatibility: **not run.** The client checkout was neither inspected nor changed, and no client live suite was executed for this change. Client adaptation remains to be done and verified per [CHANGE_WORKFLOW](../CHANGE_WORKFLOW.md).
