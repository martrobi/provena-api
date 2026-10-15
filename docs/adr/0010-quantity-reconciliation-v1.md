# ADR 0010: Quantity Reconciliation v1 (exact same-unit comparison of shipment, supplier-confirmed, lot and producer quantities)

- Date: 2026-10-15
- Decision status: Accepted
- Implementation status: **Implemented** (backend only; the separate client has not adapted). Quantity Reconciliation v1 was introduced by backend implementation commit `f17b30a` (`feat(api): add quantity reconciliation policy`), directly on top of baseline commit `547f327`.
- Decision owner / approval evidence: the owner stated the rules in the Quantity Reconciliation v1 planning request and its clarifications: no unit conversion; compared quantities use the same unit; Shipment quantity must equal CaseLot quantity; a supplier-confirmed quantity, when it exists or is required, must equal the Shipment quantity; producer contributions must sum exactly to the CaseLot quantity; exact decimal equality with no tolerance; reconciliation is derived and never manually overridden; a structured result with field-level details; a non-matched result blocks the positive `AcceptedAsCompleteUnderPolicy` path but not `ConfirmedOutsideRegulatoryScope` or `ConfirmedUnsupportedFlow`; the confirmed-quantity unit is implied from the shipment (no new column); `ExternalDdsCompleted` closing rechecks the current reconciliation even for a Current review made under policy v1; a successful close stays 201; the new close failure uses the existing ProblemDetails convention; the Shipment == CaseLot rule is a pilot single-lot assumption. A later request aligned the `NotRequired` waiver with the completeness requirement item and removed the `IsRequired` shortcut. The plan artifact itself is not retained in the repository.
- Supersedes / superseded by: **amends** [ADR 0007](0007-case-dossier-lot-attribution.md) only where it says the lot quantity is not compared with the shipment or supplier-confirmed quantity (the contributions-to-lot reconciliation it defines is unchanged), and [ADR 0009](0009-case-lifecycle-v2.md) only by adding one closing condition for the `ExternalDdsCompleted` outcome. It moves the completeness policy from version 1 to 2 under the mechanism in [ADR 0008](0008-completeness-facts-and-reviewed-history.md). Nothing else in 0007, 0008 or 0009 changes.

## Context and evidence

Before this decision (observed at baseline `547f327`):

- Only producer contributions were reconciled, against the lot (`CaseDossierService.Reconcile`, `CompletenessEvaluator.Quantity`). `CompletenessPolicy.Limitations` stated that the shipment and supplier-confirmed quantities were not compared, `docs/EUDR_DOMAIN.md` said the `QuantityConfirmation` requirement was "not compared to shipment quantity", and the matter was listed as a pending owner decision.
- `Shipment.Quantity`/`Unit`, `CaseLot.Quantity`/`Unit`, `LotContribution.Quantity`/`Unit` and `SupplierSubmission.ConfirmedQuantity` already existed as `numeric(20,6)` values. The confirmed quantity has **no unit column**; the public supplier portal shows the supplier the shipment's quantity and unit. A shipment cannot be edited once a supplier request, evidence progress or a dossier exists.
- Every input of the comparison is already a fingerprinted fact (shipment quantity/unit, lot quantity/unit/contributions, `supplierSubmission.confirmedQuantity`, requirement `isRequired`/`status`/note), so a derived result needs no new stored fact.
- `ReviewPreconditions` already blocks `AcceptedAsCompleteUnderPolicy` on any `Missing`, `Invalid` or `AwaitingReview` completeness item and looks only at the applicability items for the two confirming decisions.
- The `REQUIREMENT_QuantityConfirmation` item treats `NotRequired` with a nonblank reviewer note as `NotApplicable` and `NotRequired` without one as `Missing`; it does not consult `IsRequired`.

## Decision

**Pilot single-lot assumption (scope of this decision).** Quantity Reconciliation v1 assumes that **one shipment/case is represented by exactly one current `CaseLot`** (ADR 0007: one dossier per case, one current lot version; split, merge, processing and multi-lot are unsupported). The direct rule "Shipment quantity == current CaseLot quantity" holds only under that assumption and is **not** a permanent rule. A multi-lot or split model needs a different aggregate rule (for example the shipment against the sum of lots) with its own policy version and ADR. This is out of scope.

**Derived, never stored, never overridden.** `QuantityReconciliation.Evaluate(DossierFacts)` is a pure function evaluated on every completeness read, export and close. There is no stored result, no override and no endpoint to set one. Exact decimal equality, one unit, **no unit conversion, no tolerance** (`10` equals `10.000000`; `10` Kg is neither `10` Ton nor `10000` Kg).

**Three checks, fixed order.**

| Code | Expected | Actual | Notes |
| --- | --- | --- | --- |
| `SHIPMENT_LOT` | Shipment quantity/unit | current CaseLot quantity/unit | pilot single-lot assumption; a lot unit can never be `Other`, so a shipment in `Other` is always a `UnitMismatch` |
| `SUPPLIER_CONFIRMED_SHIPMENT` | Shipment quantity/unit | current `SupplierSubmission.ConfirmedQuantity`, unit **implied** from the shipment (`unitImplied: true`) | can never be a `UnitMismatch` |
| `CONTRIBUTIONS_LOT` | CaseLot quantity/unit | sum of producer contributions | no contributions is `MissingData`; any contribution unit other than the lot unit is `UnitMismatch` (defensive: the validator already forbids it); under- and over-allocation are `QuantityMismatch` |

Check status: `Matched | MissingData | UnitMismatch | QuantityMismatch | NotApplicable`. Outcome: `UnitMismatch` if any check is, else `QuantityMismatch` if any is, else `MissingData` if any is, else `Matched`; `NotApplicable` checks never count. `difference` is `actual - expected` and is present only for same-unit comparisons.

**Supplier-confirmed quantity rules.**
- **Every existing confirmed quantity is compared** with the shipment, whatever the requirement says (including a `NotRequired` or `Accepted` requirement).
- If none exists, the check follows the `REQUIREMENT_QuantityConfirmation` item exactly and **no longer consults `IsRequired`** (the earlier shortcut was removed):
  - requirement `NotRequired` with a **nonblank reviewer note** (the recorded basis): `NotApplicable`;
  - requirement `NotRequired` **without** a nonblank note: `MissingData`, missing field `quantityConfirmationWaiverBasis`;
  - any other state: `MissingData`, missing field `supplierConfirmedQuantity`;
  - no requirement row at all (not produced by the application): `NotApplicable`.
- Consequence: the reconciliation cannot be `Matched` while the corresponding requirement item is `Missing` because of an unbacked waiver.
- A supplier submission with a quantity that differs from the shipment is **accepted** (204): submission is not acceptance, and the mismatch is judged by the reconciliation. The only new public validation is `PrecisionScale(20, 6)` on `confirmedQuantity` (400 for more than six decimals), so an exact value is never silently rounded by PostgreSQL.

**Completeness policy v2.** `CompletenessPolicy.Version` is `"2"`. The Quantity group emits, in order, `LOT_PRESENT`, `SHIPMENT_LOT_QUANTITY_MATCH` (subject `CaseLot`), `SUPPLIER_CONFIRMED_QUANTITY_MATCH` (subject `SupplierSubmission` when one exists) and `LOT_RECONCILED` (subject `CaseLot`; code kept, now driven by `CONTRIBUTIONS_LOT`). Mapping: `Matched` -> `Satisfied`, `NotApplicable` -> `NotApplicable`, `MissingData` -> `Missing`, `UnitMismatch` and `QuantityMismatch` -> `Invalid`. The structured result is returned as the additive `quantityReconciliation` member of `GET .../completeness` and of the working-export manifest `completeness`.

**Gates.**
- `AcceptedAsCompleteUnderPolicy` is blocked through the existing blocking-item rule when any quantity item is `Missing` or `Invalid`. `ReviewPreconditions` is unchanged.
- `ConfirmedOutsideRegulatoryScope`, `ConfirmedUnsupportedFlow` and `ReturnedForCorrection` have **no quantity gate**.
- **Close recheck.** Closing a case whose review decision derives `ExternalDdsCompleted` additionally requires the **current** reconciliation outcome to be `Matched`, evaluated under the same case lock after the stale-review check, **including for a Current review made under policy v1** (a policy-version change never stales a review). Failure is a standard ProblemDetails 409 with `errors: { reason: "QuantityNotReconciled", quantityReconciliation }` (the same convention as `StaleReview`); nothing is written. The other two outcomes have no quantity gate. A successful close is still **201 Created** and no lifecycle route or success contract changes.

**Fingerprint and snapshot unchanged.** `FingerprintVersion` stays **1** and the review snapshot `SchemaVersion` stays **1**. The result is recomputed from facts the fingerprint and snapshot already contain, so it is not stored in either. Backward compatibility was checked against the actual baseline `547f327` (see below).

Deliberately undecided: legal net-mass and supplementary-unit semantics, unit conversion, tolerance, a stored confirmed-quantity unit, per-plot quantities, and any multi-lot, split or period-of-supply aggregate rule.

## Alternatives considered

- **A stored `ConfirmedQuantityUnit` column.** Rejected by the owner for v1: it would need a migration, fingerprint version 2, snapshot schema 2 and public portal changes. The unit is implied and flagged `unitImplied` instead.
- **Grandfather policy-v1 reviews at closure.** Rejected by the owner (recheck chosen): a policy-version change is informational only (ADR 0008), so without the recheck a Current v1 acceptance of a mismatching dossier could close as `ExternalDdsCompleted`.
- **Unit conversion or a tolerance.** Rejected by the owner for the pilot; legal quantity semantics are undecided.
- **A manual override or waiver of a mismatch.** Rejected by the owner: the result is derived.
- **Reject a mismatching supplier quantity on submission.** Rejected: the supplier records facts; evaluation belongs to the reconciliation (supplier submission is not acceptance).
- **Include the result in the fingerprint or snapshot (version bump).** Not needed: every input is already fingerprinted and snapshotted.
- **Keep the `IsRequired` shortcut.** Removed: the requirement item ignores `IsRequired`, so the shortcut could report `Matched` while that item was `Missing`.
- **Treat any `NotRequired` as a valid waiver.** Rejected: it would diverge from `REQUIREMENT_QuantityConfirmation`, which requires a recorded basis.

## Consequences

- **Client-visible changes (additive or value-set):** `quantityReconciliation` on completeness and the export; three Quantity-group item codes (two new); `LOT_RECONCILED` under- or over-allocation is now `Invalid` (it was `Missing`; zero contributions stay `Missing`); `policy.version` is `"2"` and existing reviews report `policyChanged: true`; a new close 409 reason; a new public 400 for more than six decimals. See the [frontend handoff](../frontend-handoffs/2026-10-quantity-reconciliation.md). No route, success status or fingerprint changes.
- A required quantity confirmation that has not been provided (or a `NotRequired` waiver without a recorded note) makes `dataCoverage` `Incomplete` and blocks acceptance until the supplier confirms or the requirement is waived with a basis.
- Existing policy-v1 acceptances of dossiers that do not reconcile can no longer close as `ExternalDdsCompleted`; a new review of a corrected dossier is needed (correcting a fact stales the old review anyway).
- The shipment is locked after a supplier request, evidence progress or a dossier exists, so a wrong shipment quantity can only be reconciled by changing the lot (or the confirmed quantity); correcting the shipment itself is not supported and is not changed here.
- No database change and no migration. Reconciliation is application-level, like the other derived rules; it is not a legal net-mass, compliance or DDS-readiness finding.

## Implementation and verification

Source: `Services/QuantityReconciliation.cs` (pure model), `Contracts/Requests.cs` (`QuantityCheckStatus`, `QuantityReconciliationOutcome`, `QuantityOperandDto`, `QuantityCheckDto`, `QuantityReconciliationResultDto`, `CompletenessDto.QuantityReconciliation`), `Services/CompletenessEvaluator.cs`, `CompletenessPolicy.cs`, `CompletenessService.cs`, `CaseClosurePolicy.QuantityError`, `CaseLifecycleService.Close`, `Contracts/Validators.cs` (`SubmissionValidator`). Tests: unit `QuantityReconciliationTests`, `CompletenessTests`, `ReviewTests`; integration `QuantityReconciliationTests`, `CompletenessReadModelTests`, `ReviewTests`.

Results recorded for the working tree (`bash scripts/verify.sh`; `pwsh` is not installed here): Release build with 0 warnings, 223 unit and 244 integration tests passed, and `git diff --check` clean, re-run after the documentation slice with the same result.

**Fingerprint v1 compatibility.** A temporary clean worktree at the baseline `547f327` (which contains no reconciliation code) and the current implementation computed the same deterministic fixture (`CompletenessTests.Complete()`). The version (`1`), the fingerprint (`2cc06ed492c0914bcd43c7e0e779eb278077addf6ba90f87fbd9b792807df5a1`) and all seven section hashes were byte-identical; the unit test `Fingerprint_v1_output_is_unchanged_by_quantity_reconciliation` pins them. The worktree was removed afterwards. This proves compatibility for that fixture, not for arbitrary data; the algorithm and `DossierFingerprint.cs` are unmodified.

The local client compatibility suite has not been run.

## Revisit trigger

A decision on multi-lot or split flows (an aggregate Shipment-to-lots rule), unit conversion or net-mass semantics, tolerance, a stored confirmed-quantity unit, per-plot quantities, or a change to who may waive the quantity confirmation and on what basis.
