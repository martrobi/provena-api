using Provena.Api.Contracts;
using Provena.Api.Domain;
namespace Provena.Api.Services;
// Pure Quantity Reconciliation v1 (completeness policy v2, ADR 0010). Derived on every read, never stored and never manually overridden. Exact decimal equality in a single unit:
// no unit conversion and no tolerance (1.5 == 1.500000, a different unit is a UnitMismatch, not a conversion).
// Pilot single-lot assumption: one shipment/case is represented by exactly one current CaseLot (ADR 0007), so the direct Shipment == current CaseLot rule is valid only for that model.
// It is deliberately not a permanent rule: a future multi-lot or split model needs a different aggregate rule and its own policy version.
// The supplier-confirmed quantity has no unit of its own; it is taken to be in the shipment's unit (UnitImplied), so that comparison can never be a UnitMismatch. Every existing
// confirmed quantity is compared with the shipment. When none exists the check mirrors the REQUIREMENT_QuantityConfirmation completeness item and does NOT consult IsRequired:
// NotRequired with a nonblank reviewer note (the recorded basis) is NotApplicable, NotRequired without one is MissingData, any other state is MissingData. So the outcome can never
// be Matched while that requirement item is Missing because of an unbacked waiver. A mismatching supplier quantity is never rejected on submission; it is judged here.
// Nothing here is a legal net-mass, compliance or DDS-readiness finding.
public static class QuantityReconciliation
{
    public const string ShipmentLot = "SHIPMENT_LOT";
    public const string SupplierConfirmedShipment = "SUPPLIER_CONFIRMED_SHIPMENT";
    public const string ContributionsLot = "CONTRIBUTIONS_LOT";

    public static QuantityReconciliationResultDto Evaluate(DossierFacts f)
    {
        var checks = new List<QuantityCheckDto> { ShipmentToLot(f), SupplierConfirmedToShipment(f), ContributionsToLot(f) };
        return new(Outcome(checks), checks);
    }

    // Precedence: UnitMismatch, then QuantityMismatch, then MissingData; Matched only when every applicable check matched (NotApplicable checks do not count).
    public static QuantityReconciliationOutcome Outcome(IReadOnlyCollection<QuantityCheckDto> checks) =>
        checks.Any(c => c.Status == QuantityCheckStatus.UnitMismatch) ? QuantityReconciliationOutcome.UnitMismatch
        : checks.Any(c => c.Status == QuantityCheckStatus.QuantityMismatch) ? QuantityReconciliationOutcome.QuantityMismatch
        : checks.Any(c => c.Status == QuantityCheckStatus.MissingData) ? QuantityReconciliationOutcome.MissingData
        : QuantityReconciliationOutcome.Matched;

    private static QuantityCheckDto ShipmentToLot(DossierFacts f)
    {
        var expected = new QuantityOperandDto("Shipment", f.Shipment.Quantity, f.Shipment.Unit, false);
        if (f.Lot == null)
            return new(ShipmentLot, QuantityCheckStatus.MissingData, expected, new("CaseLot", null, null, false), null, ["caseLot"], "No case lot has been recorded, so the shipment quantity cannot be compared with it.");
        var actual = new QuantityOperandDto("CaseLot", f.Lot.Quantity, f.Lot.Unit, false);
        return Compare(ShipmentLot, expected, actual, "The shipment quantity", "the case lot quantity");
    }

    private static QuantityCheckDto SupplierConfirmedToShipment(DossierFacts f)
    {
        var expected = new QuantityOperandDto("Shipment", f.Shipment.Quantity, f.Shipment.Unit, false);
        var confirmed = f.Submission?.ConfirmedQuantity;
        if (confirmed == null)
        {
            var actualMissing = new QuantityOperandDto("SupplierSubmission", null, f.Shipment.Unit, true);
            var requirement = f.Requirements.FirstOrDefault(r => r.Type == RequirementType.QuantityConfirmation);
            // Mirrors the REQUIREMENT_QuantityConfirmation item (which looks at the status and note only, not IsRequired): NotRequired counts as not applicable only with a recorded, nonblank basis (the reviewer note).
            // NotRequired without one is still missing data, so the reconciliation can never be Matched while that completeness item is Missing.
            if (requirement == null) // no requirement row at all: nothing to wait for
                return new(SupplierConfirmedShipment, QuantityCheckStatus.NotApplicable, expected, actualMissing, null, [], "Not applicable: no supplier-confirmed quantity exists and no quantity confirmation requirement is recorded.");
            if (requirement.Status == RequirementStatus.NotRequired && !string.IsNullOrWhiteSpace(requirement.ReviewerNote))
                return new(SupplierConfirmedShipment, QuantityCheckStatus.NotApplicable, expected, actualMissing, null, [], "Not applicable: the quantity confirmation is marked NotRequired with a recorded basis.");
            return requirement.Status == RequirementStatus.NotRequired
                ? new(SupplierConfirmedShipment, QuantityCheckStatus.MissingData, expected, actualMissing, null, ["quantityConfirmationWaiverBasis"], "The quantity confirmation is marked NotRequired without a recorded basis, so the supplier-confirmed quantity is still missing.")
                : new(SupplierConfirmedShipment, QuantityCheckStatus.MissingData, expected, actualMissing, null, ["supplierConfirmedQuantity"], "The supplier-confirmed quantity is required but has not been provided.");
        }
        // The confirmed quantity carries no unit of its own: it is taken to be in the shipment's unit.
        var actual = new QuantityOperandDto("SupplierSubmission", confirmed, f.Shipment.Unit, true);
        return Compare(SupplierConfirmedShipment, expected, actual, "The shipment quantity", "the supplier-confirmed quantity");
    }

    private static QuantityCheckDto ContributionsToLot(DossierFacts f)
    {
        if (f.Lot == null)
            return new(ContributionsLot, QuantityCheckStatus.MissingData, new("CaseLot", null, null, false), new("ProducerContributions", null, null, false), null, ["caseLot"], "Cannot be evaluated: no case lot has been recorded.");
        var expected = new QuantityOperandDto("CaseLot", f.Lot.Quantity, f.Lot.Unit, false);
        var allocated = f.Contributions.Sum(x => x.Contribution.Quantity);
        var units = f.Contributions.Select(x => x.Contribution.Unit).Distinct().ToList();
        var actual = new QuantityOperandDto("ProducerContributions", allocated, units.Count == 1 ? units[0] : null, false);
        if (f.Contributions.Count == 0)
            return new(ContributionsLot, QuantityCheckStatus.MissingData, expected, actual with { Unit = f.Lot.Unit }, null, ["producerContributions"],
                $"Producer contributions (0 {f.Lot.Unit}) do not equal the lot quantity ({Num(f.Lot.Quantity)} {f.Lot.Unit}); remaining {Num(f.Lot.Quantity)} {f.Lot.Unit}.");
        if (units.Any(u => u != f.Lot.Unit))
            return new(ContributionsLot, QuantityCheckStatus.UnitMismatch, expected, actual, null, [], $"A producer contribution is not in the lot unit {f.Lot.Unit}; units are not converted.");
        var remaining = f.Lot.Quantity - allocated;
        return allocated == f.Lot.Quantity
            ? new(ContributionsLot, QuantityCheckStatus.Matched, expected, actual, 0m, [], $"Producer contributions ({Num(allocated)} {f.Lot.Unit}) equal the lot quantity exactly.")
            : new(ContributionsLot, QuantityCheckStatus.QuantityMismatch, expected, actual, allocated - f.Lot.Quantity, [],
                $"Producer contributions ({Num(allocated)} {f.Lot.Unit}) do not equal the lot quantity ({Num(f.Lot.Quantity)} {f.Lot.Unit}); remaining {Num(remaining)} {f.Lot.Unit}.");
    }

    // Both operands are present. Different units are never converted; equal units compare by exact decimal value.
    private static QuantityCheckDto Compare(string code, QuantityOperandDto expected, QuantityOperandDto actual, string expectedLabel, string actualLabel)
    {
        if (expected.Unit != actual.Unit)
            return new(code, QuantityCheckStatus.UnitMismatch, expected, actual, null, [], $"{expectedLabel} is in {expected.Unit} but {actualLabel} is in {actual.Unit}; units are not converted.");
        var difference = actual.Quantity!.Value - expected.Quantity!.Value;
        return difference == 0m
            ? new(code, QuantityCheckStatus.Matched, expected, actual, 0m, [], $"{expectedLabel} ({Num(expected.Quantity.Value)} {expected.Unit}) equals {actualLabel} exactly.")
            : new(code, QuantityCheckStatus.QuantityMismatch, expected, actual, difference, [],
                $"{expectedLabel} ({Num(expected.Quantity.Value)} {expected.Unit}) does not equal {actualLabel} ({Num(actual.Quantity.Value)} {actual.Unit}); difference {Num(difference)} {expected.Unit}.");
    }

    private static string Num(decimal value) => DossierFingerprint.CanonicalDecimal(value);
}
