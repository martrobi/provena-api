using System.Text.Json;
using Provena.Api.Contracts;
using Provena.Api.Domain;
using Provena.Api.Services;
using Xunit;

namespace Provena.Api.UnitTests;

// Slice 5 / commit 3: pure review logic - decision preconditions, freshness (Current/Stale, changed areas, policyChanged), the typed snapshot and request validation.
public sealed class ReviewTests
{
    private static DossierFacts Complete() => CompletenessTests.Complete();
    private static ReviewPreconditionFailure? Check(ReviewDecision decision, DossierFacts f) => ReviewPreconditions.Check(decision, CompletenessEvaluator.Evaluate(f), f);
    private static string[] Codes(ReviewPreconditionFailure? failure) => failure?.FailingItems.Select(i => i.Code).ToArray() ?? [];

    // ---- decision preconditions ---------------------------------------------------------------------------------------------------------
    [Fact]
    public void Acceptance_succeeds_only_for_a_matching_supported_complete_dossier_without_drift_and_allows_not_applicable_items()
    {
        Assert.Null(Check(ReviewDecision.AcceptedAsCompleteUnderPolicy, Complete()));
        var withNotApplicable = Complete(); withNotApplicable.Requirements[1].Status = RequirementStatus.NotRequired; withNotApplicable.Requirements[1].ReviewerNote = "n/a";
        Assert.Null(Check(ReviewDecision.AcceptedAsCompleteUnderPolicy, withNotApplicable));

        var drift = Complete() with { Drift = [new(MasterDataArea.Supplier, ["email"])] };
        var failure = Check(ReviewDecision.AcceptedAsCompleteUnderPolicy, drift)!; Assert.Contains("supplier: email", failure.Reason); Assert.Contains("refresh the case master data", failure.Reason); Assert.Empty(failure.FailingItems); // drift is stated in the reason, never as a pseudo item

        var mismatch = Complete(); mismatch.Shipment.SupplierId = Guid.NewGuid();
        Assert.Contains("MASTER_DATA_SHIPMENT_MATCH", Codes(Check(ReviewDecision.AcceptedAsCompleteUnderPolicy, mismatch)));

        Assert.Contains("PARTY_RECIPIENTS", Codes(Check(ReviewDecision.AcceptedAsCompleteUnderPolicy, Complete() with { Recipients = null, RecipientEntries = [] })));
        var submitted = Complete(); submitted.Requirements[0].Status = RequirementStatus.Submitted;
        Assert.Contains("REQUIREMENT_" + submitted.Requirements[0].Type, Codes(Check(ReviewDecision.AcceptedAsCompleteUnderPolicy, submitted)));
        var rejected = Complete(); rejected.Requirements[0].Status = RequirementStatus.Rejected; Assert.NotNull(Check(ReviewDecision.AcceptedAsCompleteUnderPolicy, rejected));

        var unsupported = Complete(); unsupported.Applicability!.ActivityType = ApplicabilityActivityType.Export;
        var unsupportedFailure = Check(ReviewDecision.AcceptedAsCompleteUnderPolicy, unsupported)!; Assert.Contains("UnsupportedFlow", unsupportedFailure.Reason); Assert.Contains("requires WithinSupportedScope", unsupportedFailure.Reason); Assert.Contains("FLOW_SUPPORTED", Codes(unsupportedFailure));

        var productChanged = Complete(); productChanged.MasterData!.ProductId = Guid.NewGuid(); productChanged.Shipment.ProductId = productChanged.MasterData.ProductId;
        var changedFailure = Check(ReviewDecision.AcceptedAsCompleteUnderPolicy, productChanged)!; Assert.Contains("ProductChanged", changedFailure.Reason); Assert.Contains("APPLICABILITY_PRODUCT_MATCH", Codes(changedFailure));
    }

    [Fact]
    public void Confirmed_outside_scope_needs_only_the_outside_status_and_a_matching_shipment_and_ignores_pending_flow_support()
    {
        var outside = Complete(); outside.Applicability!.RegulatoryScope = RegulatoryScope.OutsideRegulatoryScope; outside.Applicability.FlowSupport = FlowSupport.Pending;
        outside = outside with { Recipients = null, RecipientEntries = [], Lot = null, Dossier = null, Contributions = [] }; // the InScope completeness set is not required
        Assert.Null(Check(ReviewDecision.ConfirmedOutsideRegulatoryScope, outside));
        Assert.NotNull(Check(ReviewDecision.ConfirmedOutsideRegulatoryScope, Complete()));
        var wrongStatus = Check(ReviewDecision.ConfirmedOutsideRegulatoryScope, Complete())!; Assert.Contains("ConfirmedOutsideRegulatoryScope requires OutsideRegulatoryScope", wrongStatus.Reason); Assert.Empty(wrongStatus.FailingItems);
        outside.Shipment.ProductId = Guid.NewGuid();
        Assert.Contains("MASTER_DATA_SHIPMENT_MATCH", Codes(Check(ReviewDecision.ConfirmedOutsideRegulatoryScope, outside)));
    }

    [Fact]
    public void Confirmed_unsupported_flow_needs_the_unsupported_status_including_the_other_unsupported_role()
    {
        Assert.NotNull(Check(ReviewDecision.ConfirmedUnsupportedFlow, Complete()));
        var role = Complete(); role.Applicability!.OperatorRole = OperatorRole.OtherUnsupported; role.Applicability.OperatorRoleDescription = "Downstream trader";
        Assert.Null(Check(ReviewDecision.ConfirmedUnsupportedFlow, role));
        var recorded = Complete(); recorded.Applicability!.FlowSupport = FlowSupport.UnsupportedFlow; Assert.Null(Check(ReviewDecision.ConfirmedUnsupportedFlow, recorded));
        var wood = Complete(); wood.Applicability!.AssessedCommodity = CommodityType.Wood; wood.MasterData!.ProductCommodity = CommodityType.Wood; Assert.Null(Check(ReviewDecision.ConfirmedUnsupportedFlow, wood));
        var mismatch = Complete(); mismatch.Applicability!.ActivityType = ApplicabilityActivityType.Export; mismatch.Shipment.SupplierId = Guid.NewGuid();
        Assert.Contains("MASTER_DATA_SHIPMENT_MATCH", Codes(Check(ReviewDecision.ConfirmedUnsupportedFlow, mismatch)));
        var productChanged = Complete(); productChanged.Applicability!.ActivityType = ApplicabilityActivityType.Export; productChanged.MasterData!.ProductId = Guid.NewGuid(); productChanged.Shipment.ProductId = productChanged.MasterData.ProductId;
        Assert.NotNull(Check(ReviewDecision.ConfirmedUnsupportedFlow, productChanged)); // ProductChanged is not UnsupportedFlow
    }

    [Fact]
    public void Every_failing_item_is_a_real_completeness_item_and_derived_requirements_are_stated_in_the_reason()
    {
        var broken = Complete() with { Recipients = null, RecipientEntries = [], Drift = [new(MasterDataArea.Supplier, ["email"])] };
        broken.Shipment.SupplierId = Guid.NewGuid(); broken.Applicability!.ActivityType = ApplicabilityActivityType.Export; broken.Requirements[0].Status = RequirementStatus.Submitted;
        var items = CompletenessEvaluator.Evaluate(broken).Items.Select(i => i.Code).ToHashSet();
        foreach (var decision in new[] { ReviewDecision.AcceptedAsCompleteUnderPolicy, ReviewDecision.ConfirmedOutsideRegulatoryScope, ReviewDecision.ConfirmedUnsupportedFlow })
        {
            var failure = Check(decision, broken);
            if (failure == null) continue;
            Assert.NotEmpty(failure.FailingItems); Assert.All(failure.FailingItems, i => { Assert.Contains(i.Code, items); Assert.DoesNotContain("CATALOGUE_DRIFT", i.Code); Assert.NotEqual("APPLICABILITY_STATUS", i.Code); Assert.NotEqual("Drift", i.Status); });
            Assert.False(string.IsNullOrWhiteSpace(failure.Reason));
        }
        Assert.Contains("supplier: email", Check(ReviewDecision.AcceptedAsCompleteUnderPolicy, broken)!.Reason);
    }

    [Fact]
    public void Returned_for_correction_is_always_allowed_even_with_a_mismatch_and_an_incomplete_dossier()
    {
        var broken = Complete() with { Recipients = null, RecipientEntries = [], Applicability = null, Drift = [new(MasterDataArea.Product, ["cnCode"])] }; broken.Shipment.SupplierId = Guid.NewGuid();
        Assert.Null(Check(ReviewDecision.ReturnedForCorrection, broken));
        Assert.Null(Check(ReviewDecision.ReturnedForCorrection, Complete() with { MasterData = null }));
    }

    [Fact]
    public void Decision_vocabulary_has_exactly_the_four_approved_values_and_no_compliance_verdict()
    {
        Assert.Equal(["AcceptedAsCompleteUnderPolicy", "ConfirmedOutsideRegulatoryScope", "ConfirmedUnsupportedFlow", "ReturnedForCorrection"], Enum.GetNames<ReviewDecision>());
        string[] forbidden = ["Approved", "Compliant", "LegallyCompliant", "SafeToTrade", "Released", "DeforestationFree"];
        Assert.Empty(Enum.GetNames<ReviewDecision>().Intersect(forbidden));
    }

    // ---- freshness ----------------------------------------------------------------------------------------------------------------------
    private static (CaseRevision Revision, ReviewRecord Record) Reviewed(DossierFacts facts, string policyVersion = CompletenessPolicy.Version, int fingerprintVersion = 1)
    {
        var fp = DossierFingerprint.Compute(facts, fingerprintVersion);
        var revision = new CaseRevision { Id = Guid.NewGuid(), Number = 1, PolicyId = CompletenessPolicy.Id, PolicyVersion = policyVersion, FingerprintVersion = fingerprintVersion, Fingerprint = fp.Fingerprint, SectionHashes = ReviewSnapshot.SerializeHashes(fp.SectionHashes) };
        return (revision, new ReviewRecord { Id = Guid.NewGuid(), CaseRevisionId = revision.Id, Decision = ReviewDecision.AcceptedAsCompleteUnderPolicy, ReviewedAtUtc = DateTimeOffset.UtcNow, PolicyVersion = policyVersion });
    }
    private static (ReviewFreshness Freshness, LatestReviewDto Latest) Fresh(DossierFacts reviewed, DossierFacts current, string policyVersion = CompletenessPolicy.Version) { var (r, rec) = Reviewed(reviewed, policyVersion); return ReviewFreshnessService.Evaluate(r, rec, current); }

    [Fact]
    public void Unchanged_facts_are_current_with_no_changed_areas_and_no_policy_change()
    {
        var (freshness, latest) = Fresh(Complete(), Complete());
        Assert.Equal(ReviewFreshness.Current, freshness); Assert.Empty(latest.ChangedAreas); Assert.False(latest.PolicyChanged); Assert.Equal(ReviewDecision.AcceptedAsCompleteUnderPolicy, latest.Decision);
    }

    [Fact]
    public void A_policy_version_difference_alone_is_current_with_policy_changed()
    {
        var (freshness, latest) = Fresh(Complete(), Complete(), policyVersion: "0");
        Assert.Equal(ReviewFreshness.Current, freshness); Assert.True(latest.PolicyChanged); Assert.Empty(latest.ChangedAreas);
    }

    [Fact]
    public void Live_context_catalogue_drift_labels_and_revision_supersession_do_not_stale_a_review()
    {
        var current = Complete() with { Drift = [new(MasterDataArea.Supplier, ["name"])] };
        current.MasterData!.ProductName = "Renamed"; current.MasterData.ProductCatalogueDescription = "x"; current.MasterData.Id = Guid.NewGuid(); current.Contributions[0].Links[0].Revision.SupersededAtUtc = DateTimeOffset.UtcNow;
        Assert.Equal(ReviewFreshness.Current, Fresh(Complete(), current).Freshness);
    }

    [Theory]
    [InlineData("Shipment")] [InlineData("MasterData")] [InlineData("Applicability")] [InlineData("Recipients")] [InlineData("Lot")] [InlineData("Requirements")] [InlineData("Evidence")]
    public void Each_kind_of_fact_change_is_stale_and_names_exactly_its_section(string area)
    {
        var current = Complete();
        switch (area)
        {
            case "Shipment": current.Shipment.Quantity = 11m; break;
            case "MasterData": current.MasterData!.SupplierEmail = "other@example.test"; break;
            case "Applicability": current.Applicability!.Reasoning = "changed"; break;
            case "Recipients": current.Recipients!.NotApplicableBasis = "x"; break;
            case "Lot": current.Contributions[0].Links[0].Link.ProductionEndDate = new DateOnly(2024, 6, 1); break;
            case "Requirements": current.Requirements[0].ReviewedAtUtc = current.Requirements[0].ReviewedAtUtc!.Value.AddMinutes(1); break; // same status and note, new review time
            case "Evidence": current = current with { Documents = current.Documents.Skip(1).ToList() }; break;
        }
        var (freshness, latest) = Fresh(Complete(), current);
        Assert.Equal(ReviewFreshness.Stale, freshness); Assert.Equal([area], latest.ChangedAreas);
    }

    [Fact]
    public void Changed_areas_are_listed_in_the_fixed_section_order_and_a_stored_version_is_used_for_the_comparison()
    {
        var current = Complete(); current.Requirements[0].ReviewerNote = "n"; current.Shipment.ReferenceNumber = "SHP-2"; current.Recipients!.NotApplicableBasis = "x";
        Assert.Equal(["Shipment", "Recipients", "Requirements"], Fresh(Complete(), current).Latest.ChangedAreas);
        var (revision, record) = Reviewed(Complete()); revision.FingerprintVersion = 99;
        Assert.Throws<ArgumentOutOfRangeException>(() => ReviewFreshnessService.Evaluate(revision, record, Complete())); // an unknown stored version is never silently reinterpreted
    }

    // ---- snapshot --------------------------------------------------------------------------------------------------------------------------
    private static CaseRevisionSnapshotV1 Snapshot(DossierFacts f)
    {
        var producer = new Producer { Id = Guid.Parse("00000000-0000-0000-0000-0000000000c1"), Name = "Producer label", ExternalReference = "P-1", CountryCode = "BR" };
        var plot = new ProductionPlot { Id = Guid.Parse("00000000-0000-0000-0000-0000000000d1"), ProducerId = producer.Id, Name = "Plot label", ExternalReference = "PL-1" };
        return ReviewSnapshot.Build(f, CompletenessEvaluator.Evaluate(f), [producer], [plot]);
    }

    [Fact]
    public void The_snapshot_is_typed_round_trips_and_keeps_facts_context_and_completeness_apart()
    {
        var facts = Complete(); facts.MasterData!.ProductName = "Unique product name"; facts.MasterData.ProductCatalogueDescription = "Unique catalogue text"; facts.Contributions[0].Links[0].Revision.SupersededAtUtc = DateTimeOffset.UtcNow;
        var snapshot = Snapshot(facts); var json = ReviewSnapshot.Serialize(snapshot);
        Assert.Equal(1, snapshot.SchemaVersion); Assert.Equal(json, ReviewSnapshot.Serialize(ReviewSnapshot.Deserialize(json, 1)));
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        Assert.Equal(["schemaVersion", "facts", "context", "completeness"], root.EnumerateObject().Select(p => p.Name).ToArray());
        var factsText = root.GetProperty("facts").GetRawText();
        Assert.DoesNotContain("Unique product name", factsText); Assert.DoesNotContain("Unique catalogue text", factsText); Assert.DoesNotContain("Producer label", factsText); Assert.DoesNotContain("Plot label", factsText);
        Assert.Equal("Unique product name", snapshot.Context.ProductName); Assert.Equal("Unique catalogue text", snapshot.Context.ProductCatalogueDescription);
        Assert.Equal("Producer label", Assert.Single(snapshot.Context.Producers).Name); Assert.Equal("Plot label", Assert.Single(snapshot.Context.Plots).Name);
        Assert.True(Assert.Single(snapshot.Context.PinnedRevisions).SupersededAtReviewTime);
        Assert.Equal("provena-pilot-completeness", snapshot.Completeness.PolicyId); Assert.Equal(CompletenessPolicy.Version, snapshot.Completeness.PolicyVersion); Assert.NotEmpty(snapshot.Completeness.Items);
        Assert.DoesNotContain("latestReview", json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("reviewFreshness", json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("organizationId", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"geometrySha256\"", factsText); Assert.Contains("\"regulatoryScope\":\"InScope\"", factsText); Assert.Contains("\"status\":\"Satisfied\"", root.GetProperty("completeness").GetRawText());
    }

    [Fact]
    public void The_snapshot_facts_do_not_change_with_context_only_differences_and_an_unknown_schema_is_rejected()
    {
        var a = Complete(); var b = Complete(); b.MasterData!.ProductName = "Other"; b.MasterData.Id = Guid.NewGuid();
        Assert.Equal(JsonSerializer.Serialize(Snapshot(a).Facts), JsonSerializer.Serialize(Snapshot(b).Facts));
        Assert.Throws<NotSupportedException>(() => ReviewSnapshot.Deserialize("{}", 2));
        var withoutLot = Complete() with { Lot = null, Dossier = null, Contributions = [] }; Assert.Null(Snapshot(withoutLot).Facts.Lot);
        Assert.Null(Snapshot(Complete() with { MasterData = null, Applicability = null, Recipients = null, RecipientEntries = [], Geolocation = null, Submission = null }).Facts.MasterData);
    }

    // ---- request validation ------------------------------------------------------------------------------------------------------------------
    private static readonly CaseReviewValidator Validator = new();
    private static bool Valid(CaseReviewInput input) => Validator.Validate(input).IsValid;
    private static readonly string Hash = new('a', 64);

    [Fact]
    public void Review_requests_need_a_defined_decision_a_bounded_rationale_and_a_well_formed_fingerprint()
    {
        Assert.True(Valid(new(ReviewDecision.AcceptedAsCompleteUnderPolicy, "Looks complete under the policy", Hash, null)));
        Assert.True(Valid(new(ReviewDecision.ReturnedForCorrection, new string('x', 4000), Hash, Guid.NewGuid())));
        Assert.False(Valid(new((ReviewDecision)9, "r", Hash, null))); Assert.False(Valid(new(ReviewDecision.ReturnedForCorrection, "", Hash, null))); Assert.False(Valid(new(ReviewDecision.ReturnedForCorrection, new string('x', 4001), Hash, null)));
        Assert.False(Valid(new(ReviewDecision.ReturnedForCorrection, "r", "", null))); Assert.False(Valid(new(ReviewDecision.ReturnedForCorrection, "r", "abc", null))); Assert.False(Valid(new(ReviewDecision.ReturnedForCorrection, "r", new string('g', 64), null)));
    }
}
