using Provena.Api.Contracts;
using Provena.Api.Domain;
using Provena.Api.Services;
using Xunit;

namespace Provena.Api.UnitTests;

// Applicability Determination v1 hardening: the appended RequiresLegalReview scope/status and its place in the derivation, validation, completeness items, review preconditions and fingerprint.
public sealed class ApplicabilityHardeningDomainTests
{
    private static readonly DateOnly Day = new(2026, 6, 1);
    private static CaseMasterDataVersion Copy(CommodityType commodity = CommodityType.Coffee) => new()
    {
        SupplierId = Guid.NewGuid(), SupplierName = "Supplier", ProductId = Guid.NewGuid(), ProductCnCode = "0901", ProductCommodity = commodity, ProductName = "Product",
    };
    private static CaseApplicability Applicability(CaseMasterDataVersion copy, RegulatoryScope scope, FlowSupport flow = FlowSupport.Pending,
        ApplicabilityActivityType activity = ApplicabilityActivityType.PlacingOnMarketByImport, OperatorRole role = OperatorRole.Operator) => new()
    {
        AssessedProductId = copy.ProductId, AssessedCnCode = copy.ProductCnCode, AssessedCommodity = copy.ProductCommodity,
        RegulatoryScope = scope, FlowSupport = flow, ActivityType = activity, SingleRelevantCommodity = true, OperatorRole = role,
    };
    private static CaseApplicabilityInput Input(RegulatoryScope scope, string? reasoning = "Needs counsel", string? citation = null, string? legalBasis = null, DateOnly? effective = null, DateOnly? activity = null, string? description = null) =>
        new(description, true, ApplicabilityActivityType.PlacingOnMarketByImport, OperatorRole.Operator, null, activity, ActivityDateKind.Planned, citation, null, legalBasis, effective, reasoning, scope, FlowSupport.Pending, null);
    private static bool Valid(CaseApplicabilityInput input) => new CaseApplicabilityValidator().Validate(CaseApplicabilityService.Normalize(input)).IsValid;
    private static DossierFacts WithScope(RegulatoryScope scope) { var f = CompletenessTests.Complete(); f.Applicability!.RegulatoryScope = scope; return f; }

    [Fact]
    public void The_new_values_are_appended_and_the_persisted_numeric_values_are_unchanged()
    {
        Assert.Equal([0, 1, 2, 3], Enum.GetValues<RegulatoryScope>().Select(v => (int)v).ToArray());
        Assert.Equal(0, (int)RegulatoryScope.Pending); Assert.Equal(1, (int)RegulatoryScope.InScope); Assert.Equal(2, (int)RegulatoryScope.OutsideRegulatoryScope); Assert.Equal(3, (int)RegulatoryScope.RequiresLegalReview);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], Enum.GetValues<ApplicabilityStatus>().Select(v => (int)v).ToArray());
        Assert.Equal(5, (int)ApplicabilityStatus.WithinSupportedScope); Assert.Equal(6, (int)ApplicabilityStatus.RequiresLegalReview);
    }

    [Theory]
    [InlineData(FlowSupport.Pending)]
    [InlineData(FlowSupport.Supported)]
    [InlineData(FlowSupport.UnsupportedFlow)]
    public void Requires_legal_review_derives_its_own_status_and_ignores_flow_support(FlowSupport flow)
    {
        var copy = Copy();
        Assert.Equal(ApplicabilityStatus.RequiresLegalReview, ApplicabilityDerivation.Derive(Applicability(copy, RegulatoryScope.RequiresLegalReview, flow), copy));
    }

    [Fact]
    public void Requires_legal_review_is_not_masked_by_the_pilot_envelope_and_never_becomes_an_unsupported_flow_or_a_scope_decision()
    {
        var wood = Copy(CommodityType.Wood);
        var a = Applicability(wood, RegulatoryScope.RequiresLegalReview, FlowSupport.Supported, ApplicabilityActivityType.Export, OperatorRole.OtherUnsupported);
        var status = ApplicabilityDerivation.Derive(a, wood);
        Assert.Equal(ApplicabilityStatus.RequiresLegalReview, status);
        Assert.NotEqual(ApplicabilityStatus.UnsupportedFlow, status); Assert.NotEqual(ApplicabilityStatus.OutsideRegulatoryScope, status); Assert.NotEqual(ApplicabilityStatus.WithinSupportedScope, status);
    }

    [Fact]
    public void A_changed_product_takes_precedence_over_requires_legal_review_and_no_record_stays_not_recorded()
    {
        var copy = Copy(); var a = Applicability(copy, RegulatoryScope.RequiresLegalReview);
        Assert.Equal(ApplicabilityStatus.ProductChanged, ApplicabilityDerivation.Derive(a, Copy()));
        Assert.Equal(ApplicabilityStatus.ProductChanged, ApplicabilityDerivation.Derive(a, null));
        Assert.Equal(ApplicabilityStatus.NotRecorded, ApplicabilityDerivation.Derive(null, copy));
    }

    [Fact]
    public void Requires_legal_review_needs_a_nonblank_reasoning_and_nothing_else_of_the_decision_fields()
    {
        Assert.True(Valid(Input(RegulatoryScope.RequiresLegalReview)));
        Assert.False(Valid(Input(RegulatoryScope.RequiresLegalReview, reasoning: null)));
        Assert.False(Valid(Input(RegulatoryScope.RequiresLegalReview, reasoning: "   ")));
        Assert.False(Valid(Input(RegulatoryScope.RequiresLegalReview, reasoning: new string('x', 4001))));
        Assert.True(Valid(Input(RegulatoryScope.Pending, reasoning: null)));
        Assert.False(Valid(Input(RegulatoryScope.OutsideRegulatoryScope)));
        Assert.True(Valid(Input(RegulatoryScope.OutsideRegulatoryScope, citation: "Art. 1", legalBasis: "2023/1115", effective: Day, activity: Day)));
        Assert.False(Valid(Input(RegulatoryScope.InScope, citation: "Art. 1", legalBasis: "2023/1115", effective: Day, activity: Day)));
        Assert.False(Valid(Input((RegulatoryScope)4)));
    }

    [Fact]
    public void Requires_legal_review_leaves_the_scope_flow_and_decided_items_unresolved_and_the_data_coverage_incomplete()
    {
        var evaluation = CompletenessEvaluator.Evaluate(WithScope(RegulatoryScope.RequiresLegalReview));
        Assert.Equal(ApplicabilityStatus.RequiresLegalReview, evaluation.ApplicabilityStatus);
        Assert.Equal(DataCoverage.Incomplete, evaluation.DataCoverage);
        CompletenessItemStatus Of(string code) => evaluation.Items.Single(i => i.Code == code).Status;
        Assert.Equal(CompletenessItemStatus.Satisfied, Of("APPLICABILITY_RECORDED"));
        Assert.Equal(CompletenessItemStatus.Missing, Of("APPLICABILITY_DECIDED"));
        Assert.Equal(CompletenessItemStatus.Missing, Of("APPLICABILITY_IN_SCOPE"));
        Assert.Equal(CompletenessItemStatus.Missing, Of("FLOW_SUPPORTED"));
        Assert.Equal(CompletenessItemStatus.Satisfied, Of("APPLICABILITY_PRODUCT_MATCH"));
        Assert.DoesNotContain(evaluation.Items.Where(i => i.Group == CompletenessGroup.Applicability), i => i.Status == CompletenessItemStatus.Invalid);
    }

    [Theory]
    [InlineData(ReviewDecision.AcceptedAsCompleteUnderPolicy)]
    [InlineData(ReviewDecision.ConfirmedOutsideRegulatoryScope)]
    [InlineData(ReviewDecision.ConfirmedUnsupportedFlow)]
    public void Requires_legal_review_blocks_every_positive_and_final_review_decision(ReviewDecision decision)
    {
        var facts = WithScope(RegulatoryScope.RequiresLegalReview);
        var failure = ReviewPreconditions.Check(decision, CompletenessEvaluator.Evaluate(facts), facts);
        Assert.NotNull(failure);
        Assert.Contains("RequiresLegalReview", failure!.Reason);
        Assert.Contains(failure.FailingItems, i => i.Code == "APPLICABILITY_DECIDED");
    }

    [Fact]
    public void A_return_for_correction_is_still_allowed_while_legal_review_is_required()
    {
        var facts = WithScope(RegulatoryScope.RequiresLegalReview);
        Assert.Null(ReviewPreconditions.Check(ReviewDecision.ReturnedForCorrection, CompletenessEvaluator.Evaluate(facts), facts));
    }

    [Fact]
    public void Requires_legal_review_changes_the_applicability_section_hash_through_the_existing_canonical_form_without_a_new_fingerprint_version()
    {
        var baseline = DossierFingerprint.Compute(CompletenessTests.Complete());
        var review = DossierFingerprint.Compute(WithScope(RegulatoryScope.RequiresLegalReview));
        Assert.Equal(1, DossierFingerprint.CurrentVersion); Assert.Equal([1], DossierFingerprint.SupportedVersions); Assert.Equal(1, review.Version);
        Assert.NotEqual(baseline.SectionHashes["Applicability"], review.SectionHashes["Applicability"]);
        Assert.NotEqual(baseline.Fingerprint, review.Fingerprint);
        foreach (var section in DossierFingerprint.Sections.Where(s => s != "Applicability")) Assert.Equal(baseline.SectionHashes[section], review.SectionHashes[section]);
        Assert.Equal(review.Fingerprint, DossierFingerprint.Compute(WithScope(RegulatoryScope.RequiresLegalReview)).Fingerprint);
        Assert.NotEqual(DossierFingerprint.Compute(WithScope(RegulatoryScope.OutsideRegulatoryScope)).Fingerprint, review.Fingerprint);
    }

    [Fact]
    public void The_expected_current_id_is_request_metadata_and_not_part_of_the_assessed_content()
    {
        var copy = Copy(); var current = Applicability(copy, RegulatoryScope.RequiresLegalReview); current.Reasoning = "Needs counsel";
        var input = CaseApplicabilityService.Normalize(Input(RegulatoryScope.RequiresLegalReview));
        Assert.True(CaseApplicabilityService.IsIdentical(input, copy, current));
        Assert.True(CaseApplicabilityService.IsIdentical(input with { ExpectedCurrentApplicabilityId = Guid.NewGuid() }, copy, current));
    }
}
