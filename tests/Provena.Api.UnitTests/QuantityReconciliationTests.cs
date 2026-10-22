using FluentValidation.Results;
using Provena.Api.Contracts;
using Provena.Api.Domain;
using Provena.Api.Services;
using Xunit;
using static Provena.Api.UnitTests.CompletenessTests;

namespace Provena.Api.UnitTests;

// Quantity Reconciliation v1 (completeness policy v2): exact decimal equality in one unit, no conversion, no tolerance. The Shipment == current CaseLot rule is a pilot
// single-lot assumption. The supplier-confirmed quantity has no unit of its own and is taken to be in the shipment unit.
public sealed class QuantityReconciliationTests
{
    private static QuantityReconciliationResultDto Q(DossierFacts f) => QuantityReconciliation.Evaluate(f);
    private static QuantityCheckDto Check(DossierFacts f, string code) => Q(f).Checks.Single(c => c.Code == code);
    private static QuantityCheckStatus StatusOf(DossierFacts f, string code) => Check(f, code).Status;
    private static CompletenessItemDto Item(DossierFacts f, string code) => CompletenessEvaluator.Evaluate(f).Items.Single(i => i.Code == code);
    private static ReviewPreconditionFailure? Review(ReviewDecision decision, DossierFacts f) => ReviewPreconditions.Check(decision, CompletenessEvaluator.Evaluate(f), f);
    private static DossierFacts WithoutQuantityConfirmation(DossierFacts f, RequirementStatus? status = null, string? note = "Not needed in this pilot case")
    {
        var withoutSubmission = f with { Submission = null };
        if (status != null) { var requirement = withoutSubmission.Requirements.Single(r => r.Type == RequirementType.QuantityConfirmation); requirement.Status = status.Value; requirement.ReviewerNote = note; }
        return withoutSubmission;
    }

    [Fact]
    public void A_consistent_dossier_matches_on_all_three_checks_in_a_fixed_order()
    {
        var result = Q(Complete());
        Assert.Equal(QuantityReconciliationOutcome.Matched, result.Outcome);
        Assert.Equal([QuantityReconciliation.ShipmentLot, QuantityReconciliation.SupplierConfirmedShipment, QuantityReconciliation.ContributionsLot], result.Checks.Select(c => c.Code));
        Assert.All(result.Checks, c => { Assert.Equal(QuantityCheckStatus.Matched, c.Status); Assert.Equal(0m, c.Difference); Assert.Empty(c.MissingFields); });
    }

    [Fact]
    public void Equality_is_exact_by_decimal_value_with_no_tolerance()
    {
        var scale = Complete(); scale.Lot!.Quantity = 10.000000m; scale.Submission!.ConfirmedQuantity = 10.00m; scale.Contributions[0].Contribution.Quantity = 10m;
        Assert.Equal(QuantityReconciliationOutcome.Matched, Q(scale).Outcome);

        var lot = Complete(); lot.Lot!.Quantity = 10.000001m;
        var check = Check(lot, QuantityReconciliation.ShipmentLot);
        Assert.Equal(QuantityCheckStatus.QuantityMismatch, check.Status); Assert.Equal(0.000001m, check.Difference); // the smallest storable difference is still a mismatch
        Assert.Equal(10m, check.Expected.Quantity); Assert.Equal(10.000001m, check.Actual.Quantity); Assert.Equal("Shipment", check.Expected.Source); Assert.Equal("CaseLot", check.Actual.Source);

        var confirmed = Complete(); confirmed.Submission!.ConfirmedQuantity = 9.999999m;
        var confirmedCheck = Check(confirmed, QuantityReconciliation.SupplierConfirmedShipment);
        Assert.Equal(QuantityCheckStatus.QuantityMismatch, confirmedCheck.Status); Assert.Equal(-0.000001m, confirmedCheck.Difference);
    }

    [Fact]
    public void Different_units_are_a_unit_mismatch_and_are_never_converted()
    {
        var ton = Complete(); ton.Shipment.Unit = QuantityUnit.Ton; ton.Shipment.Quantity = 1m; ton.Lot!.Quantity = 1000m; // would be equal after a conversion
        var check = Check(ton, QuantityReconciliation.ShipmentLot);
        Assert.Equal(QuantityCheckStatus.UnitMismatch, check.Status); Assert.Null(check.Difference); Assert.Equal(QuantityUnit.Ton, check.Expected.Unit); Assert.Equal(QuantityUnit.Kg, check.Actual.Unit);
        Assert.Contains("not converted", check.Reason);

        var other = Complete(); other.Shipment.Unit = QuantityUnit.Other; // a lot can never be Other, so this can never match
        Assert.Equal(QuantityCheckStatus.UnitMismatch, StatusOf(other, QuantityReconciliation.ShipmentLot));

        var contribution = Complete(); contribution.Contributions[0].Contribution.Unit = QuantityUnit.Ton;
        var contributionCheck = Check(contribution, QuantityReconciliation.ContributionsLot);
        Assert.Equal(QuantityCheckStatus.UnitMismatch, contributionCheck.Status); Assert.Null(contributionCheck.Difference);
    }

    [Fact]
    public void A_missing_lot_or_missing_contributions_is_missing_data_with_the_missing_fields_named()
    {
        var noLot = Complete() with { Dossier = null, Lot = null, Contributions = [] };
        var result = Q(noLot);
        Assert.Equal(QuantityReconciliationOutcome.MissingData, result.Outcome);
        Assert.Equal(["caseLot"], Check(noLot, QuantityReconciliation.ShipmentLot).MissingFields); Assert.Equal(QuantityCheckStatus.MissingData, StatusOf(noLot, QuantityReconciliation.ShipmentLot));
        Assert.Equal(["caseLot"], Check(noLot, QuantityReconciliation.ContributionsLot).MissingFields);
        Assert.Equal(QuantityCheckStatus.Matched, StatusOf(noLot, QuantityReconciliation.SupplierConfirmedShipment)); // independent of the lot

        var noContributions = Complete() with { Contributions = [] };
        var check = Check(noContributions, QuantityReconciliation.ContributionsLot);
        Assert.Equal(QuantityCheckStatus.MissingData, check.Status); Assert.Equal(["producerContributions"], check.MissingFields); Assert.Contains("remaining 10 Kg", check.Reason);

        var under = Complete(); under.Contributions[0].Contribution.Quantity = 9m;
        var underCheck = Check(under, QuantityReconciliation.ContributionsLot);
        Assert.Equal(QuantityCheckStatus.QuantityMismatch, underCheck.Status); Assert.Equal(-1m, underCheck.Difference); Assert.Contains("remaining 1 Kg", underCheck.Reason);
        var over = Complete(); over.Contributions[0].Contribution.Quantity = 11m; // cannot be saved through the API, but the read model must still state it
        Assert.Equal(1m, Check(over, QuantityReconciliation.ContributionsLot).Difference); Assert.Equal(QuantityCheckStatus.QuantityMismatch, StatusOf(over, QuantityReconciliation.ContributionsLot));
    }

    [Fact]
    public void The_supplier_confirmed_quantity_is_required_unless_the_requirement_is_not_required_and_is_always_compared_when_present()
    {
        var required = WithoutQuantityConfirmation(Complete());
        var missing = Check(required, QuantityReconciliation.SupplierConfirmedShipment);
        Assert.Equal(QuantityCheckStatus.MissingData, missing.Status); Assert.Equal(["supplierConfirmedQuantity"], missing.MissingFields); Assert.Equal(QuantityReconciliationOutcome.MissingData, Q(required).Outcome);

        var notRequired = WithoutQuantityConfirmation(Complete(), RequirementStatus.NotRequired);
        Assert.Equal(QuantityCheckStatus.NotApplicable, StatusOf(notRequired, QuantityReconciliation.SupplierConfirmedShipment));
        Assert.Equal(QuantityReconciliationOutcome.Matched, Q(notRequired).Outcome); // a not-applicable check never blocks a match

        var optional = WithoutQuantityConfirmation(Complete(), RequirementStatus.Missing, null); optional.Requirements.Single(r => r.Type == RequirementType.QuantityConfirmation).IsRequired = false;
        Assert.Equal(QuantityCheckStatus.MissingData, StatusOf(optional, QuantityReconciliation.SupplierConfirmedShipment)); // mirrors the requirement item, which ignores IsRequired and reports Missing

        var noRequirementRow = WithoutQuantityConfirmation(Complete()) with { Requirements = [] };
        Assert.Equal(QuantityCheckStatus.NotApplicable, StatusOf(noRequirementRow, QuantityReconciliation.SupplierConfirmedShipment));

        var presentWhileNotRequired = Complete(); presentWhileNotRequired.Requirements.Single(r => r.Type == RequirementType.QuantityConfirmation).Status = RequirementStatus.NotRequired; presentWhileNotRequired.Submission!.ConfirmedQuantity = 7m;
        Assert.Equal(QuantityCheckStatus.QuantityMismatch, StatusOf(presentWhileNotRequired, QuantityReconciliation.SupplierConfirmedShipment)); // a supplied value is compared whatever the requirement says
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_not_required_quantity_confirmation_without_a_basis_is_missing_data_exactly_like_the_requirement_item(string? note)
    {
        var withoutBasis = WithoutQuantityConfirmation(Complete(), RequirementStatus.NotRequired, note);
        var check = Check(withoutBasis, QuantityReconciliation.SupplierConfirmedShipment);
        Assert.Equal(QuantityCheckStatus.MissingData, check.Status); Assert.Equal(["quantityConfirmationWaiverBasis"], check.MissingFields);
        Assert.Equal(QuantityReconciliationOutcome.MissingData, Q(withoutBasis).Outcome); // never Matched while the requirement item is Missing
        Assert.Equal(CompletenessItemStatus.Missing, Item(withoutBasis, "REQUIREMENT_QuantityConfirmation").Status);
        Assert.Equal(CompletenessItemStatus.Missing, Item(withoutBasis, "SUPPLIER_CONFIRMED_QUANTITY_MATCH").Status);
        Assert.NotNull(Review(ReviewDecision.AcceptedAsCompleteUnderPolicy, withoutBasis));
        Assert.Contains("MissingData", CaseClosurePolicy.QuantityError(CaseClosureOutcome.ExternalDdsCompleted, Q(withoutBasis))!); // the legacy-review close path
        Assert.Null(CaseClosurePolicy.QuantityError(CaseClosureOutcome.OutsideRegulatoryScope, Q(withoutBasis)));

        var withBasis = WithoutQuantityConfirmation(Complete(), RequirementStatus.NotRequired, "Supplier quantity not needed for this pilot case");
        Assert.Equal(QuantityCheckStatus.NotApplicable, StatusOf(withBasis, QuantityReconciliation.SupplierConfirmedShipment));
        Assert.Equal(CompletenessItemStatus.NotApplicable, Item(withBasis, "REQUIREMENT_QuantityConfirmation").Status);
        Assert.Equal(QuantityReconciliationOutcome.Matched, Q(withBasis).Outcome);
        Assert.Null(CaseClosurePolicy.QuantityError(CaseClosureOutcome.ExternalDdsCompleted, Q(withBasis)));
    }

    [Fact]
    public void The_confirmed_quantity_uses_the_implied_shipment_unit_and_can_never_be_a_unit_mismatch()
    {
        var f = Complete(); f.Shipment.Unit = QuantityUnit.Ton; f.Shipment.Quantity = 5m; f.Submission!.ConfirmedQuantity = 5m;
        var check = Check(f, QuantityReconciliation.SupplierConfirmedShipment);
        Assert.Equal(QuantityCheckStatus.Matched, check.Status); Assert.True(check.Actual.UnitImplied); Assert.False(check.Expected.UnitImplied);
        Assert.Equal(QuantityUnit.Ton, check.Actual.Unit); Assert.Equal("SupplierSubmission", check.Actual.Source);
        f.Submission.ConfirmedQuantity = 5000m;
        Assert.Equal(QuantityCheckStatus.QuantityMismatch, StatusOf(f, QuantityReconciliation.SupplierConfirmedShipment));
        f.Shipment.Unit = QuantityUnit.Other;
        Assert.NotEqual(QuantityCheckStatus.UnitMismatch, StatusOf(f, QuantityReconciliation.SupplierConfirmedShipment));
    }

    [Fact]
    public void The_outcome_precedence_is_unit_mismatch_then_quantity_mismatch_then_missing_data_then_matched()
    {
        var f = Complete(); f.Shipment.Unit = QuantityUnit.Ton; f.Submission!.ConfirmedQuantity = 1m; // check 1 unit mismatch, check 2 quantity mismatch
        f = f with { Contributions = [] }; // check 3 missing data
        Assert.Equal([QuantityCheckStatus.UnitMismatch, QuantityCheckStatus.QuantityMismatch, QuantityCheckStatus.MissingData], Q(f).Checks.Select(c => c.Status));
        Assert.Equal(QuantityReconciliationOutcome.UnitMismatch, Q(f).Outcome);

        f.Shipment.Unit = QuantityUnit.Kg; // unit mismatch resolved
        Assert.Equal(QuantityReconciliationOutcome.QuantityMismatch, Q(f).Outcome);

        f.Submission.ConfirmedQuantity = 10m; // quantity mismatch resolved, contributions still missing
        Assert.Equal(QuantityReconciliationOutcome.MissingData, Q(f).Outcome);

        Assert.Equal(QuantityReconciliationOutcome.Matched, Q(Complete()).Outcome);
    }

    [Fact]
    public void Quantity_items_keep_their_codes_order_subjects_and_statuses()
    {
        var items = CompletenessEvaluator.Evaluate(Complete()).Items.Where(i => i.Group == CompletenessGroup.Quantity && i.Code != SourceAllocation.ItemCode).ToList(); // the policy v3 allocation item is covered by SourceAllocationTests
        Assert.Equal(["LOT_PRESENT", "SHIPMENT_LOT_QUANTITY_MATCH", "SUPPLIER_CONFIRMED_QUANTITY_MATCH", "LOT_RECONCILED"], items.Select(i => i.Code));
        Assert.All(items, i => Assert.Equal(CompletenessItemStatus.Satisfied, i.Status));
        Assert.Equal(["CaseLot", "CaseLot", "SupplierSubmission", "CaseLot"], items.Select(i => i.SubjectType));

        var lotMismatch = Complete(); lotMismatch.Lot!.Quantity = 11m; lotMismatch.Contributions[0].Contribution.Quantity = 11m;
        Assert.Equal(CompletenessItemStatus.Invalid, Item(lotMismatch, "SHIPMENT_LOT_QUANTITY_MATCH").Status); Assert.Equal(CompletenessItemStatus.Satisfied, Item(lotMismatch, "LOT_RECONCILED").Status);
        var unit = Complete(); unit.Shipment.Unit = QuantityUnit.Ton; Assert.Equal(CompletenessItemStatus.Invalid, Item(unit, "SHIPMENT_LOT_QUANTITY_MATCH").Status);
        var under = Complete(); under.Contributions[0].Contribution.Quantity = 9m; Assert.Equal(CompletenessItemStatus.Invalid, Item(under, "LOT_RECONCILED").Status);
        var none = Complete() with { Contributions = [] }; Assert.Equal(CompletenessItemStatus.Missing, Item(none, "LOT_RECONCILED").Status);
        var notRequired = WithoutQuantityConfirmation(Complete(), RequirementStatus.NotRequired);
        var applicable = Item(notRequired, "SUPPLIER_CONFIRMED_QUANTITY_MATCH"); Assert.Equal(CompletenessItemStatus.NotApplicable, applicable.Status); Assert.Null(applicable.SubjectId);
        Assert.Equal(CompletenessItemStatus.Missing, Item(WithoutQuantityConfirmation(Complete()), "SUPPLIER_CONFIRMED_QUANTITY_MATCH").Status);

        var noLot = Complete() with { Dossier = null, Lot = null, Contributions = [] };
        var noLotItems = CompletenessEvaluator.Evaluate(noLot).Items.Where(i => i.Group == CompletenessGroup.Quantity && i.Code != SourceAllocation.ItemCode).ToList();
        Assert.Equal(["LOT_PRESENT", "SHIPMENT_LOT_QUANTITY_MATCH", "SUPPLIER_CONFIRMED_QUANTITY_MATCH", "LOT_RECONCILED"], noLotItems.Select(i => i.Code));
        Assert.Equal([CompletenessItemStatus.Missing, CompletenessItemStatus.Missing, CompletenessItemStatus.Satisfied, CompletenessItemStatus.Missing], noLotItems.Select(i => i.Status));
    }

    [Fact]
    public void A_quantity_mismatch_makes_data_coverage_incomplete_and_the_evaluation_carries_the_same_structured_result()
    {
        var f = Complete();
        Assert.Equal(DataCoverage.Complete, CompletenessEvaluator.Evaluate(f).DataCoverage);
        f.Submission!.ConfirmedQuantity = 9m;
        var evaluation = CompletenessEvaluator.Evaluate(f);
        Assert.Equal(DataCoverage.Incomplete, evaluation.DataCoverage);
        Assert.Equal(QuantityReconciliationOutcome.QuantityMismatch, evaluation.QuantityReconciliation.Outcome);
        Assert.Equal(Q(f).Checks.Select(c => (c.Code, c.Status, c.Difference)), evaluation.QuantityReconciliation.Checks.Select(c => (c.Code, c.Status, c.Difference)));
    }

    [Fact]
    public void A_non_matched_reconciliation_blocks_only_the_positive_acceptance_decision()
    {
        var mismatch = Complete(); mismatch.Shipment.Quantity = 11m;
        var failure = Review(ReviewDecision.AcceptedAsCompleteUnderPolicy, mismatch)!;
        Assert.Contains("SHIPMENT_LOT_QUANTITY_MATCH", failure.FailingItems.Select(i => i.Code)); Assert.Equal("Invalid", failure.FailingItems.Single(i => i.Code == "SHIPMENT_LOT_QUANTITY_MATCH").Status);
        var confirmed = Complete(); confirmed.Submission!.ConfirmedQuantity = 9m;
        Assert.Contains("SUPPLIER_CONFIRMED_QUANTITY_MATCH", Review(ReviewDecision.AcceptedAsCompleteUnderPolicy, confirmed)!.FailingItems.Select(i => i.Code));
        Assert.Null(Review(ReviewDecision.AcceptedAsCompleteUnderPolicy, Complete()));
        Assert.Null(Review(ReviewDecision.ReturnedForCorrection, mismatch));

        var outside = Complete(); outside.Applicability!.RegulatoryScope = RegulatoryScope.OutsideRegulatoryScope; outside.Shipment.Quantity = 11m; outside.Submission!.ConfirmedQuantity = 1m;
        Assert.Null(Review(ReviewDecision.ConfirmedOutsideRegulatoryScope, outside));
        var unsupported = Complete(); unsupported.Applicability!.FlowSupport = FlowSupport.UnsupportedFlow; unsupported.Shipment.Unit = QuantityUnit.Other; unsupported.Shipment.Quantity = 11m;
        Assert.Null(Review(ReviewDecision.ConfirmedUnsupportedFlow, unsupported));
    }

    [Fact]
    public void Closing_as_external_dds_completed_needs_a_matched_reconciliation_and_the_other_outcomes_have_no_quantity_gate()
    {
        var matched = Q(Complete());
        var mismatch = Complete(); mismatch.Shipment.Quantity = 11m; var broken = Q(mismatch);
        var unitMismatch = Complete(); unitMismatch.Shipment.Unit = QuantityUnit.Ton;
        Assert.Null(CaseClosurePolicy.QuantityError(CaseClosureOutcome.ExternalDdsCompleted, matched));
        var error = CaseClosurePolicy.QuantityError(CaseClosureOutcome.ExternalDdsCompleted, broken);
        Assert.NotNull(error); Assert.Contains("QuantityMismatch", error);
        Assert.Contains("MissingData", CaseClosurePolicy.QuantityError(CaseClosureOutcome.ExternalDdsCompleted, Q(Complete() with { Contributions = [] }))!);
        Assert.Contains("UnitMismatch", CaseClosurePolicy.QuantityError(CaseClosureOutcome.ExternalDdsCompleted, Q(unitMismatch))!);
        Assert.Null(CaseClosurePolicy.QuantityError(CaseClosureOutcome.OutsideRegulatoryScope, broken));
        Assert.Null(CaseClosurePolicy.QuantityError(CaseClosureOutcome.UnsupportedFlow, broken));
    }

    [Fact]
    public void A_review_made_under_policy_v1_stays_current_and_is_rechecked_only_at_closure()
    {
        // A policy-version change never stales a review (ADR 0008); the closure recheck is what keeps a mismatched v1 acceptance from closing.
        var f = Complete();
        var fingerprint = DossierFingerprint.Compute(f, 1);
        var revision = new CaseRevision { Id = Guid.NewGuid(), Number = 1, PolicyId = CompletenessPolicy.Id, PolicyVersion = "1", FingerprintVersion = 1, Fingerprint = fingerprint.Fingerprint, SectionHashes = ReviewSnapshot.SerializeHashes(fingerprint.SectionHashes) };
        var record = new ReviewRecord { Id = Guid.NewGuid(), CaseRevisionId = revision.Id, Decision = ReviewDecision.AcceptedAsCompleteUnderPolicy, ReviewedAtUtc = DateTimeOffset.UtcNow, PolicyVersion = "1" };
        var (freshness, latest) = ReviewFreshnessService.Evaluate(revision, record, f);
        Assert.Equal(ReviewFreshness.Current, freshness); Assert.True(latest.PolicyChanged); Assert.Empty(latest.ChangedAreas);
        Assert.Null(CaseClosurePolicy.QuantityError(CaseClosureOutcome.ExternalDdsCompleted, Q(f)));
        f.Shipment.Quantity = 11m; // a quantity change is a Shipment fact: it stales the review rather than being hidden by the policy version
        Assert.Equal(ReviewFreshness.Stale, ReviewFreshnessService.Evaluate(revision, record, f).Freshness);
    }

    [Fact]
    public void The_policy_states_the_single_lot_assumption_and_the_implied_unit_from_version_2_on()
    {
        Assert.Equal("3", CompletenessPolicy.Version);
        var limitations = string.Join(' ', CompletenessPolicy.Limitations);
        foreach (var expected in new[] { "no unit conversion", "no tolerance", "single-lot assumption", "supplier-confirmed quantity", "taken to be in the shipment unit" }) Assert.Contains(expected, limitations);
    }

    [Fact]
    public void The_supplier_confirmed_quantity_accepts_at_most_six_decimals_and_a_mismatch_is_not_a_validation_error()
    {
        var validator = new SubmissionValidator();
        ValidationResult Validate(decimal? quantity) => validator.Validate(new SubmissionInput("BR", null, null, quantity, null));
        Assert.True(Validate(10.123456m).IsValid); Assert.True(Validate(null).IsValid); Assert.True(Validate(99999999999999.999999m).IsValid);
        Assert.False(Validate(10.1234567m).IsValid); Assert.False(Validate(0m).IsValid); Assert.False(Validate(-1m).IsValid);
        Assert.Contains(Validate(1.1234567m).Errors, e => e.PropertyName == nameof(SubmissionInput.ConfirmedQuantity));
        // The validator knows nothing about the shipment, so a quantity that differs from it is accepted here and judged by the reconciliation instead.
        Assert.True(Validate(123456.5m).IsValid);
    }
}
