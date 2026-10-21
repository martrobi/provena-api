using FluentValidation;
using Provena.Api.Contracts;
using Provena.Api.Domain;
using Provena.Api.Services;
using Xunit;

namespace Provena.Api.UnitTests;

// Pure helpers and validators of the completeness facts (Slice 5 / commit 1): applicability derivation, master-data copy/drift, input validation and normalization.
public sealed class CompletenessFactsDomainTests
{
    private static readonly Guid ProductX = Guid.NewGuid(), ProductY = Guid.NewGuid(), SupplierA = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 6, 1);

    private static CaseMasterDataVersion Copy(Guid? product = null, string cn = "0901", CommodityType commodity = CommodityType.Coffee) => new()
    {
        SupplierId = SupplierA, SupplierName = "Supplier", ProductId = product ?? ProductX, ProductCnCode = cn, ProductCommodity = commodity, ProductName = "Product",
    };
    private static CaseApplicability Applicability(CaseMasterDataVersion copy, RegulatoryScope scope = RegulatoryScope.InScope, FlowSupport flow = FlowSupport.Supported,
        ApplicabilityActivityType activity = ApplicabilityActivityType.PlacingOnMarketByImport, bool single = true, OperatorRole role = OperatorRole.Operator) => new()
    {
        AssessedProductId = copy.ProductId, AssessedCnCode = copy.ProductCnCode, AssessedCommodity = copy.ProductCommodity,
        RegulatoryScope = scope, FlowSupport = flow, ActivityType = activity, SingleRelevantCommodity = single, OperatorRole = role,
    };

    // ---- applicability derivation -------------------------------------------------------------------------------------------------------
    [Fact]
    public void No_applicability_is_not_recorded_regardless_of_the_master_data()
    {
        Assert.Equal(ApplicabilityStatus.NotRecorded, ApplicabilityDerivation.Derive(null, null));
        Assert.Equal(ApplicabilityStatus.NotRecorded, ApplicabilityDerivation.Derive(null, Copy()));
    }

    [Theory]
    [InlineData(RegulatoryScope.Pending, FlowSupport.Pending, ApplicabilityStatus.Pending)]
    [InlineData(RegulatoryScope.Pending, FlowSupport.Supported, ApplicabilityStatus.Pending)]
    [InlineData(RegulatoryScope.OutsideRegulatoryScope, FlowSupport.Pending, ApplicabilityStatus.OutsideRegulatoryScope)]
    [InlineData(RegulatoryScope.OutsideRegulatoryScope, FlowSupport.UnsupportedFlow, ApplicabilityStatus.OutsideRegulatoryScope)]
    [InlineData(RegulatoryScope.OutsideRegulatoryScope, FlowSupport.Supported, ApplicabilityStatus.OutsideRegulatoryScope)]
    [InlineData(RegulatoryScope.InScope, FlowSupport.Pending, ApplicabilityStatus.Pending)]
    [InlineData(RegulatoryScope.InScope, FlowSupport.UnsupportedFlow, ApplicabilityStatus.UnsupportedFlow)]
    [InlineData(RegulatoryScope.InScope, FlowSupport.Supported, ApplicabilityStatus.WithinSupportedScope)]
    public void Scope_and_flow_support_derive_the_status_in_the_documented_order(RegulatoryScope scope, FlowSupport flow, ApplicabilityStatus expected)
    {
        var copy = Copy();
        Assert.Equal(expected, ApplicabilityDerivation.Derive(Applicability(copy, scope, flow), copy));
    }

    [Fact]
    public void Outside_scope_ignores_the_flow_envelope_even_for_an_unsupported_flow()
    {
        var copy = Copy(commodity: CommodityType.Wood);
        var a = Applicability(copy, RegulatoryScope.OutsideRegulatoryScope, FlowSupport.Pending, ApplicabilityActivityType.Export, single: false, role: OperatorRole.OtherUnsupported);
        Assert.Equal(ApplicabilityStatus.OutsideRegulatoryScope, ApplicabilityDerivation.Derive(a, copy));
    }

    [Theory]
    [InlineData(CommodityType.Wood)]
    [InlineData(CommodityType.Cattle)]
    [InlineData(CommodityType.Other)]
    public void Unsupported_commodities_reject_the_flow_even_when_a_human_marked_it_supported(CommodityType commodity)
    {
        var copy = Copy(commodity: commodity);
        Assert.Equal(ApplicabilityStatus.UnsupportedFlow, ApplicabilityDerivation.Derive(Applicability(copy), copy));
    }

    [Theory]
    [InlineData(CommodityType.Coffee)]
    [InlineData(CommodityType.Cocoa)]
    [InlineData(CommodityType.Rubber)]
    [InlineData(CommodityType.Soy)]
    [InlineData(CommodityType.OilPalm)]
    public void Supported_commodities_stay_within_the_supported_scope(CommodityType commodity)
    {
        var copy = Copy(commodity: commodity);
        Assert.Equal(ApplicabilityStatus.WithinSupportedScope, ApplicabilityDerivation.Derive(Applicability(copy), copy));
    }

    [Fact]
    public void Export_a_non_single_commodity_and_the_other_unsupported_role_each_reject_the_flow()
    {
        var copy = Copy();
        Assert.Equal(ApplicabilityStatus.UnsupportedFlow, ApplicabilityDerivation.Derive(Applicability(copy, activity: ApplicabilityActivityType.Export), copy));
        Assert.Equal(ApplicabilityStatus.UnsupportedFlow, ApplicabilityDerivation.Derive(Applicability(copy, single: false), copy));
        Assert.Equal(ApplicabilityStatus.UnsupportedFlow, ApplicabilityDerivation.Derive(Applicability(copy, role: OperatorRole.OtherUnsupported), copy));
        Assert.Equal(ApplicabilityStatus.WithinSupportedScope, ApplicabilityDerivation.Derive(Applicability(copy, activity: ApplicabilityActivityType.PlacingOnMarketDomestic), copy));
    }

    [Fact]
    public void A_different_product_id_with_the_same_cn_code_and_commodity_is_product_changed()
    {
        var assessed = Copy(ProductX); var applicability = Applicability(assessed);
        var swapped = Copy(ProductY);
        Assert.Equal(assessed.ProductCnCode, swapped.ProductCnCode); Assert.Equal(assessed.ProductCommodity, swapped.ProductCommodity);
        Assert.Equal(ApplicabilityStatus.ProductChanged, ApplicabilityDerivation.Derive(applicability, swapped));
    }

    [Fact]
    public void A_changed_cn_code_or_commodity_is_product_changed_and_takes_precedence_over_every_decision()
    {
        var assessed = Copy(); var applicability = Applicability(assessed, RegulatoryScope.OutsideRegulatoryScope);
        Assert.Equal(ApplicabilityStatus.ProductChanged, ApplicabilityDerivation.Derive(applicability, Copy(cn: "1801")));
        Assert.Equal(ApplicabilityStatus.ProductChanged, ApplicabilityDerivation.Derive(applicability, Copy(commodity: CommodityType.Cocoa)));
        Assert.Equal(ApplicabilityStatus.ProductChanged, ApplicabilityDerivation.Derive(Applicability(assessed, RegulatoryScope.Pending), Copy(ProductY)));
    }

    [Fact]
    public void A_supplier_or_operator_or_context_only_refresh_never_produces_product_changed()
    {
        var assessed = Copy(); var applicability = Applicability(assessed);
        var refreshed = Copy();
        refreshed.SupplierId = Guid.NewGuid(); refreshed.SupplierName = "Another supplier"; refreshed.SupplierEmail = "x@example.test";
        refreshed.OperatorLegalName = "Operator"; refreshed.OperatorEori = "HR1"; refreshed.ProductName = "Renamed"; refreshed.ProductCatalogueDescription = "Changed";
        Assert.Equal(ApplicabilityStatus.WithinSupportedScope, ApplicabilityDerivation.Derive(applicability, refreshed));
    }

    // ---- master-data copy, equality, drift and shipment binding -------------------------------------------------------------------------
    private static Supplier LiveSupplier() => new() { Id = SupplierA, Name = "Supplier", Email = "s@example.test", PostalAddress = "Street 1", CountryCode = "BR" };
    private static Product LiveProduct() => new() { Id = ProductX, Name = "Product", CnCode = "0901", CommodityType = CommodityType.Coffee, Description = "Catalogue text" };
    private static OperatorProfile LiveProfile() => new() { LegalName = "Op", PostalAddress = "Addr", Email = "op@example.test", EstablishmentCountryCode = "HR", EoriNumber = "HR123" };

    [Fact]
    public void Copy_takes_every_live_value_and_leaves_operator_values_null_without_a_profile()
    {
        var withProfile = CaseMasterDataService.Copy(Guid.NewGuid(), Guid.NewGuid(), LiveProfile(), LiveSupplier(), LiveProduct());
        Assert.Equal(("Op", "Addr", "op@example.test", "HR", "HR123"), (withProfile.OperatorLegalName, withProfile.OperatorPostalAddress, withProfile.OperatorEmail, withProfile.OperatorEstablishmentCountryCode, withProfile.OperatorEori));
        Assert.Equal((SupplierA, "Supplier", "Street 1", "s@example.test", "BR"), (withProfile.SupplierId, withProfile.SupplierName, withProfile.SupplierPostalAddress, withProfile.SupplierEmail, withProfile.SupplierCountryCode));
        Assert.Equal((ProductX, "0901", CommodityType.Coffee, "Product", "Catalogue text"), (withProfile.ProductId, withProfile.ProductCnCode, withProfile.ProductCommodity, withProfile.ProductName, withProfile.ProductCatalogueDescription));
        var without = CaseMasterDataService.Copy(Guid.NewGuid(), Guid.NewGuid(), null, LiveSupplier(), LiveProduct());
        Assert.Null(without.OperatorLegalName); Assert.Null(without.OperatorPostalAddress); Assert.Null(without.OperatorEmail); Assert.Null(without.OperatorEstablishmentCountryCode); Assert.Null(without.OperatorEori);
    }

    [Fact]
    public void Identical_means_every_copied_field_including_the_context_fields()
    {
        CaseMasterDataVersion Make() => CaseMasterDataService.Copy(Guid.NewGuid(), Guid.NewGuid(), LiveProfile(), LiveSupplier(), LiveProduct());
        Assert.True(CaseMasterDataService.IsIdentical(Make(), Make()));
        var mutations = new Action<CaseMasterDataVersion>[]
        {
            x => x.OperatorLegalName = "x", x => x.OperatorPostalAddress = "x", x => x.OperatorEmail = "x", x => x.OperatorEstablishmentCountryCode = "XX", x => x.OperatorEori = null,
            x => x.SupplierId = Guid.NewGuid(), x => x.SupplierName = "x", x => x.SupplierPostalAddress = null, x => x.SupplierEmail = "x", x => x.SupplierCountryCode = null,
            x => x.ProductId = Guid.NewGuid(), x => x.ProductCnCode = "1801", x => x.ProductCommodity = CommodityType.Soy, x => x.ProductName = "x", x => x.ProductCatalogueDescription = null,
        };
        foreach (var mutate in mutations) { var changed = Make(); mutate(changed); Assert.False(CaseMasterDataService.IsIdentical(Make(), changed)); }
    }

    [Fact]
    public void Drift_is_empty_when_live_values_equal_the_copy_and_ignores_the_context_fields()
    {
        var copy = CaseMasterDataService.Copy(Guid.NewGuid(), Guid.NewGuid(), LiveProfile(), LiveSupplier(), LiveProduct());
        var product = LiveProduct(); product.Name = "Renamed"; product.Description = "Another description";
        Assert.Empty(CaseMasterDataService.Drift(copy, LiveProfile(), LiveSupplier(), product));
        var supplier = LiveSupplier(); supplier.UpdatedAtUtc = DateTimeOffset.UtcNow.AddDays(1); supplier.ContactPerson = "Not a copied field"; supplier.Notes = "Not a copied field";
        Assert.Empty(CaseMasterDataService.Drift(copy, LiveProfile(), supplier, LiveProduct()));
    }

    [Fact]
    public void Drift_names_the_area_and_the_changed_fields_only()
    {
        var copy = CaseMasterDataService.Copy(Guid.NewGuid(), Guid.NewGuid(), LiveProfile(), LiveSupplier(), LiveProduct());
        var profile = LiveProfile(); profile.Email = "new@example.test"; profile.EoriNumber = null;
        var supplier = LiveSupplier(); supplier.Name = "Renamed"; supplier.CountryCode = "AR";
        var product = LiveProduct(); product.CnCode = "1801"; product.CommodityType = CommodityType.Cocoa;
        var drift = CaseMasterDataService.Drift(copy, profile, supplier, product);
        Assert.Equal([MasterDataArea.Operator, MasterDataArea.Supplier, MasterDataArea.Product], drift.Select(x => x.Area));
        Assert.Equal(["email", "eori"], drift[0].Fields); Assert.Equal(["name", "countryCode"], drift[1].Fields); Assert.Equal(["cnCode", "commodity"], drift[2].Fields);
    }

    [Fact]
    public void Drift_reports_a_missing_live_operator_profile_against_a_copied_one_and_not_against_an_empty_one()
    {
        var copied = CaseMasterDataService.Copy(Guid.NewGuid(), Guid.NewGuid(), LiveProfile(), LiveSupplier(), LiveProduct());
        Assert.Equal(["legalName", "postalAddress", "email", "establishmentCountryCode", "eori"], Assert.Single(CaseMasterDataService.Drift(copied, null, LiveSupplier(), LiveProduct())).Fields);
        var empty = CaseMasterDataService.Copy(Guid.NewGuid(), Guid.NewGuid(), null, LiveSupplier(), LiveProduct());
        Assert.Empty(CaseMasterDataService.Drift(empty, null, LiveSupplier(), LiveProduct()));
        Assert.Equal(MasterDataArea.Operator, Assert.Single(CaseMasterDataService.Drift(empty, LiveProfile(), LiveSupplier(), LiveProduct())).Area);
    }

    [Fact]
    public void Shipment_match_requires_both_the_supplier_and_the_product_of_the_current_shipment()
    {
        var copy = Copy(); var shipment = new Shipment { SupplierId = SupplierA, ProductId = ProductX };
        Assert.True(CaseMasterDataService.ShipmentMatches(copy, shipment));
        shipment.SupplierId = Guid.NewGuid(); Assert.False(CaseMasterDataService.ShipmentMatches(copy, shipment));
        shipment.SupplierId = SupplierA; shipment.ProductId = Guid.NewGuid(); Assert.False(CaseMasterDataService.ShipmentMatches(copy, shipment));
    }

    // ---- operator profile ---------------------------------------------------------------------------------------------------------------
    private static readonly OperatorProfileValidator ProfileValidator = new();
    private static OperatorProfileInput Profile(string name = "Op", string address = "Addr", string email = "op@example.test", string country = "HR", string? eori = null) => new(name, address, email, country, eori);

    [Fact]
    public void Operator_profile_requires_name_address_email_and_country_and_checks_formats()
    {
        Assert.True(ProfileValidator.Validate(OperatorProfileService.Normalize(Profile(eori: "HR123456789012345"))).IsValid);
        foreach (var bad in new[] { Profile(name: " "), Profile(address: ""), Profile(email: "not-an-email"), Profile(country: "hr"), Profile(country: ""), Profile(eori: "has space"), Profile(eori: "HR1234567890123456"), Profile(name: new string('x', 201)) })
            Assert.False(ProfileValidator.Validate(OperatorProfileService.Normalize(bad)).IsValid);
        Assert.Null(OperatorProfileService.Normalize(Profile(eori: "  ")).EoriNumber);
    }

    // ---- applicability input ------------------------------------------------------------------------------------------------------------
    private static readonly CaseApplicabilityValidator ApplicabilityValidator = new();
    private static CaseApplicabilityInput Input(RegulatoryScope scope = RegulatoryScope.InScope, FlowSupport flow = FlowSupport.Supported, OperatorRole role = OperatorRole.Operator, string? roleDescription = null,
        string? productDescription = "Green coffee beans", string? citation = "Art. 1", string? legalBasis = "2023/1115", string? reasoning = "Reasoning", DateOnly? effective = null, DateOnly? activity = null) =>
        new(productDescription, true, ApplicabilityActivityType.PlacingOnMarketByImport, role, roleDescription, activity ?? Day, ActivityDateKind.Planned, citation, null, legalBasis, effective ?? Day, reasoning, scope, flow, null);
    private static bool Valid(CaseApplicabilityInput input) => ApplicabilityValidator.Validate(CaseApplicabilityService.Normalize(input)).IsValid;

    [Fact]
    public void Operator_role_description_is_required_for_other_unsupported_and_not_permitted_for_operator()
    {
        Assert.True(Valid(Input(role: OperatorRole.Operator)));
        Assert.True(Valid(Input(role: OperatorRole.Operator, roleDescription: "   "))); // blank normalizes to null
        Assert.False(Valid(Input(role: OperatorRole.Operator, roleDescription: "Downstream trader")));
        Assert.False(Valid(Input(role: OperatorRole.OtherUnsupported)));
        Assert.False(Valid(Input(role: OperatorRole.OtherUnsupported, roleDescription: "  ")));
        Assert.True(Valid(Input(role: OperatorRole.OtherUnsupported, roleDescription: "Downstream trader")));
        Assert.False(Valid(Input(role: OperatorRole.OtherUnsupported, roleDescription: new string('x', 501))));
    }

    [Fact]
    public void A_decided_scope_requires_the_decision_fields_and_pending_does_not()
    {
        var pending = new CaseApplicabilityInput(null, false, ApplicabilityActivityType.PlacingOnMarketByImport, OperatorRole.Operator, null, null, ActivityDateKind.Planned, null, null, null, null, null, RegulatoryScope.Pending, FlowSupport.Pending, null);
        Assert.True(Valid(pending));
        foreach (var scope in new[] { RegulatoryScope.InScope, RegulatoryScope.OutsideRegulatoryScope })
        {
            Assert.True(Valid(Input(scope)));
            Assert.False(Valid(Input(scope, citation: " "))); Assert.False(Valid(Input(scope, legalBasis: null))); Assert.False(Valid(Input(scope, reasoning: "")));
            Assert.False(Valid(Input(scope) with { EffectiveDate = null })); Assert.False(Valid(Input(scope) with { ActivityDate = null }));
        }
    }

    [Fact]
    public void Product_description_is_required_only_for_in_scope_and_outside_scope_may_keep_flow_support_pending()
    {
        Assert.False(Valid(Input(RegulatoryScope.InScope, productDescription: " ")));
        Assert.True(Valid(Input(RegulatoryScope.OutsideRegulatoryScope, FlowSupport.Pending, productDescription: null)));
        Assert.True(Valid(Input(RegulatoryScope.InScope, FlowSupport.Pending)));
    }

    [Fact]
    public void Applicability_rejects_undefined_enums_and_over_long_text()
    {
        Assert.False(Valid(Input() with { RegulatoryScope = (RegulatoryScope)9 })); Assert.False(Valid(Input() with { FlowSupport = (FlowSupport)9 }));
        Assert.False(Valid(Input() with { ActivityType = (ApplicabilityActivityType)9 })); Assert.False(Valid(Input() with { OperatorRole = (OperatorRole)9 })); Assert.False(Valid(Input() with { ActivityDateKind = (ActivityDateKind)9 }));
        Assert.False(Valid(Input(productDescription: new string('x', 2001)))); Assert.False(Valid(Input(reasoning: new string('x', 4001))));
        Assert.False(Valid(Input(citation: new string('x', 1001)))); Assert.False(Valid(Input(legalBasis: new string('x', 201)))); Assert.False(Valid(Input() with { Qualifications = new string('x', 2001) }));
    }

    [Fact]
    public void Applicability_identity_compares_the_normalized_content_and_the_server_copied_product()
    {
        var copy = Copy();
        CaseApplicability Stored(CaseApplicabilityInput input) { var n = CaseApplicabilityService.Normalize(input); var a = Applicability(copy, n.RegulatoryScope, n.FlowSupport); a.ProductDescription = n.ProductDescription; a.ScopeCitation = n.ScopeCitation; a.LegalBasisVersion = n.LegalBasisVersion; a.Reasoning = n.Reasoning; a.ActivityDate = n.ActivityDate; a.EffectiveDate = n.EffectiveDate; a.ActivityDateKind = n.ActivityDateKind; return a; }
        var baseline = Input(); var stored = Stored(baseline);
        Assert.True(CaseApplicabilityService.IsIdentical(CaseApplicabilityService.Normalize(Input(productDescription: "  Green coffee beans ")), copy, stored));
        Assert.False(CaseApplicabilityService.IsIdentical(CaseApplicabilityService.Normalize(Input(reasoning: "Other")), copy, stored));
        Assert.False(CaseApplicabilityService.IsIdentical(CaseApplicabilityService.Normalize(Input(flow: FlowSupport.UnsupportedFlow)), copy, stored));
        // The same client content against a copy of a different product is a new assessment.
        Assert.False(CaseApplicabilityService.IsIdentical(CaseApplicabilityService.Normalize(baseline), Copy(ProductY), stored));
        Assert.False(CaseApplicabilityService.IsIdentical(CaseApplicabilityService.Normalize(baseline), Copy(cn: "1801"), stored));
    }

    // ---- recipients ---------------------------------------------------------------------------------------------------------------------
    private static readonly CaseRecipientsValidator RecipientsValidator = new();
    private static CaseRecipientInput Recipient(string name = "Recipient", string address = "Address 1", string email = "r@example.test") => new(name, address, email);
    private static CaseRecipientsInput Identified(params CaseRecipientInput[] recipients) => new(RecipientResolution.RecipientsIdentified, null, [.. recipients]);
    private static bool RecipientsValid(CaseRecipientsInput input) => RecipientsValidator.Validate(CaseRecipientService.Normalize(input)).IsValid;

    [Fact]
    public void Recipient_statement_tri_state_is_validated_strictly()
    {
        Assert.True(RecipientsValid(Identified(Recipient())));
        Assert.False(RecipientsValid(Identified()));
        Assert.False(RecipientsValid(new(RecipientResolution.RecipientsIdentified, null, null))); // missing is never silently "not applicable"
        Assert.False(RecipientsValid(new(RecipientResolution.RecipientsIdentified, "basis", [Recipient()])));
        Assert.True(RecipientsValid(new(RecipientResolution.NotApplicable, "Sold at the point of import", null)));
        Assert.True(RecipientsValid(new(RecipientResolution.NotApplicable, "Sold at the point of import", [])));
        Assert.False(RecipientsValid(new(RecipientResolution.NotApplicable, null, [])));
        Assert.False(RecipientsValid(new(RecipientResolution.NotApplicable, "   ", [])));
        Assert.False(RecipientsValid(new(RecipientResolution.NotApplicable, "basis", [Recipient()])));
        Assert.False(RecipientsValid(new((RecipientResolution)9, "basis", [])));
    }

    [Fact]
    public void Recipient_count_is_bounded_and_every_field_is_required()
    {
        Assert.True(RecipientsValid(Identified([.. Enumerable.Range(0, 50).Select(i => Recipient("R" + i))])));
        Assert.False(RecipientsValid(Identified([.. Enumerable.Range(0, 51).Select(i => Recipient("R" + i))])));
        foreach (var bad in new[] { Recipient(name: " "), Recipient(address: ""), Recipient(email: ""), Recipient(email: "nope"), Recipient(name: new string('x', 201)), Recipient(address: new string('x', 1001)) })
            Assert.False(RecipientsValid(Identified(bad)));
        Assert.False(RecipientsValid(Identified(Recipient(), null!)));
    }

    [Fact]
    public void Recipient_normalization_trims_and_identity_is_order_independent()
    {
        var normalized = CaseRecipientService.Normalize(new(RecipientResolution.RecipientsIdentified, "  ", [Recipient("  A ", " Addr ", " a@example.test ")]));
        Assert.Null(normalized.NotApplicableBasis); Assert.Equal(new CaseRecipientInput("A", "Addr", "a@example.test"), Assert.Single(normalized.Recipients!));
        Assert.Empty(CaseRecipientService.Normalize(new(RecipientResolution.NotApplicable, "b", null)).Recipients!);

        var statement = new CaseRecipientStatement { Resolution = RecipientResolution.RecipientsIdentified };
        var stored = new[] { Recipient("A", "1", "a@example.test"), Recipient("B", "2", "b@example.test") }.Select(r => new CaseRecipient { Name = r.Name, PostalAddress = r.PostalAddress, Email = r.Email }).ToList();
        Assert.True(CaseRecipientService.IsIdentical(CaseRecipientService.Normalize(Identified(Recipient("B", "2", "b@example.test"), Recipient(" A", "1", "a@example.test"))), statement, stored));
        Assert.False(CaseRecipientService.IsIdentical(CaseRecipientService.Normalize(Identified(Recipient("A", "1", "a@example.test"))), statement, stored));
        Assert.False(CaseRecipientService.IsIdentical(CaseRecipientService.Normalize(Identified(Recipient("A", "1", "a@example.test"), Recipient("B", "2", "other@example.test"))), statement, stored));
        Assert.False(CaseRecipientService.IsIdentical(CaseRecipientService.Normalize(new(RecipientResolution.NotApplicable, "basis", [])), statement, stored));
    }

    [Fact]
    public void Evidence_link_input_requires_a_document_and_a_defined_purpose()
    {
        var validator = new CaseEvidenceLinkValidator();
        Assert.True(validator.Validate(new CaseEvidenceLinkInput(Guid.NewGuid(), EvidencePurpose.Legality, null)).IsValid);
        Assert.False(validator.Validate(new CaseEvidenceLinkInput(Guid.Empty, EvidencePurpose.Legality, null)).IsValid);
        Assert.False(validator.Validate(new CaseEvidenceLinkInput(Guid.NewGuid(), (EvidencePurpose)9, null)).IsValid);
        Assert.False(validator.Validate(new CaseEvidenceLinkInput(Guid.NewGuid(), EvidencePurpose.Legality, new string('x', 1001))).IsValid);
    }

    [Fact]
    public void Supplier_postal_address_is_optional_trailing_and_bounded()
    {
        var validator = new SupplierValidator();
        Assert.True(validator.Validate(new SupplierInput("Supplier", null, null, null, null, null)).IsValid); // existing six-argument callers still compile and validate
        Assert.True(validator.Validate(new SupplierInput("Supplier", null, null, null, null, null, new string('x', 1000))).IsValid);
        Assert.False(validator.Validate(new SupplierInput("Supplier", null, null, null, null, null, new string('x', 1001))).IsValid);
    }
}
