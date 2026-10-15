# Quantity Reconciliation v1: derived quantity checks, completeness policy v2 and the acceptance and close gates

Status: **backend contract implemented; the client has not adapted yet.** This document is the input for the client's adaptation. Canonical sources are the code, the tests, [API](../API.md) ("Completeness", "Case lifecycle", "Uploads and supplier capability"), [EUDR_DOMAIN](../EUDR_DOMAIN.md) ("Quantity Reconciliation v1") and [ADR 0010](../adr/0010-quantity-reconciliation-v1.md). This is a handoff, not a second API specification.

## 1. Summary

- The backend now compares the recorded quantities of a case and returns a **structured result**, `quantityReconciliation`, on `GET /api/eudr-cases/{caseId}/completeness` (and in the working export manifest). It is derived on every read, never stored and **cannot be overridden**.
- Three comparisons, all by **exact decimal equality in one unit, with no unit conversion and no tolerance**: Shipment == current CaseLot; supplier-confirmed quantity == Shipment; sum of producer contributions == CaseLot.
- **Pilot single-lot assumption:** one shipment/case is represented by exactly one current CaseLot. The Shipment == CaseLot rule is not a general rule and does not describe multi-lot or split flows (not supported).
- The supplier-confirmed quantity has **no unit of its own**; it is taken to be in the shipment's unit (`unitImplied: true`).
- The completeness policy is now **version `"2"`**. Two new Quantity items exist and `LOT_RECONCILED` changed value for partial allocations.
- A non-matched result **blocks `AcceptedAsCompleteUnderPolicy`** (review creation) and **blocks closing as `ExternalDdsCompleted`**. It never blocks `ConfirmedOutsideRegulatoryScope`, `ConfirmedUnsupportedFlow` or `ReturnedForCorrection`, nor closing with the two non-DDS outcomes.
- A supplier's confirmed quantity that differs from the shipment is **accepted** on submission and judged afterwards (submission is not acceptance). The only new public validation is at most 6 decimals.
- Not changed: routes, success status codes (a successful close is still **201 Created**), the dossier fingerprint (`fingerprintVersion` stays **1**) and the review snapshot schema (stays **1**). No database migration.

## 2. Changed endpoints and behaviour

No route is added, removed or renamed (`EndpointInventoryTests` is unchanged).

| Endpoint | Change |
| --- | --- |
| `GET /api/eudr-cases/{caseId}/completeness` | New response member `quantityReconciliation`; `policy.version` is `"2"`; items `SHIPMENT_LOT_QUANTITY_MATCH` and `SUPPLIER_CONFIRMED_QUANTITY_MATCH` added; `LOT_RECONCILED` can now be `Invalid`; `dataCoverage` can be `Incomplete` for more cases (section 4). |
| `GET /api/eudr-cases/{caseId}/export` (working export) | `manifest.json` -> `completeness` carries the same `quantityReconciliation` and the policy-v2 items. |
| `POST /api/eudr-cases/{caseId}/reviews` | `AcceptedAsCompleteUnderPolicy` now also fails with 409 when a Quantity item is `Missing` or `Invalid` (section 6). Other decisions unchanged. |
| `POST /api/eudr-cases/{caseId}/close` | New 409 reason `QuantityNotReconciled` for the `ExternalDdsCompleted` outcome (section 6). Success is unchanged (**201**). |
| `PUT /public/supplier-requests/{token}/details` | `confirmedQuantity` with more than 6 decimals is now **400**. A quantity that differs from the shipment is still **204**. |
| `GET /api/eudr-cases/{caseId}/reviews`, `.../reviews/{id}` | Shapes unchanged. Reviews made before this change report `policyVersion: "1"`; `latestReview.policyChanged` is `true` for them. A stored snapshot's `completeness.items` keep the codes and statuses they had when written. |

## 3. Request contracts

No request body changes. Relevant constraints:

- `PUT /public/supplier-requests/{token}/details` -> `confirmedQuantity` (optional decimal): > 0, < 1e14, **at most 6 decimal places**; otherwise 400 ProblemDetails with `errors.ConfirmedQuantity`. There is still no unit field on this request.
- `PUT /api/eudr-cases/{caseId}/dossier/lot` is unchanged (quantities > 0, at most 6 decimals, unit not `Other`, contribution unit equal to the lot unit).
- `PATCH /api/eudr-cases/{caseId}/requirements/{id}` with `{ "status": "NotRequired", "reviewerNote": "..." }`: the reviewer API still **accepts `NotRequired` without a note** (unchanged). Whether it counts as a valid waiver is decided as in section 4.

## 4. Response contracts

### `quantityReconciliation` (new, always present)

```json
{
  "outcome": "QuantityMismatch",
  "checks": [
    {
      "code": "SHIPMENT_LOT", "status": "QuantityMismatch",
      "expected": { "source": "Shipment", "quantity": 10, "unit": "Kg", "unitImplied": false },
      "actual":   { "source": "CaseLot",  "quantity": 25, "unit": "Kg", "unitImplied": false },
      "difference": 15, "missingFields": [],
      "reason": "The shipment quantity (10 Kg) does not equal the case lot quantity (25 Kg); difference 15 Kg."
    },
    {
      "code": "SUPPLIER_CONFIRMED_SHIPMENT", "status": "NotApplicable",
      "expected": { "source": "Shipment", "quantity": 10, "unit": "Kg", "unitImplied": false },
      "actual":   { "source": "SupplierSubmission", "quantity": null, "unit": "Kg", "unitImplied": true },
      "difference": null, "missingFields": [],
      "reason": "Not applicable: the quantity confirmation is marked NotRequired with a recorded basis."
    },
    {
      "code": "CONTRIBUTIONS_LOT", "status": "Matched",
      "expected": { "source": "CaseLot", "quantity": 25, "unit": "Kg", "unitImplied": false },
      "actual":   { "source": "ProducerContributions", "quantity": 25, "unit": "Kg", "unitImplied": false },
      "difference": 0, "missingFields": [],
      "reason": "Producer contributions (25 Kg) equal the lot quantity exactly."
    }
  ]
}
```

Exact types (JSON uses camelCase property names and string enums; `reason` is human-readable English text, not a stable code):

| Type | Members |
| --- | --- |
| `QuantityReconciliationResult` | `outcome: QuantityReconciliationOutcome`, `checks: QuantityCheck[]` (always exactly 3, in the order below) |
| `QuantityCheck` | `code: string`, `status: QuantityCheckStatus`, `expected: QuantityOperand`, `actual: QuantityOperand`, `difference: number \| null`, `missingFields: string[]`, `reason: string` |
| `QuantityOperand` | `source: string`, `quantity: number \| null`, `unit: QuantityUnit \| null`, `unitImplied: boolean` |
| `QuantityReconciliationOutcome` | `Matched \| MissingData \| UnitMismatch \| QuantityMismatch` |
| `QuantityCheckStatus` | `Matched \| MissingData \| UnitMismatch \| QuantityMismatch \| NotApplicable` |
| `QuantityUnit` | `Kg \| Ton \| Piece \| Liter \| Other` (a lot is never `Other`) |

- **Check `code` values, in this fixed order:** `SHIPMENT_LOT`, `SUPPLIER_CONFIRMED_SHIPMENT`, `CONTRIBUTIONS_LOT`.
- **`source` values:** `Shipment`, `CaseLot`, `SupplierSubmission`, `ProducerContributions`.
- **`missingFields` values (camelCase names, not API paths):** `caseLot`, `producerContributions`, `supplierConfirmedQuantity`, `quantityConfirmationWaiverBasis`. Empty unless `status` is `MissingData`.
- **`difference`** = `actual.quantity - expected.quantity`; `null` unless both quantities are present in the same unit (so `null` for `MissingData` and `UnitMismatch`); `0` for `Matched`.
- **`quantity`/`unit` may be `null`** when the operand does not exist (for example no lot -> `CaseLot` operand `{ quantity: null, unit: null }`). Contributions with mixed units give `unit: null`. With no contributions the `ProducerContributions` operand is `quantity: 0`, `unit` = the lot unit.
- **Decimals are JSON numbers.** Compare and display them as decimals (up to 6 places), never as binary floats; `10` and `10.000000` are the same quantity.
- **`outcome` precedence:** `UnitMismatch` > `QuantityMismatch` > `MissingData` > `Matched`. `NotApplicable` checks do not count; `Matched` means every applicable check matched.

### Check rules (what each `status` means)

| Check | `Matched` | `QuantityMismatch` | `UnitMismatch` | `MissingData` | `NotApplicable` |
| --- | --- | --- | --- | --- | --- |
| `SHIPMENT_LOT` | same unit, equal quantity | same unit, different quantity | shipment unit differs from the lot unit (also any shipment in `Other`) | no current lot (`caseLot`) | never |
| `SUPPLIER_CONFIRMED_SHIPMENT` | a confirmed quantity equals the shipment | a confirmed quantity differs from the shipment | never (the unit is implied) | no confirmed quantity and no valid waiver | no confirmed quantity and `NotRequired` with a nonblank reviewer note (or no requirement row) |
| `CONTRIBUTIONS_LOT` | contributions sum to the lot, same unit | sum differs (under- or over-allocation) | a contribution unit differs from the lot unit | no lot, or no contributions | never |

Supplier-confirmed quantity rules (the part most likely to surprise a client):

1. **Every existing confirmed quantity is compared with the shipment**, whatever the `QuantityConfirmation` requirement status is (`Submitted`, `Accepted`, `NotRequired`, ...).
2. When **no** confirmed quantity exists, the check mirrors the `REQUIREMENT_QuantityConfirmation` item and **does not consult `IsRequired`**:
   - requirement `NotRequired` with a **nonblank** `reviewerNote` (the recorded basis) -> `NotApplicable`;
   - requirement `NotRequired` **without** a nonblank note (missing, empty or whitespace) -> `MissingData`, `missingFields: ["quantityConfirmationWaiverBasis"]`;
   - any other requirement state -> `MissingData`, `missingFields: ["supplierConfirmedQuantity"]`.
3. Consequently the reconciliation is never `Matched` while `REQUIREMENT_QuantityConfirmation` is `Missing` because a `NotRequired` waiver has no basis.

### Completeness items (group `Quantity`, always in this order)

| `code` | `subjectType` / `subjectId` | `Satisfied` | `Missing` | `Invalid` | `NotApplicable` |
| --- | --- | --- | --- | --- | --- |
| `LOT_PRESENT` (unchanged) | `CaseLot` / lot id; both `null` when no lot | a lot is recorded | no lot | never | never |
| `SHIPMENT_LOT_QUANTITY_MATCH` (**new**) | `CaseLot` / lot id; both `null` when no lot | `SHIPMENT_LOT` is `Matched` | no lot | quantity or unit mismatch | never |
| `SUPPLIER_CONFIRMED_QUANTITY_MATCH` (**new**) | `SupplierSubmission` / submission id when a submission exists, else both `null` | `Matched` | missing data (see rule 2) | quantity mismatch | valid waiver |
| `LOT_RECONCILED` (code kept) | `CaseLot` / lot id; both `null` when no lot | `CONTRIBUTIONS_LOT` is `Matched` | no lot or no contributions | under-/over-allocation or unit mismatch | never |

Mapping: `Matched` -> `Satisfied`, `MissingData` -> `Missing`, `UnitMismatch` and `QuantityMismatch` -> `Invalid`, `NotApplicable` -> `NotApplicable`. An item's `reason` is the corresponding check's `reason`.

### Other changed values

- `policy.version`: `"1"` -> **`"2"`**; `policy.limitations` text for quantities changed (it states the pilot single-lot assumption, the implied unit and the waiver rule).
- `LOT_RECONCILED` with a non-zero under- or over-allocation: `Missing` -> **`Invalid`**. With no contributions it is still `Missing`.
- `dataCoverage` is `Incomplete` whenever a Quantity item is `Missing` or `Invalid`. In particular a required quantity confirmation that the supplier has not provided now keeps `dataCoverage` `Incomplete`, and so does a `NotRequired` waiver without a recorded note.
- `fingerprint`, `fingerprintVersion` (1) and the section hashes are **unchanged** for unchanged facts. The `CaseLotDto.reconciliation` object (`allocatedQuantity`, `remainingQuantity`, `status` `Reconciled | UnderAllocated`) on the dossier endpoints is **unchanged** and compares contributions only; `CaseDossierDto.shipmentQuantity`/`shipmentUnit` remain display fields. Do not derive the new checks from them.

## 5. What the UI can derive

- Show the three checks with `expected`, `actual`, `difference` and `reason`; mark an operand with `unitImplied: true` as "unit assumed from the shipment".
- Use `outcome === "Matched"` as the single boolean for "quantities reconcile". It is equivalent to all four Quantity items being `Satisfied` or `NotApplicable`.
- Acceptance is possible only when every Quantity item is `Satisfied`/`NotApplicable` (and the other existing conditions hold); the close-as-`ExternalDdsCompleted` action additionally needs `outcome === "Matched"` **now**, not at review time.
- Nothing here is a legal net-mass, compliance or DDS statement; avoid wording such as "compliant" or "verified quantity".

## 6. Error and status behaviour

### Review creation (acceptance gate)

`POST /api/eudr-cases/{caseId}/reviews` with `decision: "AcceptedAsCompleteUnderPolicy"` and any Quantity item `Missing` or `Invalid` -> **409** ProblemDetails, the existing shape (`errors: { reason, failingItems: [{ code, subjectType, subjectId, status, reason }] }`). Quantity codes that can appear in `failingItems`: `LOT_PRESENT`, `SHIPMENT_LOT_QUANTITY_MATCH`, `SUPPLIER_CONFIRMED_QUANTITY_MATCH`, `LOT_RECONCILED`, and `REQUIREMENT_QuantityConfirmation` when it is `Missing`/`Invalid`/`AwaitingReview`. No review row is written.

```json
{
  "status": 409, "title": "Request failed", "type": "https://httpstatuses.com/409", "traceId": "...",
  "detail": "AcceptedAsCompleteUnderPolicy cannot be recorded: 1 completeness item(s) block AcceptedAsCompleteUnderPolicy.",
  "errors": {
    "reason": "AcceptedAsCompleteUnderPolicy cannot be recorded: 1 completeness item(s) block AcceptedAsCompleteUnderPolicy.",
    "failingItems": [ { "code": "SHIPMENT_LOT_QUANTITY_MATCH", "subjectType": "CaseLot", "subjectId": "...", "status": "Invalid", "reason": "The shipment quantity (10 Kg) does not equal the case lot quantity (25 Kg); difference 15 Kg." } ]
  }
}
```

### Close (ExternalDdsCompleted gate)

`POST /api/eudr-cases/{caseId}/close` whose backing review decision is `AcceptedAsCompleteUnderPolicy` (outcome `ExternalDdsCompleted`) while the **current** `quantityReconciliation.outcome` is not `Matched` -> **409** ProblemDetails (same convention as `StaleReview`; **no new response envelope**). This also applies when the backing review is `Current` and was made under policy `"1"`. Nothing is written: no closure, no audit event, no status change, supplier links are not revoked.

```json
{
  "status": 409, "title": "Request failed", "type": "https://httpstatuses.com/409", "traceId": "...",
  "detail": "The case cannot be closed as ExternalDdsCompleted while the quantity reconciliation is QuantityMismatch; correct the quantities and record a new review.",
  "errors": { "reason": "QuantityNotReconciled", "quantityReconciliation": { "outcome": "QuantityMismatch", "checks": [ "... same shape as section 4 ..." ] } }
}
```

- `outcome` in the detail text is one of `MissingData`, `UnitMismatch`, `QuantityMismatch`; `errors.quantityReconciliation` is the full structure of section 4. Use `errors.reason === "QuantityNotReconciled"` to recognise it; do not parse `detail`.
- **Order inside close** (unchanged except the new last step): archived (409) -> already closed (409) -> body shape (400) -> review checks (409) -> DDS-reference rule (400) -> freshness (`StaleReview` 409) -> **quantity gate (`QuantityNotReconciled` 409)** -> success. A stale review is therefore reported as `StaleReview`, not as a quantity failure.
- After correcting the facts the old review is `Stale` (a quantity is a fingerprinted fact), so the Owner records a **new** review and then closes.
- **Success is unchanged:** `201 Created`, `CaseClosureDto`, `Location` = `GET .../lifecycle`.

### Gate matrix

| Action | Quantity outcome not `Matched` | `Matched` |
| --- | --- | --- |
| Review `AcceptedAsCompleteUnderPolicy` | 409 `failingItems` (above) | allowed if the other conditions hold |
| Review `ConfirmedOutsideRegulatoryScope` | no quantity gate (its own applicability conditions still apply) | same |
| Review `ConfirmedUnsupportedFlow` | no quantity gate (its own applicability conditions still apply) | same |
| Review `ReturnedForCorrection` | allowed | allowed |
| Close as `ExternalDdsCompleted` | 409 `QuantityNotReconciled` | 201 |
| Close as `OutsideRegulatoryScope` | no quantity gate (201) | 201 |
| Close as `UnsupportedFlow` | no quantity gate (201) | 201 |

### Public supplier details

- `confirmedQuantity` with more than 6 decimals -> **400** ProblemDetails (`errors.ConfirmedQuantity`); the previous submission stays current.
- A `confirmedQuantity` that differs from the shipment quantity -> **204** as before. The public response does not expose the reconciliation (it exposes no completeness data); the internal user sees the mismatch on `GET completeness`.

## 7. Closed and archived cases

Unchanged. `GET completeness` and the working export stay readable for Closed and archived cases and report the same `quantityReconciliation`. A closure that was recorded before this change is not re-evaluated. Reopening re-enables the gates for the next review and close.

## 8. Breaking-change notes

- **No route, status-code or fingerprint breaking change.**
- **Value-set / behaviour changes a strict client may trip on:** `policy.version` `"2"`; two new item codes; `LOT_RECONCILED` can be `Invalid` (a client that mapped only `Missing` to "incomplete" must also handle `Invalid`); `quantityReconciliation` is an additional required-looking member of the completeness response (older clients that ignore unknown members are unaffected); a new `409` reason on close; a new `400` on public details.
- **Behavioural consequences:** acceptance and `ExternalDdsCompleted` closing are refused more often. Existing policy-`"1"` acceptances of a dossier whose quantities do not reconcile can no longer be closed as `ExternalDdsCompleted` until the quantities are corrected and a new review is recorded.

## 9. Required frontend adaptations (contract level)

1. Add `quantityReconciliation` to the completeness model and render the three checks (expected, actual, difference, reason, `unitImplied`).
2. Handle `Invalid` for Quantity items and the two new item codes; do not assume `LOT_RECONCILED` can only be `Missing`.
3. Disable or warn on the "accept as complete" review action when a Quantity item is `Missing`/`Invalid`; surface the `failingItems` of the 409 as before.
4. On close as `ExternalDdsCompleted`, recognise `errors.reason === "QuantityNotReconciled"` and show `errors.quantityReconciliation`; offer the path "correct quantities -> new review -> close".
5. Do not block or pre-validate the supplier portal against the shipment quantity; handle the new 400 for more than 6 decimals.
6. In the requirement review UI, tell the reviewer that `NotRequired` for the quantity confirmation needs a note (the API still accepts it without one, but the case then stays `Missing`/`Incomplete`).
7. Display `policy.version` as data (`"2"`); treat `policyChanged: true` on older reviews as informational, as before.
8. Keep treating quantities as decimals (6 places), with the unit from the same object.

## 10. Implemented and remaining

**Implemented (backend, verified):**
- the derived reconciliation and its DTOs; the four Quantity completeness items; policy version 2; the additive member on completeness and on the working export;
- the acceptance gate (through the existing blocking-item rule) and the `ExternalDdsCompleted` close gate, including the recheck for Current policy-`"1"` reviews;
- the `NotRequired` waiver rule aligned with `REQUIREMENT_QuantityConfirmation`, comparison of every existing confirmed quantity and the removal of the `IsRequired` shortcut;
- the public `confirmedQuantity` precision validation; fingerprint version 1 and snapshot schema 1 unchanged.

**Remaining / not implemented:**
- client adaptation (section 9) and the local client/API compatibility run (section 13);
- multi-lot, split, merge, period-of-supply aggregate rules (the Shipment == CaseLot rule is the single-lot pilot rule only);
- unit conversion, net-mass and supplementary-unit semantics, any tolerance;
- a stored unit for the supplier-confirmed quantity (it is implied from the shipment);
- a manual override of a reconciliation result (deliberately not offered);
- per-plot quantities; correcting a locked shipment's quantity (a shipment cannot be edited after a supplier request, evidence progress or a dossier exists; unchanged).

## 11. Acceptance criteria

Backend (each is covered by an automated test unless marked otherwise):

1. Equal quantities in one unit are `Matched` even at different scales (`10` vs `10.000000`); a difference of `0.000001` is `QuantityMismatch`.
2. Different units are `UnitMismatch` with `difference: null`; nothing is converted (`1 Ton` vs `1000 Kg` mismatches); a shipment in `Other` never matches a lot.
3. Missing lot, no contributions, and a required-but-missing confirmed quantity are `MissingData` with the documented `missingFields`.
4. `NotRequired` with a nonblank note is `NotApplicable`; `NotRequired` with a missing, empty or whitespace note is `MissingData` and `REQUIREMENT_QuantityConfirmation` is `Missing` at the same time; no `IsRequired` shortcut exists.
5. A present confirmed quantity is always compared, even when the requirement is `NotRequired`; the check can never be a `UnitMismatch`.
6. Outcome precedence is `UnitMismatch` > `QuantityMismatch` > `MissingData` > `Matched`.
7. Item order, subjects and status mapping are as in section 4; `dataCoverage` becomes `Incomplete` on any Missing/Invalid Quantity item.
8. A non-matched outcome blocks `AcceptedAsCompleteUnderPolicy` (409 with failing items, no review row); outside-scope, unsupported-flow and returned-for-correction reviews succeed, and so do their closures (201) with mismatching quantities.
9. A Current policy-`"1"` `Accepted` review on a non-matched dossier cannot close as `ExternalDdsCompleted`: 409 ProblemDetails with `errors.reason = "QuantityNotReconciled"` and the structured result; no closure, no `CaseClosed` audit event, status unchanged, supplier link not revoked. The same holds for an unbacked `NotRequired` waiver (`MissingData`).
10. A matching dossier with a Current policy-`"1"` review closes as `ExternalDdsCompleted` with **201** and `Location` to `GET .../lifecycle`.
11. A supplier `confirmedQuantity` that differs from the shipment returns 204 and is reported by completeness; a value with 7 decimals returns 400 and leaves the previous submission current.
12. The working export manifest contains `completeness.quantityReconciliation`.
13. `fingerprintVersion` is 1, the fingerprint of the fixed fixture equals the baseline value, and the snapshot schema is 1.

Frontend (to be verified by the client suite, not by the backend):

14. The completeness view shows the three checks and distinguishes `unitImplied` operands.
15. The accept action and the `ExternalDdsCompleted` close action show the backend's blocking reason from `failingItems` / `errors.quantityReconciliation` instead of a generic failure.
16. The supplier portal still submits a quantity that differs from the shipment and shows a validation message for more than 6 decimals.

## 12. Compatibility risks

- A client that treats unknown completeness members as an error, or that enumerates the Quantity items by a fixed list, must be updated.
- A client that hard-codes `policy.version === "1"` or compares `policyVersion` between a review and the current policy will report every pre-existing review as changed (that is expected and informational).
- A client that offers "close" for any `Current` review will now meet a 409 for `ExternalDdsCompleted`; it should evaluate `quantityReconciliation.outcome` on the current completeness response first.
- `reason` strings are descriptive English text, not stable identifiers; use `code`, `status`, `missingFields` and `errors.reason`.
- The confirmed-quantity unit is an assumption, not a recorded fact; do not present it as supplier-entered.

## 13. Backend verification performed

- `bash scripts/verify.sh` on the working tree (`pwsh` is not installed on this machine): Release build with 0 warnings, **223 unit tests and 244 integration tests passed** (PostgreSQL via Testcontainers). The documentation slice changed no behaviour; the only code edits in it were comments and the `CompletenessPolicy.Limitations` text, and the gate was re-run afterwards with the same result.
- `git diff --check` exited 0 after the documentation slice; every relative Markdown link in the changed documents resolves.
- **Fingerprint v1 compatibility:** a temporary clean worktree at baseline commit `547f327` and the current implementation computed the same deterministic fixture. The version, the fingerprint (`2cc06ed492c0914bcd43c7e0e779eb278077addf6ba90f87fbd9b792807df5a1`) and all seven section hashes were byte-identical; a unit test pins them.
- The backend implementation is available in commit `f17b30a` (`feat(api): add quantity reconciliation policy`), directly on top of `547f327`.

## 14. Local frontend/backend compatibility evidence

**Not run.** The companion client has not adapted to this contract and the cross-repository browser suite described in [CHANGE_WORKFLOW](../CHANGE_WORKFLOW.md) was not executed against this checkout. Record the two repository revisions (with dirty state), the environment, the exact command and the result here when it is run. Mocked client tests are not proof of live compatibility.
