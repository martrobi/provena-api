using Provena.Api.Contracts;
using Provena.Api.Domain;
using Provena.Api.Services;
using Xunit;

namespace Provena.Api.UnitTests;

// Slice 5 / commit 2: the pure completeness evaluator (policy v1), the three axes, the advisories and the canonical fingerprint.
public sealed class CompletenessTests
{
    private static readonly Guid SupplierId = Guid.Parse("00000000-0000-0000-0000-0000000000a1"), ProductId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid ProducerId = Guid.Parse("00000000-0000-0000-0000-0000000000c1"), PlotId = Guid.Parse("00000000-0000-0000-0000-0000000000d1"), RevisionId = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    private static readonly Guid DocumentA = Guid.Parse("00000000-0000-0000-0000-0000000000f1"), DocumentB = Guid.Parse("00000000-0000-0000-0000-0000000000f2");
    private static readonly DateTimeOffset At = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly D1 = new(2024, 1, 1), D2 = new(2024, 12, 31);

    private static Guid Fixed(int n) => new($"00000000-0000-0000-0001-{n:D12}");

    // A fully satisfied dossier; each test mutates the (fresh) parts it cares about.
    internal static DossierFacts Complete()
    {
        var shipment = new Shipment { Id = Fixed(1), ReferenceNumber = "SHP-1", SupplierId = SupplierId, ProductId = ProductId, Quantity = 10m, Unit = QuantityUnit.Kg, OriginCountryCode = "BR" };
        var md = new CaseMasterDataVersion
        {
            Id = Fixed(2), Number = 1, OperatorLegalName = "Op d.o.o.", OperatorPostalAddress = "Ilica 1", OperatorEmail = "op@example.test", OperatorEstablishmentCountryCode = "HR", OperatorEori = "HR1",
            SupplierId = SupplierId, SupplierName = "Supplier", SupplierPostalAddress = "Street 1", SupplierEmail = "s@example.test", SupplierCountryCode = "BR",
            ProductId = ProductId, ProductCnCode = "0901", ProductCommodity = CommodityType.Coffee, ProductName = "Coffee", ProductCatalogueDescription = "Catalogue text",
        };
        var app = new CaseApplicability
        {
            Id = Fixed(3), Number = 1, AssessedProductId = ProductId, AssessedCnCode = "0901", AssessedCommodity = CommodityType.Coffee, ProductDescription = "Green coffee beans", SingleRelevantCommodity = true,
            ActivityType = ApplicabilityActivityType.PlacingOnMarketByImport, OperatorRole = OperatorRole.Operator, ActivityDate = D1, ActivityDateKind = ActivityDateKind.Planned, ScopeCitation = "Art. 1",
            LegalBasisVersion = "2023/1115", EffectiveDate = D1, Reasoning = "Because", RegulatoryScope = RegulatoryScope.InScope, FlowSupport = FlowSupport.Supported, DecidedByUserId = Fixed(90), DecidedAtUtc = At,
        };
        var statement = new CaseRecipientStatement { Id = Fixed(4), Number = 1, Resolution = RecipientResolution.RecipientsIdentified };
        var recipient = new CaseRecipient { Id = Fixed(5), CaseRecipientStatementId = statement.Id, Name = "Importer", PostalAddress = "Main 1", Email = "i@example.test" };
        var dossier = new CaseDossier { Id = Fixed(6) };
        var lot = new CaseLot { Id = Fixed(7), CaseDossierId = dossier.Id, Number = 1, LotReference = "LOT-1", Quantity = 10m, Unit = QuantityUnit.Kg };
        var contribution = new LotContribution { Id = Fixed(8), CaseLotId = lot.Id, ProducerId = ProducerId, Quantity = 10m, Unit = QuantityUnit.Kg };
        var revision = new PlotRevision { Id = RevisionId, ProductionPlotId = PlotId, Number = 1, CountryCode = "BR", GeometryType = PlotGeometryType.Polygon, GeometryJson = """{"type":"Polygon"}""" };
        var link = new ContributionPlot { Id = Fixed(9), LotContributionId = contribution.Id, ProducerId = ProducerId, ProductionPlotId = PlotId, PlotRevisionId = RevisionId, ProductionStartDate = D1, ProductionEndDate = D2 };
        var documents = new List<CaseDocument>
        {
            new() { Id = DocumentA, OriginalFileName = "a.pdf", ContentType = "application/pdf", SizeBytes = 10, Category = DocumentCategory.Certificate, UploadedByType = UploadedByType.InternalUser, UploadedAtUtc = At },
            new() { Id = DocumentB, OriginalFileName = "b.pdf", ContentType = "application/pdf", SizeBytes = 20, Category = DocumentCategory.Other, UploadedByType = UploadedByType.Supplier, UploadedAtUtc = At },
        };
        var links = new List<CaseEvidenceLink>
        {
            new() { Id = Fixed(20), CaseDocumentId = DocumentA, Purpose = EvidencePurpose.Legality },
            new() { Id = Fixed(21), CaseDocumentId = DocumentB, Purpose = EvidencePurpose.DeforestationFree, Note = "n" },
        };
        var requirements = Enum.GetValues<RequirementType>().Select((t, i) => new EudrRequirement { Id = Fixed(30 + i), Type = t, IsRequired = true, Status = RequirementStatus.Accepted, ReviewedByUserId = Fixed(91), ReviewedAtUtc = At }).ToList();
        return new(shipment, md, app, statement, [recipient], dossier, lot, [new(contribution, [new(link, revision)])], documents,
            new GeoSubmission { Id = Fixed(40), OriginalFileName = "g.geojson", FeatureCount = 1, GeometryTypes = "Polygon", ValidationStatus = GeoValidationStatus.Valid, UploadedAtUtc = At },
            new SupplierSubmission { Id = Fixed(41), CountryOfProductionCode = "BR", ConfirmedQuantity = 10m, SubmittedAtUtc = At }, links, requirements, []);
    }

    private static CompletenessItemDto Get(DossierFacts f, string code, Guid? subject = null) =>
        CompletenessEvaluator.Evaluate(f).Items.Single(i => i.Code == code && (subject == null || i.SubjectId == subject));
    private static CompletenessItemStatus Status(DossierFacts f, string code, Guid? subject = null) => Get(f, code, subject).Status;

    [Fact]
    public void A_fully_recorded_dossier_is_complete_and_ready_for_owner_review_without_any_reviewed_state()
    {
        var result = CompletenessEvaluator.Evaluate(Complete());
        Assert.Equal(DataCoverage.Complete, result.DataCoverage); Assert.Equal(EvidenceReadiness.ReadyForOwnerReview, result.EvidenceReadiness);
        Assert.Equal(ApplicabilityStatus.WithinSupportedScope, result.ApplicabilityStatus);
        Assert.DoesNotContain(result.Items, i => i.Status is CompletenessItemStatus.Missing or CompletenessItemStatus.Invalid or CompletenessItemStatus.AwaitingReview);
        Assert.Equal(result.Items.Count, result.Items.Select(i => (i.Code, i.SubjectId)).Distinct().Count());
    }

    // ---- master data ---------------------------------------------------------------------------------------------------------------------
    [Fact]
    public void Master_data_shipment_match_is_missing_without_a_copy_invalid_on_either_mismatch_and_satisfied_otherwise()
    {
        const string code = "MASTER_DATA_SHIPMENT_MATCH";
        var f = Complete(); Assert.Equal(CompletenessItemStatus.Satisfied, Status(f, code));
        Assert.Equal(CompletenessItemStatus.Missing, Status(Complete() with { MasterData = null }, code));
        var supplier = Complete(); supplier.Shipment.SupplierId = Guid.NewGuid();
        Assert.Equal(CompletenessItemStatus.Invalid, Status(supplier, code)); Assert.Contains("supplier", Get(supplier, code).Reason); Assert.Contains("refresh", Get(supplier, code).Reason);
        var product = Complete(); product.Shipment.ProductId = Guid.NewGuid();
        Assert.Equal(CompletenessItemStatus.Invalid, Status(product, code)); Assert.Contains("product", Get(product, code).Reason);
        var both = Complete(); both.Shipment.SupplierId = Guid.NewGuid(); both.Shipment.ProductId = Guid.NewGuid(); Assert.Contains("supplier and product", Get(both, code).Reason);
        Assert.Equal(DataCoverage.Incomplete, CompletenessEvaluator.Evaluate(supplier).DataCoverage);
    }

    // ---- applicability -------------------------------------------------------------------------------------------------------------------
    private static readonly string[] ApplicabilityCodes = ["APPLICABILITY_RECORDED", "APPLICABILITY_DECIDED", "APPLICABILITY_PRODUCT_MATCH", "APPLICABILITY_IN_SCOPE", "FLOW_SUPPORTED"];

    [Fact]
    public void Without_an_applicability_every_applicability_item_is_missing()
    {
        var f = Complete() with { Applicability = null };
        Assert.All(ApplicabilityCodes, c => Assert.Equal(CompletenessItemStatus.Missing, Status(f, c)));
        Assert.Equal(ApplicabilityStatus.NotRecorded, CompletenessEvaluator.Evaluate(f).ApplicabilityStatus);
    }

    [Fact]
    public void A_pending_scope_or_pending_flow_support_is_not_decided()
    {
        var pending = Complete(); pending.Applicability!.RegulatoryScope = RegulatoryScope.Pending;
        Assert.Equal(CompletenessItemStatus.Missing, Status(pending, "APPLICABILITY_DECIDED")); Assert.Equal(CompletenessItemStatus.Missing, Status(pending, "APPLICABILITY_IN_SCOPE")); Assert.Equal(CompletenessItemStatus.Missing, Status(pending, "FLOW_SUPPORTED"));
        Assert.Equal(ApplicabilityStatus.Pending, CompletenessEvaluator.Evaluate(pending).ApplicabilityStatus);
        var flow = Complete(); flow.Applicability!.FlowSupport = FlowSupport.Pending;
        Assert.Equal(CompletenessItemStatus.Missing, Status(flow, "APPLICABILITY_DECIDED")); Assert.Equal(CompletenessItemStatus.Missing, Status(flow, "FLOW_SUPPORTED")); Assert.Equal(CompletenessItemStatus.Satisfied, Status(flow, "APPLICABILITY_IN_SCOPE"));
    }

    [Fact]
    public void Outside_regulatory_scope_is_invalid_for_this_policy_and_makes_the_flow_not_applicable()
    {
        var f = Complete(); f.Applicability!.RegulatoryScope = RegulatoryScope.OutsideRegulatoryScope; f.Applicability.FlowSupport = FlowSupport.Pending;
        Assert.Equal(CompletenessItemStatus.Satisfied, Status(f, "APPLICABILITY_DECIDED"));
        Assert.Equal(CompletenessItemStatus.Invalid, Status(f, "APPLICABILITY_IN_SCOPE")); Assert.Contains("outside this policy's scope", Get(f, "APPLICABILITY_IN_SCOPE").Reason); Assert.DoesNotContain("compliant", Get(f, "APPLICABILITY_IN_SCOPE").Reason.Replace("non-compliance", ""));
        Assert.Equal(CompletenessItemStatus.NotApplicable, Status(f, "FLOW_SUPPORTED"));
        Assert.Equal(ApplicabilityStatus.OutsideRegulatoryScope, CompletenessEvaluator.Evaluate(f).ApplicabilityStatus);
    }

    [Theory]
    [InlineData("recorded")] [InlineData("wood")] [InlineData("cattle")] [InlineData("other")] [InlineData("export")] [InlineData("multi")] [InlineData("role")]
    public void Unsupported_flows_are_invalid_never_complete(string cause)
    {
        var f = Complete(); var a = f.Applicability!;
        switch (cause)
        {
            case "recorded": a.FlowSupport = FlowSupport.UnsupportedFlow; break;
            case "wood": a.AssessedCommodity = CommodityType.Wood; break;
            case "cattle": a.AssessedCommodity = CommodityType.Cattle; break;
            case "other": a.AssessedCommodity = CommodityType.Other; break;
            case "export": a.ActivityType = ApplicabilityActivityType.Export; break;
            case "multi": a.SingleRelevantCommodity = false; break;
            case "role": a.OperatorRole = OperatorRole.OtherUnsupported; a.OperatorRoleDescription = "Trader"; break;
        }
        f.MasterData!.ProductCommodity = a.AssessedCommodity; // keep the assessed product equal to the copy so ProductChanged does not mask the cause
        Assert.Equal(CompletenessItemStatus.Invalid, Status(f, "FLOW_SUPPORTED")); Assert.Contains("unsupported by pilot policy v1", Get(f, "FLOW_SUPPORTED").Reason);
        Assert.Equal(ApplicabilityStatus.UnsupportedFlow, CompletenessEvaluator.Evaluate(f).ApplicabilityStatus);
    }

    [Fact]
    public void A_changed_product_is_invalid_on_the_product_match_item_and_a_supplier_only_change_is_not()
    {
        var swapped = Complete(); swapped.MasterData!.ProductId = Guid.NewGuid(); swapped.Shipment.ProductId = swapped.MasterData.ProductId; // same CN and commodity, different product id
        Assert.Equal(CompletenessItemStatus.Invalid, Status(swapped, "APPLICABILITY_PRODUCT_MATCH")); Assert.Equal(ApplicabilityStatus.ProductChanged, CompletenessEvaluator.Evaluate(swapped).ApplicabilityStatus);
        var supplierOnly = Complete(); supplierOnly.MasterData!.SupplierId = Guid.NewGuid(); supplierOnly.MasterData.SupplierName = "Another"; supplierOnly.Shipment.SupplierId = supplierOnly.MasterData.SupplierId;
        Assert.Equal(CompletenessItemStatus.Satisfied, Status(supplierOnly, "APPLICABILITY_PRODUCT_MATCH"));
    }

    // ---- product --------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public void Product_description_is_read_from_the_applicability_never_from_the_catalogue_description()
    {
        var f = Complete(); f.MasterData!.ProductCatalogueDescription = null; // a blank catalogue description does not matter
        Assert.Equal(CompletenessItemStatus.Satisfied, Status(f, "PRODUCT_DESCRIPTION"));
        var blank = Complete(); blank.Applicability!.ProductDescription = "  "; blank.MasterData!.ProductCatalogueDescription = "A fine catalogue description";
        Assert.Equal(CompletenessItemStatus.Missing, Status(blank, "PRODUCT_DESCRIPTION"));
        Assert.Equal(CompletenessItemStatus.Missing, Status(Complete() with { Applicability = null }, "PRODUCT_DESCRIPTION"));
    }

    [Theory]
    [InlineData("0901", CompletenessItemStatus.Satisfied)] [InlineData("0901210000", CompletenessItemStatus.Satisfied)] [InlineData("090", CompletenessItemStatus.Invalid)] [InlineData("09012100001", CompletenessItemStatus.Invalid)] [InlineData("09A1", CompletenessItemStatus.Invalid)]
    public void Cn_code_is_checked_for_format_only(string cn, CompletenessItemStatus expected)
    {
        var f = Complete(); f.MasterData!.ProductCnCode = cn;
        Assert.Equal(expected, Status(f, "PRODUCT_CN_CODE")); Assert.Contains("format", Get(f, "PRODUCT_CN_CODE").Reason);
        Assert.Contains("not regulatory scope", Get(f, "PRODUCT_CN_CODE").Reason);
        Assert.Equal(CompletenessItemStatus.Missing, Status(Complete() with { MasterData = null }, "PRODUCT_CN_CODE"));
    }

    // ---- quantity -------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public void Lot_items_follow_the_exact_reconciliation_without_shipment_comparison_or_tolerance()
    {
        var none = Complete() with { Dossier = null, Lot = null, Contributions = [] };
        Assert.Equal(CompletenessItemStatus.Missing, Status(none, "LOT_PRESENT")); Assert.Equal(CompletenessItemStatus.Missing, Status(none, "LOT_RECONCILED"));
        Assert.Equal(CompletenessItemStatus.Satisfied, Status(Complete(), "LOT_RECONCILED"));
        var under = Complete(); under.Contributions[0].Contribution.Quantity = 9.999999m;
        Assert.Equal(CompletenessItemStatus.Satisfied, Status(under, "LOT_PRESENT")); Assert.Equal(CompletenessItemStatus.Missing, Status(under, "LOT_RECONCILED"));
        Assert.Contains("remaining 0.000001 Kg", Get(under, "LOT_RECONCILED").Reason);
        var equalDifferentScale = Complete(); equalDifferentScale.Contributions[0].Contribution.Quantity = 10.000000m; equalDifferentScale.Lot!.Quantity = 10m;
        Assert.Equal(CompletenessItemStatus.Satisfied, Status(equalDifferentScale, "LOT_RECONCILED"));
        var differsFromShipment = Complete(); differsFromShipment.Shipment.Quantity = 999m; differsFromShipment.Submission!.ConfirmedQuantity = 1m;
        Assert.Equal(CompletenessItemStatus.Satisfied, Status(differsFromShipment, "LOT_RECONCILED")); // never compared with the shipment or supplier-confirmed quantity
    }

    // ---- production -----------------------------------------------------------------------------------------------------------------------
    [Fact]
    public void Every_contribution_needs_a_plot_and_every_link_needs_both_period_dates()
    {
        var noPlots = Complete(); noPlots = noPlots with { Contributions = [new(noPlots.Contributions[0].Contribution, [])] };
        Assert.Equal(CompletenessItemStatus.Missing, Status(noPlots, "SOURCE_HAS_PLOT"));
        Assert.DoesNotContain(CompletenessEvaluator.Evaluate(noPlots).Items, i => i.Code is "SOURCE_PRODUCTION_PERIOD" or "SOURCE_GEOLOCATION_FORM");
        Assert.Equal(CompletenessItemStatus.Satisfied, Status(Complete(), "SOURCE_HAS_PLOT", Fixed(8)));
        foreach (var tweak in new Action<ContributionPlot>[] { l => l.ProductionStartDate = null, l => l.ProductionEndDate = null, l => { l.ProductionStartDate = null; l.ProductionEndDate = null; } })
        {
            var f = Complete(); tweak(f.Contributions[0].Links[0].Link);
            Assert.Equal(CompletenessItemStatus.Missing, Status(f, "SOURCE_PRODUCTION_PERIOD", Fixed(9)));
        }
        Assert.Equal(CompletenessItemStatus.Satisfied, Status(Complete(), "SOURCE_PRODUCTION_PERIOD", Fixed(9)));
        var outOfWindow = Complete(); outOfWindow.Contributions[0].Links[0].Link.ProductionStartDate = new DateOnly(1999, 1, 1); outOfWindow.Contributions[0].Links[0].Link.ProductionEndDate = new DateOnly(1999, 2, 1);
        Assert.Equal(CompletenessItemStatus.Satisfied, Status(outOfWindow, "SOURCE_PRODUCTION_PERIOD")); // no date-window check
    }

    [Theory]
    [InlineData(PlotGeometryType.Polygon, null, CompletenessItemStatus.Satisfied)]
    [InlineData(PlotGeometryType.Polygon, 500.0, CompletenessItemStatus.Satisfied)]
    [InlineData(PlotGeometryType.MultiPolygon, null, CompletenessItemStatus.Satisfied)]
    [InlineData(PlotGeometryType.Point, 4.0, CompletenessItemStatus.Satisfied)]
    [InlineData(PlotGeometryType.Point, 1.5, CompletenessItemStatus.Satisfied)]
    [InlineData(PlotGeometryType.Point, null, CompletenessItemStatus.Missing)]
    [InlineData(PlotGeometryType.Point, 4.0001, CompletenessItemStatus.Invalid)]
    [InlineData(PlotGeometryType.Point, 20.0, CompletenessItemStatus.Invalid)]
    public void Geolocation_form_uses_the_declared_area_rule_for_points_only(PlotGeometryType type, double? area, CompletenessItemStatus expected)
    {
        var f = Complete(); var revision = f.Contributions[0].Links[0].Revision; revision.GeometryType = type; revision.AreaHectares = area.HasValue ? (decimal)area.Value : null;
        var item = Get(f, "SOURCE_GEOLOCATION_FORM", RevisionId);
        Assert.Equal(expected, item.Status); Assert.Equal("PlotRevision", item.SubjectType);
        Assert.DoesNotContain("proof", item.Reason.Replace("not proof", "")); // never claims geographic truth
    }

    // ---- parties --------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public void Operator_facts_are_evaluated_on_the_case_copy_and_eori_does_not_participate()
    {
        Assert.Equal(CompletenessItemStatus.Satisfied, Status(Complete(), "PARTY_OPERATOR"));
        Assert.Equal(CompletenessItemStatus.Missing, Status(Complete() with { MasterData = null }, "PARTY_OPERATOR"));
        foreach (var tweak in new Action<CaseMasterDataVersion>[] { m => m.OperatorLegalName = null, m => m.OperatorPostalAddress = " ", m => m.OperatorEmail = "", m => m.OperatorEstablishmentCountryCode = null })
        { var f = Complete(); tweak(f.MasterData!); Assert.Equal(CompletenessItemStatus.Missing, Status(f, "PARTY_OPERATOR")); }
        var noEori = Complete(); noEori.MasterData!.OperatorEori = null; Assert.Equal(CompletenessItemStatus.Satisfied, Status(noEori, "PARTY_OPERATOR"));
    }

    [Fact]
    public void Supplier_name_address_and_email_are_evaluated_independently()
    {
        var f = Complete(); f.MasterData!.SupplierPostalAddress = null;
        Assert.Equal(CompletenessItemStatus.Satisfied, Status(f, "PARTY_SUPPLIER_NAME")); Assert.Equal(CompletenessItemStatus.Missing, Status(f, "PARTY_SUPPLIER_POSTAL_ADDRESS")); Assert.Equal(CompletenessItemStatus.Satisfied, Status(f, "PARTY_SUPPLIER_EMAIL"));
        var email = Complete(); email.MasterData!.SupplierEmail = " "; Assert.Equal(CompletenessItemStatus.Missing, Status(email, "PARTY_SUPPLIER_EMAIL")); Assert.Equal(CompletenessItemStatus.Satisfied, Status(email, "PARTY_SUPPLIER_POSTAL_ADDRESS"));
        var name = Complete(); name.MasterData!.SupplierName = ""; Assert.Equal(CompletenessItemStatus.Missing, Status(name, "PARTY_SUPPLIER_NAME"));
        var none = Complete() with { MasterData = null };
        Assert.All(new[] { "PARTY_SUPPLIER_NAME", "PARTY_SUPPLIER_POSTAL_ADDRESS", "PARTY_SUPPLIER_EMAIL" }, c => Assert.Equal(CompletenessItemStatus.Missing, Status(none, c)));
    }

    [Fact]
    public void Recipients_are_unresolved_identified_or_not_applicable_with_a_basis_and_never_derived_from_absence()
    {
        var unresolved = Complete() with { Recipients = null, RecipientEntries = [] };
        Assert.Equal(CompletenessItemStatus.Missing, Status(unresolved, "PARTY_RECIPIENTS")); Assert.Contains("unresolved", Get(unresolved, "PARTY_RECIPIENTS").Reason);
        Assert.Equal(CompletenessItemStatus.Satisfied, Status(Complete(), "PARTY_RECIPIENTS"));
        var na = Complete() with { RecipientEntries = [] }; na.Recipients!.Resolution = RecipientResolution.NotApplicable; na.Recipients.NotApplicableBasis = "Stays with the operator";
        Assert.Equal(CompletenessItemStatus.NotApplicable, Status(na, "PARTY_RECIPIENTS"));
        na.Recipients.NotApplicableBasis = " "; Assert.Equal(CompletenessItemStatus.Invalid, Status(na, "PARTY_RECIPIENTS"));
        var identifiedButEmpty = Complete() with { RecipientEntries = [] }; Assert.Equal(CompletenessItemStatus.Invalid, Status(identifiedButEmpty, "PARTY_RECIPIENTS"));
        var incomplete = Complete(); incomplete.RecipientEntries[0].Email = ""; Assert.Equal(CompletenessItemStatus.Invalid, Status(incomplete, "PARTY_RECIPIENTS"));
    }

    // ---- requirements and evidence links ----------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData(RequirementStatus.Accepted, null, CompletenessItemStatus.Satisfied)]
    [InlineData(RequirementStatus.Submitted, null, CompletenessItemStatus.AwaitingReview)]
    [InlineData(RequirementStatus.Missing, null, CompletenessItemStatus.Missing)]
    [InlineData(RequirementStatus.Rejected, "no", CompletenessItemStatus.Invalid)]
    [InlineData(RequirementStatus.NotRequired, "Not needed for this commodity", CompletenessItemStatus.NotApplicable)]
    [InlineData(RequirementStatus.NotRequired, null, CompletenessItemStatus.Missing)]
    [InlineData(RequirementStatus.NotRequired, "   ", CompletenessItemStatus.Missing)]
    public void Requirement_statuses_map_to_evidence_axis_items(RequirementStatus status, string? note, CompletenessItemStatus expected)
    {
        var f = Complete(); var r = f.Requirements.Single(x => x.Type == RequirementType.Geolocation); r.Status = status; r.ReviewerNote = note;
        var item = Get(f, "REQUIREMENT_Geolocation");
        Assert.Equal(expected, item.Status); Assert.Equal(CompletenessGroup.Evidence, item.Group); Assert.Equal("Requirement", item.SubjectType); Assert.Equal(r.Id, item.SubjectId);
        if (status == RequirementStatus.Accepted) Assert.Contains("not a review of the dossier", item.Reason);
        Assert.Equal(6, CompletenessEvaluator.Evaluate(f).Items.Count(i => i.Code.StartsWith("REQUIREMENT_")));
    }

    [Fact]
    public void Evidence_links_count_only_when_not_withdrawn_and_linked_to_a_current_document()
    {
        const string legality = "EVIDENCE_LEGALITY_PRESENT", free = "EVIDENCE_DEFORESTATION_FREE_PRESENT";
        var ok = Complete(); Assert.Equal(CompletenessItemStatus.Satisfied, Status(ok, legality)); Assert.Equal(CompletenessItemStatus.Satisfied, Status(ok, free));
        Assert.Contains("presence only; not reviewed and not a sufficiency finding", Get(ok, legality).Reason);
        var none = Complete() with { EvidenceLinks = [] }; Assert.Equal(CompletenessItemStatus.Missing, Status(none, legality)); Assert.Equal(CompletenessItemStatus.Missing, Status(none, free));
        var withdrawnLink = Complete(); withdrawnLink.EvidenceLinks[0].WithdrawnAtUtc = At; Assert.Equal(CompletenessItemStatus.Missing, Status(withdrawnLink, legality)); Assert.Equal(CompletenessItemStatus.Satisfied, Status(withdrawnLink, free));
        var withdrawnDocument = Complete() with { Documents = Complete().Documents.Where(d => d.Id != DocumentA).ToList() }; // a withdrawn document is not a current document
        Assert.Equal(CompletenessItemStatus.Missing, Status(withdrawnDocument, legality)); Assert.Equal(CompletenessItemStatus.Satisfied, Status(withdrawnDocument, free));
        var wrongPurpose = Complete(); wrongPurpose.EvidenceLinks[0].Purpose = EvidencePurpose.Applicability; Assert.Equal(CompletenessItemStatus.Missing, Status(wrongPurpose, legality));
        var rowButWithdrawnFlag = Complete(); rowButWithdrawnFlag.Documents[0].WithdrawnAtUtc = At; Assert.Equal(CompletenessItemStatus.Missing, Status(rowButWithdrawnFlag, legality));
    }

    // ---- the three axes -------------------------------------------------------------------------------------------------------------------
    [Fact]
    public void Data_coverage_is_independent_of_the_evidence_group()
    {
        var evidenceBroken = Complete() with { EvidenceLinks = [] }; foreach (var r in evidenceBroken.Requirements) r.Status = RequirementStatus.Missing;
        var result = CompletenessEvaluator.Evaluate(evidenceBroken);
        Assert.Equal(DataCoverage.Complete, result.DataCoverage); Assert.Equal(EvidenceReadiness.EvidenceMissing, result.EvidenceReadiness);
        var dataBroken = Complete() with { Recipients = null, RecipientEntries = [] };
        var second = CompletenessEvaluator.Evaluate(dataBroken);
        Assert.Equal(DataCoverage.Incomplete, second.DataCoverage); Assert.Equal(EvidenceReadiness.ReadyForOwnerReview, second.EvidenceReadiness);
        // Not-applicable and outside-scope states do not hide missing data; Invalid counts like Missing for coverage.
        var invalid = Complete(); invalid.MasterData!.ProductCnCode = "x"; Assert.Equal(DataCoverage.Incomplete, CompletenessEvaluator.Evaluate(invalid).DataCoverage);
    }

    [Fact]
    public void Evidence_readiness_has_missing_precedence_then_awaiting_decisions_then_ready()
    {
        var f = Complete(); f.Requirements[0].Status = RequirementStatus.Submitted;
        Assert.Equal(EvidenceReadiness.AwaitingRequirementDecisions, CompletenessEvaluator.Evaluate(f).EvidenceReadiness);
        f.Requirements[1].Status = RequirementStatus.Missing; Assert.Equal(EvidenceReadiness.EvidenceMissing, CompletenessEvaluator.Evaluate(f).EvidenceReadiness); // Missing wins over Submitted
        f.Requirements[1].Status = RequirementStatus.Rejected; Assert.Equal(EvidenceReadiness.EvidenceMissing, CompletenessEvaluator.Evaluate(f).EvidenceReadiness);
        f.Requirements[1].Status = RequirementStatus.NotRequired; f.Requirements[1].ReviewerNote = "n/a"; f.Requirements[0].Status = RequirementStatus.Accepted;
        Assert.Equal(EvidenceReadiness.ReadyForOwnerReview, CompletenessEvaluator.Evaluate(f).EvidenceReadiness);
        var missingLink = Complete() with { EvidenceLinks = [] }; Assert.Equal(EvidenceReadiness.EvidenceMissing, CompletenessEvaluator.Evaluate(missingLink).EvidenceReadiness);
    }

    [Fact]
    public void Review_freshness_has_no_review_state_yet_and_the_axis_enums_pin_the_vocabulary()
    {
        Assert.Equal(["None", "Current", "Stale"], Enum.GetNames<ReviewFreshness>());
        Assert.Equal(["EvidenceMissing", "AwaitingRequirementDecisions", "ReadyForOwnerReview"], Enum.GetNames<EvidenceReadiness>());
        Assert.Equal(["Complete", "Incomplete"], Enum.GetNames<DataCoverage>());
        Assert.Equal(["Satisfied", "Missing", "Invalid", "AwaitingReview", "NotApplicable"], Enum.GetNames<CompletenessItemStatus>());
        Assert.Equal(["MasterData", "Applicability", "Product", "Quantity", "Production", "Parties", "Evidence"], Enum.GetNames<CompletenessGroup>());
        string[] forbidden = ["Compliant", "LegallyCompliant", "SafeToTrade", "DeforestationFree", "Reviewed"];
        foreach (var type in new[] { typeof(CompletenessItemStatus), typeof(CompletenessGroup), typeof(DataCoverage), typeof(EvidenceReadiness), typeof(ReviewFreshness), typeof(ApplicabilityStatus) })
            Assert.Empty(Enum.GetNames(type).Intersect(forbidden));
    }

    [Fact]
    public void Policy_v1_is_explicit_and_states_its_limitations()
    {
        Assert.Equal("provena-pilot-completeness", CompletenessPolicy.Id); Assert.Equal("1", CompletenessPolicy.Version);
        var limitations = string.Join(' ', CompletenessPolicy.Limitations);
        foreach (var expected in new[] { "Coordinate precision is not evaluated", "human judgement", "EORI", "no unit conversion", "stay unsupported" }) Assert.Contains(expected, limitations);
    }

    // ---- advisories ------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public void Advisories_report_catalogue_drift_and_superseded_pinned_revisions_without_affecting_completeness()
    {
        var f = Complete() with { Drift = [new(MasterDataArea.Supplier, ["email"]), new(MasterDataArea.Operator, ["legalName", "eori"])] };
        f.Contributions[0].Links[0].Revision.SupersededAtUtc = At;
        var advisories = CompletenessService.Advisories(f, caseClosed: false);
        Assert.Equal(["CATALOGUE_DRIFT_SUPPLIER", "CATALOGUE_DRIFT_OPERATOR", "PINNED_REVISION_SUPERSEDED"], advisories.Select(a => a.Code));
        Assert.Contains("email", advisories[0].Message); var pinned = advisories[2]; Assert.Equal(RevisionId, pinned.SubjectId);
        Assert.DoesNotContain("closed", advisories[0].Message); Assert.Contains("after case closure", CompletenessService.Advisories(f, caseClosed: true)[0].Message);
        var result = CompletenessEvaluator.Evaluate(f);
        Assert.Equal(DataCoverage.Complete, result.DataCoverage); Assert.Equal(EvidenceReadiness.ReadyForOwnerReview, result.EvidenceReadiness);
        Assert.Empty(CompletenessService.Advisories(Complete(), false));
    }

    // ---- fingerprint ------------------------------------------------------------------------------------------------------------------------
    private static string Fp(DossierFacts f) => DossierFingerprint.Compute(f).Fingerprint;
    private static string Section(DossierFacts f, string name) => DossierFingerprint.Compute(f).SectionHashes[name];

    [Fact]
    public void Fingerprint_is_deterministic_lower_case_sha256_hex_with_all_sections()
    {
        var a = DossierFingerprint.Compute(Complete()); var b = DossierFingerprint.Compute(Complete());
        Assert.Equal(a.Fingerprint, b.Fingerprint); Assert.Equal(a.SectionHashes.OrderBy(x => x.Key), b.SectionHashes.OrderBy(x => x.Key));
        Assert.Matches("^[0-9a-f]{64}$", a.Fingerprint); Assert.Equal(DossierFingerprint.Sections.Order(), a.SectionHashes.Keys.Order());
        Assert.All(a.SectionHashes.Values, h => Assert.Matches("^[0-9a-f]{64}$", h)); Assert.Equal(1, a.Version);
        Assert.Equal(["Shipment", "MasterData", "Applicability", "Recipients", "Lot", "Requirements", "Evidence"], DossierFingerprint.Sections);
    }

    [Fact]
    public void Fingerprint_is_independent_of_collection_order()
    {
        var f = Complete(); var g = Complete();
        g = g with { Documents = g.Documents.AsEnumerable().Reverse().ToList(), EvidenceLinks = g.EvidenceLinks.AsEnumerable().Reverse().ToList(), Requirements = g.Requirements.AsEnumerable().Reverse().ToList() };
        Assert.Equal(Fp(f), Fp(g));
        var h = Complete(); var second = new CaseRecipient { Id = Fixed(50), CaseRecipientStatementId = h.Recipients!.Id, Name = "Zed", PostalAddress = "Z 1", Email = "z@example.test" };
        var one = h with { RecipientEntries = [h.RecipientEntries[0], second] }; var two = h with { RecipientEntries = [second, h.RecipientEntries[0]] };
        Assert.Equal(Fp(one), Fp(two)); Assert.NotEqual(Fp(f), Fp(one));
        // Two contributions in either order.
        var c2 = new LotContribution { Id = Fixed(51), ProducerId = Guid.Parse("00000000-0000-0000-0000-0000000000c2"), Quantity = 1m, Unit = QuantityUnit.Kg };
        var x = Complete(); var y = Complete(); x = x with { Contributions = [x.Contributions[0], new(c2, [])] }; y = y with { Contributions = [new(c2, []), y.Contributions[0]] };
        Assert.Equal(Fp(x), Fp(y));
    }

    [Fact]
    public void Fingerprint_normalizes_decimals_by_value()
    {
        var a = Complete(); var b = Complete();
        a.Shipment.Quantity = 1.5m; b.Shipment.Quantity = 1.500000m; Assert.Equal(Fp(a), Fp(b));
        a.Lot!.Quantity = 10m; b.Lot!.Quantity = 10.000000m; a.Contributions[0].Contribution.Quantity = 10m; b.Contributions[0].Contribution.Quantity = 10.00m; Assert.Equal(Fp(a), Fp(b));
        a.Contributions[0].Links[0].Revision.AreaHectares = 2.5m; b.Contributions[0].Links[0].Revision.AreaHectares = 2.5000m; Assert.Equal(Fp(a), Fp(b));
        b.Shipment.Quantity = 1.500001m; Assert.NotEqual(Fp(a), Fp(b));
        Assert.Equal("1.5", DossierFingerprint.CanonicalDecimal(1.500000m)); Assert.Equal("10", DossierFingerprint.CanonicalDecimal(10.000m)); Assert.Equal("0", DossierFingerprint.CanonicalDecimal(0.000m)); Assert.Equal("-2.25", DossierFingerprint.CanonicalDecimal(-2.2500m));
    }

    [Fact]
    public void Fingerprint_ignores_the_master_data_version_id_and_the_context_fields()
    {
        var baseline = Fp(Complete());
        var otherVersion = Complete(); otherVersion.MasterData!.Id = Guid.NewGuid(); otherVersion.MasterData.Number = 7; otherVersion.MasterData.CreatedByUserId = Guid.NewGuid(); Assert.Equal(baseline, Fp(otherVersion));
        var context = Complete(); context.MasterData!.ProductName = "Renamed in catalogue"; context.MasterData.ProductCatalogueDescription = "Another description"; Assert.Equal(baseline, Fp(context));
        var drift = Complete() with { Drift = [new(MasterDataArea.Supplier, ["email"])] }; Assert.Equal(baseline, Fp(drift)); // live drift is advisory, never a fact
    }

    [Theory]
    [InlineData("operatorLegalName")] [InlineData("operatorPostalAddress")] [InlineData("operatorEmail")] [InlineData("operatorCountry")] [InlineData("operatorEori")]
    [InlineData("supplierId")] [InlineData("supplierName")] [InlineData("supplierAddress")] [InlineData("supplierEmail")] [InlineData("supplierCountry")]
    [InlineData("productId")] [InlineData("productCn")] [InlineData("productCommodity")]
    public void A_fact_bearing_master_data_change_changes_the_master_data_hash_and_the_fingerprint(string field)
    {
        var f = Complete(); var m = f.MasterData!;
        switch (field)
        {
            case "operatorLegalName": m.OperatorLegalName = "x"; break; case "operatorPostalAddress": m.OperatorPostalAddress = "x"; break; case "operatorEmail": m.OperatorEmail = "x@example.test"; break;
            case "operatorCountry": m.OperatorEstablishmentCountryCode = "DE"; break; case "operatorEori": m.OperatorEori = "DE1"; break;
            case "supplierId": m.SupplierId = Guid.NewGuid(); break; case "supplierName": m.SupplierName = "x"; break; case "supplierAddress": m.SupplierPostalAddress = "x"; break;
            case "supplierEmail": m.SupplierEmail = "x@example.test"; break; case "supplierCountry": m.SupplierCountryCode = "AR"; break;
            case "productId": m.ProductId = Guid.NewGuid(); break; case "productCn": m.ProductCnCode = "1801"; break; case "productCommodity": m.ProductCommodity = CommodityType.Cocoa; break;
        }
        var baseline = DossierFingerprint.Compute(Complete()); var changed = DossierFingerprint.Compute(f);
        Assert.NotEqual(baseline.SectionHashes["MasterData"], changed.SectionHashes["MasterData"]); Assert.NotEqual(baseline.Fingerprint, changed.Fingerprint);
        foreach (var section in DossierFingerprint.Sections.Where(s => s != "MasterData")) Assert.Equal(baseline.SectionHashes[section], changed.SectionHashes[section]);
    }

    [Fact]
    public void Requirement_review_provenance_alone_changes_the_requirements_hash()
    {
        var baseline = Section(Complete(), "Requirements");
        var by = Complete(); by.Requirements[0].ReviewedByUserId = Guid.NewGuid(); Assert.NotEqual(baseline, Section(by, "Requirements")); Assert.NotEqual(Fp(Complete()), Fp(by));
        var at = Complete(); at.Requirements[0].ReviewedAtUtc = At.AddMinutes(1); Assert.NotEqual(baseline, Section(at, "Requirements"));
        var note = Complete(); note.Requirements[0].ReviewerNote = "n"; Assert.NotEqual(baseline, Section(note, "Requirements"));
        var status = Complete(); status.Requirements[0].Status = RequirementStatus.Rejected; Assert.NotEqual(baseline, Section(status, "Requirements"));
        var required = Complete(); required.Requirements[0].IsRequired = false; Assert.NotEqual(baseline, Section(required, "Requirements"));
        Assert.Equal(Section(Complete(), "Lot"), Section(by, "Lot"));
    }

    [Fact]
    public void Labels_active_flags_and_revision_supersession_are_not_facts_but_the_pinned_revision_id_is()
    {
        var baseline = Fp(Complete());
        var superseded = Complete(); superseded.Contributions[0].Links[0].Revision.SupersededAtUtc = At; Assert.Equal(baseline, Fp(superseded));
        var pinned = Complete(); pinned.Contributions[0].Links[0].Link.PlotRevisionId = Guid.NewGuid(); Assert.NotEqual(baseline, Fp(pinned));
        var revisionId = Complete(); revisionId.Contributions[0].Links[0].Revision.Id = Guid.NewGuid(); revisionId.Contributions[0].Links[0].Link.PlotRevisionId = revisionId.Contributions[0].Links[0].Revision.Id; Assert.NotEqual(baseline, Fp(revisionId));
        var geometry = Complete(); geometry.Contributions[0].Links[0].Revision.GeometryJson = """{"type":"Polygon","x":1}"""; Assert.NotEqual(baseline, Fp(geometry));
        var period = Complete(); period.Contributions[0].Links[0].Link.ProductionEndDate = D2.AddDays(-1); Assert.NotEqual(baseline, Fp(period));
        Assert.NotEqual(Section(Complete(), "Lot"), Section(period, "Lot"));
        // Facts do not even carry producer or plot labels; the case status and timestamps of the case itself are not inputs either.
        Assert.DoesNotContain(typeof(DossierFacts).GetProperties(), p => p.Name.Contains("Producer") || p.Name.Contains("PlotName") || p.Name.Contains("Status"));
    }

    [Fact]
    public void Each_section_reacts_only_to_its_own_facts()
    {
        var baseline = DossierFingerprint.Compute(Complete());
        void Only(string section, DossierFacts changed)
        {
            var result = DossierFingerprint.Compute(changed); Assert.NotEqual(baseline.Fingerprint, result.Fingerprint);
            foreach (var s in DossierFingerprint.Sections) Assert.Equal(s == section, baseline.SectionHashes[s] != result.SectionHashes[s]);
        }
        var shipment = Complete(); shipment.Shipment.ReferenceNumber = "SHP-2"; Only("Shipment", shipment);
        var applicability = Complete(); applicability.Applicability!.OperatorRoleDescription = "x"; Only("Applicability", applicability);
        var recipients = Complete(); recipients.Recipients!.NotApplicableBasis = "x"; Only("Recipients", recipients);
        var lot = Complete(); lot.Lot!.Note = "x"; Only("Lot", lot);
        var evidence = Complete() with { EvidenceLinks = Complete().EvidenceLinks.Take(1).ToList() }; Only("Evidence", evidence);
        var requirements = Complete(); requirements.Requirements[2].ReviewerNote = "x"; Only("Requirements", requirements);
        var role = Complete(); role.Applicability!.OperatorRole = OperatorRole.OtherUnsupported; Only("Applicability", role);
        var assessed = Complete(); assessed.Applicability!.AssessedProductId = Guid.NewGuid(); Only("Applicability", assessed);
        Only("Evidence", Complete() with { Documents = Complete().Documents.Skip(1).ToList() });
    }

    [Fact]
    public void Every_supported_fingerprint_version_remains_computable_and_unknown_versions_are_rejected()
    {
        Assert.Contains(DossierFingerprint.CurrentVersion, DossierFingerprint.SupportedVersions);
        foreach (var version in DossierFingerprint.SupportedVersions)
        {
            var result = DossierFingerprint.Compute(Complete(), version);
            Assert.Equal(version, result.Version); Assert.Equal(result.Fingerprint, DossierFingerprint.Compute(Complete(), version).Fingerprint);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => DossierFingerprint.Compute(Complete(), 99));
        // Frozen reference value: the version-1 canonical form must never change silently (add a new fingerprint version instead).
        Assert.Equal("2cc06ed492c0914bcd43c7e0e779eb278077addf6ba90f87fbd9b792807df5a1", DossierFingerprint.Compute(Complete(), 1).Fingerprint);
    }
}
