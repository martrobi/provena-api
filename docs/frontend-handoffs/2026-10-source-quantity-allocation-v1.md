# Source Quantity Allocation v1: `allocatedQuantity`, `clearSourceAllocations`, derived `sourceAllocation`, policy v3, fingerprint v2, snapshot schema v2

Status: **backend contract implemented (commit `7770612`); the client has not adapted yet.** This document is the input for the client's adaptation. Canonical sources are the code (`Contracts/Requests.cs`, `Contracts/Reviews.cs`, `Contracts/Validators.cs`, `Services/CaseDossierService.cs`, `Services/SourceAllocation.cs`, `Services/CompletenessEvaluator.cs`, `Services/ReviewSnapshot.cs`, `Services/ReviewedExportService.cs`, `Program.cs`), the tests (`SourceAllocationTests` unit and integration, `MigrationTests`) and [ADR 0014](../adr/0014-source-quantity-allocation.md). Contract summary also in [API](../API.md) "Case dossier".

## 1. Summary

- Each plot link of the current lot (`ContributionPlot`) can carry an optional **`allocatedQuantity`**: how much of the lot is attributed to that plot, in the lot unit. `null` means "not recorded". Existing data stays `null`; nothing is inferred or backfilled.
- It is written inside the existing **`PUT /api/eudr-cases/{caseId}/dossier/lot`** (complete replacement). There is **no new route**. Any change of an allocation creates a **new immutable lot version** (`currentLot.id` and `number` change; the old version keeps its values in `lot-history`).
- `PUT` gains **`clearSourceAllocations`** (default `false`). Recorded allocations are **never cleared or kept by omission**: see section 5.
- `CaseLotDto` gains **`sourceAllocation`**, a derived, never stored summary; `ContributionPlotDto` gains **`allocatedQuantity`**.
- Completeness policy is now version **"3"** with a new item **`SOURCE_ALLOCATION_RECONCILED`**.
- New reviews are stored with **fingerprint version 2** and **review snapshot schema 2**; old reviews keep version 1 and stay readable. `ReviewDetailDto.snapshot` is a versioned union selected by `schemaVersion` (no `$type`).
- Wording: "raspodjela količine po izvorima", "pripisana količina". It is quantity attribution. **Never** "compliant", "legally valid", "verified net mass", "risk" or "legal origin".

## 2. Changed endpoints and behaviour

| Route | Who | Change |
| --- | --- | --- |
| `PUT /api/eudr-cases/{caseId}/dossier/lot` | any authenticated member (unchanged) | body gains `contributions[].plots[].allocatedQuantity` and `clearSourceAllocations`; new typed 400 reasons |
| `GET /api/eudr-cases/{caseId}/dossier` | any authenticated member | `currentLot.sourceAllocation`, `currentLot.contributions[].plots[].allocatedQuantity` |
| `GET /api/eudr-cases/{caseId}/dossier/lot-history` | any authenticated member | each version carries its own `sourceAllocation` and plot `allocatedQuantity` (legacy versions: `NotRecorded` / `null`) |
| `GET /api/eudr-cases/{caseId}/completeness` | any authenticated member | `policy.version` `"3"`, new item `SOURCE_ALLOCATION_RECONCILED`, `fingerprintVersion` 2 for the current dossier |
| `POST /api/eudr-cases/{caseId}/reviews` | Owner (unchanged) | new reviews have `fingerprintVersion` 2 and `snapshotSchemaVersion` 2; an unreconciled recorded allocation blocks `AcceptedAsCompleteUnderPolicy` |
| `GET /api/eudr-cases/{caseId}/reviews/{reviewId}` | any authenticated member | `snapshot` is the stored version-1 or version-2 shape |
| `GET /api/eudr-cases/{caseId}/export` | any authenticated member | `manifest.json` `dossier.currentLot` carries the allocation members; `completeness` carries the new item |
| `GET /api/eudr-cases/{caseId}/reviews/{reviewId}/export` | any authenticated member | `review.json` gains `sourceAllocation` and `sourceAllocationScope` |

No request or response contains `organizationId`. Authorization is unchanged. `summary.csv` is unchanged.

## 3. Request contract (PUT lot)

Property names are camelCase and case-insensitive; enums are strings; the body is the **complete** lot.

```json
{
  "lotReference": "LOT-2026-001",
  "quantity": 10,
  "unit": "Kg",
  "note": null,
  "contributions": [
    {
      "producerId": "…", "quantity": 10, "unit": "Kg",
      "plots": [
        { "productionPlotId": "…", "plotRevisionId": "…", "productionStartDate": "2024-01-01", "productionEndDate": "2024-12-31", "allocatedQuantity": 6.5 },
        { "productionPlotId": "…", "plotRevisionId": "…", "productionStartDate": "2024-01-01", "productionEndDate": "2024-12-31", "allocatedQuantity": 3.5 }
      ]
    }
  ],
  "expectedCurrentLotId": "7c1e…-the-currentLot.id-you-loaded-or-null",
  "clearSourceAllocations": false
}
```

| Field | Rules |
| --- | --- |
| `contributions[].plots[].allocatedQuantity` | optional, `null`/omitted = not recorded. When present: **> 0** (zero and negatives are invalid), **< 100000000000000**, at most **6 decimals**. The unit is the lot unit (there is no unit field). |
| all-or-none | within one lot version either **every** plot link has `allocatedQuantity` or **none** has. A partial set is 400. |
| per contribution | the sum of the plot allocations of one contribution must be **<=** that contribution's `quantity` (400 `PlotAllocationExceedsContribution`). Less than the quantity is allowed (draft; derived `UnderAllocated`). |
| `clearSourceAllocations` | optional boolean, default `false`. See section 5. |
| `expectedCurrentLotId` | unchanged (see the [production coverage handoff](2026-10-production-coverage-and-lot-concurrency.md)): exactly the `currentLot.id` you loaded, or `null`/omitted only when the GET answered 204. |
| everything else | unchanged (`lotReference` max 100, `quantity` > 0 with at most 6 decimals, `unit` not `Other`, contribution unit equals the lot unit, `Σ contributions <= lot quantity`, period both-or-neither, caps 200/500/5000) |

Exact decimals: the backend compares by value (`10` equals `10.000000`) with no tolerance, conversion or percentage.

## 4. Response contract

`CaseDossierDto` (GET and PUT) and each `CaseLotDto` of `lot-history`:

```json
{
  "id": "…", "number": 3, "lotReference": "LOT-2026-001", "quantity": 10, "unit": "Kg", "note": null,
  "reconciliation": { "allocatedQuantity": 10, "remainingQuantity": 0, "status": "Reconciled" },
  "contributions": [
    { "id": "…", "producerId": "…", "producerName": "…", "producerIsActive": true, "quantity": 10, "unit": "Kg",
      "plots": [ { "id": "…", "productionPlotId": "…", "plotName": "…", "plotIsActive": true, "plotRevisionId": "…", "revisionNumber": 1,
                   "countryCode": "BR", "geometryType": "Polygon", "areaHectares": 1.5, "revisionSupersededAtUtc": null,
                   "productionStartDate": "2024-01-01", "productionEndDate": "2024-12-31", "allocatedQuantity": 6.5 } ] }
  ],
  "createdByUserId": "…", "createdAtUtc": "…", "supersededAtUtc": null,
  "sourceAllocation": {
    "status": "Matched",
    "lotQuantity": 10, "unit": "Kg", "allocatedQuantity": 10, "unallocatedQuantity": 0,
    "contributions": [ { "producerId": "…", "contributionQuantity": 10, "allocatedQuantity": 10, "unallocatedQuantity": 0, "status": "Matched" } ]
  }
}
```

(The pre-existing `reconciliation` member, producer contributions against the lot, is unchanged and a different thing from `sourceAllocation`.)

`sourceAllocation` is **derived on read, never stored, never overridden**, and is **not** part of `completeness.quantityReconciliation`. Per-plot values live **only** on `plots[].allocatedQuantity` (not duplicated in `sourceAllocation`).

| `status` (lot and per contribution) | Meaning |
| --- | --- |
| `NotRecorded` | no plot link of this lot version has a value (every legacy version, and any version saved without allocations) |
| `MissingData` | allocation is recorded but a contribution has no plot link or one of its plot links has no value |
| `OverAllocated` | allocations exceed the contribution (or the lot). Not producible through the API (writes reject it); defensive |
| `UnderAllocated` | allocations are below the contribution quantity, or below the lot quantity |
| `Matched` | every contribution is fully allocated to its plots **and** the plot total equals the lot quantity (exact decimal equality) |

Lot precedence: `NotRecorded` > `OverAllocated` > `MissingData` > `UnderAllocated` > `Matched`. `unallocatedQuantity` = quantity - allocated total. Contributions that together fall short of the lot are `UnderAllocated` even if each one is fully allocated.

## 5. Error/status behaviour and the silent-clear contract

Order inside one transaction: case lock (404) -> closed/archived (409, untyped) -> body validation (400) -> load the current lot -> **`expectedCurrentLotId` (409 `LotHistoryChanged`)** -> **silent-clear protection (400)** -> identical-resubmission no-op -> `lotReference` uniqueness (409, untyped) -> source locks and checks -> per-contribution allocation check (400) -> supersede and insert. A **stale `expectedCurrentLotId` wins** over the silent-clear 400 and over the identical no-op.

| Status | `errors.reason` | When |
| --- | --- | --- |
| 400 | `SourceAllocationsNotSupplied` | the current lot has recorded allocations, the request carries none (omitted or all `null`) and `clearSourceAllocations` is not `true` |
| 400 | `ClearSourceAllocationsNotApplicable` | `clearSourceAllocations` is `true` but the current lot has no recorded allocation (or no lot exists) |
| 400 | `PlotAllocationExceedsContribution` (+ `errors.producerId`) | the plot allocations of a contribution sum to more than its quantity |
| 400 | (validation, no `reason`) | `errors` is a map from the validator property path to messages: `allocatedQuantity` <= 0 / > max / more than 6 decimals under `Contributions[i].Plots[j].AllocatedQuantity`; the all-or-none message ("Every plot link must carry an allocated quantity when any plot allocation is recorded.") under `Contributions`; "clearSourceAllocations cannot be combined with allocated quantities." under `ClearSourceAllocations` |
| 409 | `LotHistoryChanged` (+ `currentLotId`, `currentLotNumber`) | unchanged; the only optimistic-concurrency conflict |

```json
// 400 SourceAllocationsNotSupplied
{ "status": 400, "title": "Validation failed",
  "detail": "The current lot has recorded source allocations: supply allocatedQuantity for every plot link or set clearSourceAllocations to true.",
  "errors": { "reason": "SourceAllocationsNotSupplied" } }
// 400 PlotAllocationExceedsContribution
{ "status": 400, "title": "Validation failed", "detail": "Plot allocations exceed the contribution quantity.",
  "errors": { "reason": "PlotAllocationExceedsContribution", "producerId": "…" } }
```

Match on `errors.reason`, never on the detail text. Nothing is written by any of these. The silent-clear matrix (current lot has recorded allocations):

| Request | Result |
| --- | --- |
| allocations for every plot link | validated normally; a new version with the new values |
| all `null`/omitted, `clearSourceAllocations` false/omitted | 400 `SourceAllocationsNotSupplied` |
| all `null`/omitted, `clearSourceAllocations: true` | 200, a new version with `sourceAllocation.status` `NotRecorded` (the old version keeps its values in the history) |
| any value together with `clearSourceAllocations: true` | 400 validation error on `ClearSourceAllocations` |

With no recorded allocation, `clearSourceAllocations: true` is 400 `ClearSourceAllocationsNotApplicable`, and a save without allocations is an ordinary save. There is **no silent preservation, no silent clearing and no silent retry**.

## 6. Optimistic concurrency

Unchanged. Load `GET .../dossier`, send `currentLot.id` as `expectedCurrentLotId` (or `null` for 204), do not refetch right before submit, never retry silently. After a successful PUT use the response's `currentLot.id` as the next expected id. An identical PUT (allocations compared by value, `10` = `10.000000`) with the correct id is a 200 no-op with the same lot id and no new version; identical content with a stale id is the 409.

## 7. Completeness, review, fingerprint and snapshot semantics

- **Policy v3, item `SOURCE_ALLOCATION_RECONCILED`** (group `Quantity`, `subjectType` `CaseLot`, `subjectId` the current lot id; no subject when there is no lot). Mapping: `NotRecorded` -> `NotApplicable`, `Matched` -> `Satisfied`, `MissingData` -> `Missing`, `UnderAllocated` -> `Invalid`, `OverAllocated` -> `Invalid`. Render it with the existing completeness item renderer; it appears after `LOT_RECONCILED`.
- Allocation is **optional**. But once recorded, a non-`Matched` allocation makes `dataCoverage` `Incomplete` and **blocks `AcceptedAsCompleteUnderPolicy`** through the existing blocking rule (409 with `failingItems` containing `SOURCE_ALLOCATION_RECONCILED`). `ReturnedForCorrection`, `ConfirmedOutsideRegulatoryScope` and `ConfirmedUnsupportedFlow` keep their existing behaviour. There is **no close-specific allocation gate**.
- **A successful allocation change creates a new `CaseLot` version, so the latest review becomes `Stale`** (`changedAreas` contains `Lot`), also for old version-1 reviews. Closing then fails with the existing `StaleReview`. Do not compute staleness in the client.
- **New reviews:** `fingerprintVersion` 2, `snapshotSchemaVersion` 2, `policyVersion` "3". Old reviews keep their stored versions (1) and `policyChanged: true`; they are not rewritten and stay readable. The completeness `fingerprint` is the version-2 value of the current dossier; send it back unchanged as `expectedFingerprint`.
- **`ReviewDetailDto.snapshot`** is the stored version-1 or version-2 shape. Both carry `schemaVersion` (1 or 2) and the members `schemaVersion`, `facts`, `context`, `completeness`; there is **no `$type`** member. Use `schemaVersion` (the same value as `snapshotSchemaVersion`) to choose the shape. Version 2 differs only in `facts.lot.contributions[].plots[].allocatedQuantity` (nullable). A version-1 snapshot has no such member: read it as "not recorded".
- **OpenAPI:** `ReviewDetailDto.snapshot` is `anyOf` the two shapes (schema names `IReviewSnapshotCaseRevisionSnapshotV1` and `…V2`); there is no discriminator in the document or on the wire. Generate or hand-write the client type as a union keyed by `schemaVersion`.
- **Working export** (`manifest.json`): allocation exists only at `dossier.currentLot.sourceAllocation` and `dossier.currentLot.contributions[].plots[].allocatedQuantity`; there is no top-level `sourceAllocation`. `summary.csv` is unchanged.
- **Reviewed export** (`review.json`): the stored `snapshot` plus siblings `sourceAllocation` (a `SourceAllocationDto`, derived **only** from the stored snapshot, never from live rows; `NotRecorded` for a stored schema-1 review; `null` only if the review has no lot) and `sourceAllocationScope` (a string stating that it is quantity attribution and not a legal net-mass, yield or compliance determination). It stays as reviewed after later edits.

## 8. Breaking-change notes

- **Behaviour change for lot writers:** a PUT that re-saves a lot with recorded allocations but without `allocatedQuantity` (an old form) is now **400 `SourceAllocationsNotSupplied`**, not a silent clear. Forms must load, show and resend the allocations they loaded.
- **Version values change for new reviews** (`policyVersion` "3", `fingerprintVersion` 2, `snapshotSchemaVersion` 2). A client that hard-coded `snapshotSchemaVersion === 1` or `fingerprintVersion === 1` breaks; a client that types `snapshot` as the version-1 shape only is wrong for new reviews.
- Additive: the request fields, `ContributionPlotDto.allocatedQuantity`, `CaseLotDto.sourceAllocation`, the completeness item, `sourceAllocation`/`sourceAllocationScope` in `review.json`.
- Unchanged: routes, `expectedCurrentLotId`, `LotHistoryChanged`, the producer-level `reconciliation`, `quantityReconciliation`, authorization, `summary.csv`.

## 9. Required frontend adaptations at contract level

1. The existing lot editor must send `allocatedQuantity` on each plot link when allocation is being recorded.
2. **Do not auto-fill** an allocation: no equal split, no "single plot = whole contribution" inference, no percentage conversion.
3. Preserve **exact decimal** values end to end (do not round-trip through binary floating point for display or submit; at most 6 decimals).
4. **All-or-none UX:** if the user starts recording source allocation, require every current plot link of the lot to receive a value before submit. Also keep each contribution's allocated sum within its quantity (the backend enforces it; surface the 400 `PlotAllocationExceedsContribution` for `producerId`).
5. **Explicit clear:** never clear allocations by omitting values. Send `clearSourceAllocations: true` (with all `allocatedQuantity` `null`/omitted) only after an explicit user action. Never send the flag when nothing is recorded (400).
6. **Existing allocations:** if loaded values exist, show and resend them in the edit form so an unrelated lot edit does not hit `SourceAllocationsNotSupplied`.
7. Reuse `expectedCurrentLotId` exactly as today.
8. **`LotHistoryChanged`:** the same conscious conflict flow: preserve the draft, refresh the current dossier (`currentLotId`, `currentLotNumber`), show what changed and require a conscious retry. No silent retry.
9. Render `sourceAllocation`. Suggested Croatian labels: section "Raspodjela količine po izvorima"; per plot "Pripisana količina"; "Ukupno pripisano" (`allocatedQuantity`); "Neraspoređeno" (`unallocatedQuantity`); "Status raspodjele" (`status`). Suggested status wording: `NotRecorded` "Raspodjela nije evidentirana"; `MissingData` "Raspodjela nepotpuna (nedostaju podaci)"; `UnderAllocated` "Pripisano manje od količine"; `OverAllocated` "Pripisano više od količine"; `Matched` "Količina je u potpunosti pripisana izvorima". **Do not** call `Matched` compliant, legally valid or verified net mass.
10. Completeness: support `SOURCE_ALLOCATION_RECONCILED` with the existing completeness renderer (including `NotApplicable`).
11. After a successful allocation change (a new `CaseLot` version, which can stale the current review) refresh the case-scoped dependencies that show backend truth: the dossier, lot history, completeness (fingerprint, `reviewFreshness`, `changedAreas`), the latest review, the risk overview, the External DDS record and the lifecycle read. Do not invent client-side fingerprint or review-state logic and do not broadly invalidate unrelated query families.
12. **Snapshot/review UI:** if the client consumes `ReviewDetailDto.snapshot`, support `schemaVersion` 1 and 2 and select the shape by `schemaVersion`. Do not expect `$type`.
13. Do not broaden scope into country risk, legality or deforestation findings, DDS coverage, split/merge or unit conversion; none exists in this slice.
14. Update every existing client helper, live-test helper or script that writes a lot or asserts `policy.version`, `fingerprintVersion` or `snapshotSchemaVersion` to the new values.

## 10. Compatibility risks

- Old lot forms that omit `allocatedQuantity` fail with 400 as soon as a lot has recorded allocations.
- Treating `Matched` as compliance, or an unrecorded allocation as an error, contradicts the contract (allocation is optional).
- Stale review, risk and External DDS panels if the lot save is not wired to their case-scoped refresh.
- Decoding `snapshot` with a version-1-only type, or looking for `$type`, fails for version-2 reviews.
- Matching on `detail` text instead of `errors.reason` is unsupported.

## 11. Out of scope

Mandatory allocation, percentages, unit conversion, tolerance, legal net-mass or yield semantics, multi-lot, split, merge and processing lineage, cross-case plot capacity, country risk, legality and deforestation findings, DDS partial coverage, Annex I scope, and any change to Quantity Reconciliation.

## 12. Backend verification performed and local compatibility evidence

- Backend: `scripts/verify.ps1` passed on the implementation commit `7770612` (restore, Release build with warnings as errors, 298 unit and 375 integration tests against PostgreSQL), including unit `SourceAllocationTests`, integration `SourceAllocationTests` and the four allocation `MigrationTests`. The endpoint inventory is unchanged.
- One additive migration (see [DATABASE](../DATABASE.md)); Down is refused while any allocation exists.
- Local frontend/backend compatibility: **not run.** The client checkout was neither inspected nor changed, and no client live suite was executed for this change. Client adaptation remains to be done and verified per [CHANGE_WORKFLOW](../CHANGE_WORKFLOW.md).
