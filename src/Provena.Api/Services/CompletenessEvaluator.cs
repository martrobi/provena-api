using System.Text.RegularExpressions;
using Provena.Api.Contracts;
using Provena.Api.Domain;
namespace Provena.Api.Services;
public sealed record CompletenessEvaluation(IReadOnlyList<CompletenessItemDto> Items, ApplicabilityStatus ApplicabilityStatus, DataCoverage DataCoverage, EvidenceReadiness EvidenceReadiness, QuantityReconciliationResultDto QuantityReconciliation);
// Pure evaluation of policy v3 (ADR 0008; v2 adds Quantity Reconciliation v1, v3 adds Source Quantity Allocation v1) over DossierFacts. Every item states exactly what was checked. Nothing here is a legal-compliance, safety or
// deforestation-free verdict: Satisfied means "this recorded fact satisfies this check". Item order is deterministic.
public static partial class CompletenessEvaluator
{
    [GeneratedRegex("^[0-9]{4,10}$")] private static partial Regex CnFormat();
    private const decimal PointAreaLimitHectares = 4m;

    private static CompletenessItemDto Item(string code, CompletenessGroup group, CompletenessItemStatus status, string reason, string? subjectType = null, Guid? subjectId = null) =>
        new(code, group, subjectType, subjectId, status, reason);

    public static CompletenessEvaluation Evaluate(DossierFacts facts)
    {
        var items = new List<CompletenessItemDto>();
        var applicabilityStatus = ApplicabilityDerivation.Derive(facts.Applicability, facts.MasterData);
        MasterData(facts, items); Applicability(facts, applicabilityStatus, items); Product(facts, items); var quantity = QuantityReconciliation.Evaluate(facts); Quantity(facts, quantity, items);
        Production(facts, items); Parties(facts, items); Evidence(facts, items);

        // Axis 1: the data groups only; EVIDENCE items never decide it.
        var coverage = items.Where(i => i.Group != CompletenessGroup.Evidence).Any(i => i.Status is CompletenessItemStatus.Missing or CompletenessItemStatus.Invalid) ? DataCoverage.Incomplete : DataCoverage.Complete;
        // Axis 2: there is no "Reviewed" outcome.
        var evidence = items.Where(i => i.Group == CompletenessGroup.Evidence).ToList();
        var readiness = evidence.Any(i => i.Status is CompletenessItemStatus.Missing or CompletenessItemStatus.Invalid) ? EvidenceReadiness.EvidenceMissing
            : evidence.Any(i => i.Status == CompletenessItemStatus.AwaitingReview) ? EvidenceReadiness.AwaitingRequirementDecisions : EvidenceReadiness.ReadyForOwnerReview;
        return new(items, applicabilityStatus, coverage, readiness, quantity);
    }

    private static void MasterData(DossierFacts f, List<CompletenessItemDto> items)
    {
        const string code = "MASTER_DATA_SHIPMENT_MATCH"; const CompletenessGroup g = CompletenessGroup.MasterData;
        if (f.MasterData == null) { items.Add(Item(code, g, CompletenessItemStatus.Missing, "No case master data copy exists; refresh the case master data.")); return; }
        var supplier = f.MasterData.SupplierId != f.Shipment.SupplierId; var product = f.MasterData.ProductId != f.Shipment.ProductId;
        if (supplier || product)
            items.Add(Item(code, g, CompletenessItemStatus.Invalid, $"The shipment's {(supplier && product ? "supplier and product" : supplier ? "supplier" : "product")} changed since the case master data was copied; refresh the case master data."));
        else items.Add(Item(code, g, CompletenessItemStatus.Satisfied, "The case master data was copied for the shipment's current supplier and product."));
    }

    private static void Applicability(DossierFacts f, ApplicabilityStatus status, List<CompletenessItemDto> items)
    {
        const CompletenessGroup g = CompletenessGroup.Applicability; var a = f.Applicability;
        if (a == null)
        {
            items.Add(Item("APPLICABILITY_RECORDED", g, CompletenessItemStatus.Missing, "No applicability assessment has been recorded."));
            foreach (var code in new[] { "APPLICABILITY_DECIDED", "APPLICABILITY_PRODUCT_MATCH", "APPLICABILITY_IN_SCOPE", "FLOW_SUPPORTED" })
                items.Add(Item(code, g, CompletenessItemStatus.Missing, "Cannot be evaluated: no applicability assessment has been recorded."));
            return;
        }
        var scopePending = a.RegulatoryScope == RegulatoryScope.Pending;
        var legalReview = a.RegulatoryScope == RegulatoryScope.RequiresLegalReview;
        var flowPending = a.RegulatoryScope == RegulatoryScope.InScope && a.FlowSupport == FlowSupport.Pending;
        items.Add(Item("APPLICABILITY_RECORDED", g, CompletenessItemStatus.Satisfied, $"Applicability assessment version {a.Number} is recorded.", "CaseApplicability", a.Id));
        items.Add(scopePending ? Item("APPLICABILITY_DECIDED", g, CompletenessItemStatus.Missing, "The regulatory scope decision is still Pending.")
            : legalReview ? Item("APPLICABILITY_DECIDED", g, CompletenessItemStatus.Missing, "The Owner determined that the regulatory scope requires legal review before it can be decided.")
            : flowPending ? Item("APPLICABILITY_DECIDED", g, CompletenessItemStatus.Missing, "The regulatory scope is InScope but flow support is still Pending.")
            : Item("APPLICABILITY_DECIDED", g, CompletenessItemStatus.Satisfied, "The regulatory scope decision" + (a.RegulatoryScope == RegulatoryScope.InScope ? " and flow support are" : " is") + " recorded."));
        items.Add(status == ApplicabilityStatus.ProductChanged
            ? Item("APPLICABILITY_PRODUCT_MATCH", g, CompletenessItemStatus.Invalid, "The case master data now describes a different product (id, CN code or commodity) than the one assessed; record the applicability again.")
            : Item("APPLICABILITY_PRODUCT_MATCH", g, CompletenessItemStatus.Satisfied, "The assessed product (id, CN code and commodity) equals the product in the case master data."));
        items.Add(a.RegulatoryScope switch
        {
            RegulatoryScope.Pending => Item("APPLICABILITY_IN_SCOPE", g, CompletenessItemStatus.Missing, "The regulatory scope decision is still Pending."),
            RegulatoryScope.RequiresLegalReview => Item("APPLICABILITY_IN_SCOPE", g, CompletenessItemStatus.Missing, "The regulatory scope requires legal review and is not decided."),
            RegulatoryScope.InScope => Item("APPLICABILITY_IN_SCOPE", g, CompletenessItemStatus.Satisfied, "The recorded decision places the case in scope."),
            _ => Item("APPLICABILITY_IN_SCOPE", g, CompletenessItemStatus.Invalid, "The recorded decision places the case outside this policy's scope; this is not a finding of non-compliance."),
        });
        if (a.RegulatoryScope == RegulatoryScope.OutsideRegulatoryScope)
            items.Add(Item("FLOW_SUPPORTED", g, CompletenessItemStatus.NotApplicable, "Not applicable: the recorded applicability decision places the case outside the regulatory scope."));
        else if (scopePending || legalReview || flowPending)
            items.Add(Item("FLOW_SUPPORTED", g, CompletenessItemStatus.Missing, "Flow support is not decided yet."));
        else if (a.FlowSupport == FlowSupport.UnsupportedFlow || ApplicabilityDerivation.EnvelopeRejects(a))
            items.Add(Item("FLOW_SUPPORTED", g, CompletenessItemStatus.Invalid, "The flow is unsupported by pilot policy v1: " + UnsupportedCauses(a) + "."));
        else items.Add(Item("FLOW_SUPPORTED", g, CompletenessItemStatus.Satisfied, "The flow is within the pilot policy v1 supported envelope."));
    }

    private static string UnsupportedCauses(CaseApplicability a)
    {
        var causes = new List<string>();
        if (a.FlowSupport == FlowSupport.UnsupportedFlow) causes.Add("recorded as an unsupported flow");
        if (a.AssessedCommodity is CommodityType.Wood or CommodityType.Cattle or CommodityType.Other) causes.Add($"commodity {a.AssessedCommodity}");
        if (a.ActivityType == ApplicabilityActivityType.Export) causes.Add("export activity");
        if (!a.SingleRelevantCommodity) causes.Add("not a single relevant commodity");
        if (a.OperatorRole == OperatorRole.OtherUnsupported) causes.Add("operator role other than Operator");
        return string.Join("; ", causes);
    }

    private static void Product(DossierFacts f, List<CompletenessItemDto> items)
    {
        const CompletenessGroup g = CompletenessGroup.Product;
        // The authoritative assessed description is the applicability's; the shared catalogue Product.Description is never evaluated.
        items.Add(string.IsNullOrWhiteSpace(f.Applicability?.ProductDescription)
            ? Item("PRODUCT_DESCRIPTION", g, CompletenessItemStatus.Missing, f.Applicability == null ? "No applicability is recorded, so there is no assessed product description." : "The assessed product description on the applicability is blank.")
            : Item("PRODUCT_DESCRIPTION", g, CompletenessItemStatus.Satisfied, "The applicability records an assessed product description."));
        if (f.MasterData == null) items.Add(Item("PRODUCT_CN_CODE", g, CompletenessItemStatus.Missing, "No case master data copy exists, so there is no CN code to check."));
        else if (CnFormat().IsMatch(f.MasterData.ProductCnCode)) items.Add(Item("PRODUCT_CN_CODE", g, CompletenessItemStatus.Satisfied, "The CN code has a valid format (4 to 10 digits); this confirms format only, not regulatory scope."));
        else items.Add(Item("PRODUCT_CN_CODE", g, CompletenessItemStatus.Invalid, "The CN code does not have a valid format (4 to 10 digits); this checks format only, not regulatory scope."));
    }

    // Policy v2 (Quantity Reconciliation v1): every comparison is exact decimal equality in one unit, with no conversion and no tolerance. The checks are derived by the pure
    // QuantityReconciliation; the shipment == current lot rule is a pilot single-lot assumption (one shipment/case has one current CaseLot), not a permanent multi-lot rule.
    private static void Quantity(DossierFacts f, QuantityReconciliationResultDto q, List<CompletenessItemDto> items)
    {
        const CompletenessGroup g = CompletenessGroup.Quantity;
        items.Add(f.Lot == null ? Item("LOT_PRESENT", g, CompletenessItemStatus.Missing, "No case lot has been recorded.")
            : Item("LOT_PRESENT", g, CompletenessItemStatus.Satisfied, $"Case lot version {f.Lot.Number} is recorded.", "CaseLot", f.Lot.Id));
        items.Add(QuantityItem("SHIPMENT_LOT_QUANTITY_MATCH", q, QuantityReconciliation.ShipmentLot, "CaseLot", f.Lot?.Id));
        items.Add(QuantityItem("SUPPLIER_CONFIRMED_QUANTITY_MATCH", q, QuantityReconciliation.SupplierConfirmedShipment, "SupplierSubmission", f.Submission?.Id));
        items.Add(QuantityItem("LOT_RECONCILED", q, QuantityReconciliation.ContributionsLot, "CaseLot", f.Lot?.Id));
        items.Add(SourceAllocationItem(f));
    }

    // Policy v3: allocation of the lot to plots is optional. Not recorded (or no lot) is NotApplicable; once recorded it must reconcile exactly (MissingData -> Missing, Under/Over -> Invalid).
    private static CompletenessItemDto SourceAllocationItem(DossierFacts f)
    {
        const CompletenessGroup g = CompletenessGroup.Quantity;
        var allocation = SourceAllocation.FromFacts(f);
        if (allocation == null) return Item(SourceAllocation.ItemCode, g, CompletenessItemStatus.NotApplicable, "Not applicable: no case lot has been recorded.");
        var reason = allocation.Status switch
        {
            SourceAllocationStatus.NotRecorded => "Not applicable: no plot allocation has been recorded for the lot.",
            SourceAllocationStatus.Matched => $"Plot allocations ({Num(allocation.AllocatedQuantity)} {allocation.Unit}) equal the lot quantity exactly and every producer contribution is fully allocated.",
            SourceAllocationStatus.MissingData => "Plot allocations are recorded but a producer contribution has no allocated plot link; every contribution must be allocated to its plots.",
            SourceAllocationStatus.UnderAllocated => $"Plot allocations ({Num(allocation.AllocatedQuantity)} {allocation.Unit}) do not cover the lot quantity ({Num(allocation.LotQuantity)} {allocation.Unit}) and its producer contributions; unallocated {Num(allocation.UnallocatedQuantity)} {allocation.Unit}.",
            _ => $"Plot allocations ({Num(allocation.AllocatedQuantity)} {allocation.Unit}) exceed the lot quantity or a producer contribution.",
        };
        return Item(SourceAllocation.ItemCode, g, SourceAllocation.ItemStatus(allocation.Status), reason, "CaseLot", f.Lot!.Id);
    }

    // MissingData -> Missing; UnitMismatch and QuantityMismatch -> Invalid; the structured detail lives in the reconciliation result.
    private static CompletenessItemDto QuantityItem(string code, QuantityReconciliationResultDto q, string checkCode, string subjectType, Guid? subjectId)
    {
        var check = q.Checks.Single(c => c.Code == checkCode);
        var status = check.Status switch
        {
            QuantityCheckStatus.Matched => CompletenessItemStatus.Satisfied,
            QuantityCheckStatus.NotApplicable => CompletenessItemStatus.NotApplicable,
            QuantityCheckStatus.MissingData => CompletenessItemStatus.Missing,
            _ => CompletenessItemStatus.Invalid,
        };
        return subjectId == null ? Item(code, CompletenessGroup.Quantity, status, check.Reason) : Item(code, CompletenessGroup.Quantity, status, check.Reason, subjectType, subjectId);
    }

    private static void Production(DossierFacts f, List<CompletenessItemDto> items)
    {
        const CompletenessGroup g = CompletenessGroup.Production;
        foreach (var c in f.Contributions)
            items.Add(c.Links.Count == 0
                ? Item("SOURCE_HAS_PLOT", g, CompletenessItemStatus.Missing, "The producer contribution has no production plot linked.", "LotContribution", c.Contribution.Id)
                : Item("SOURCE_HAS_PLOT", g, CompletenessItemStatus.Satisfied, $"The producer contribution links {c.Links.Count} production plot(s).", "LotContribution", c.Contribution.Id));
        foreach (var l in f.Contributions.SelectMany(c => c.Links))
            items.Add(l.Link.ProductionStartDate.HasValue && l.Link.ProductionEndDate.HasValue
                ? Item("SOURCE_PRODUCTION_PERIOD", g, CompletenessItemStatus.Satisfied, "Both production period dates are recorded (presence only; no date-window check).", "ContributionPlot", l.Link.Id)
                : Item("SOURCE_PRODUCTION_PERIOD", g, CompletenessItemStatus.Missing, "Both production start and end dates are required for every pinned source.", "ContributionPlot", l.Link.Id));
        // Declared area only: polygon area is never calculated, and coordinate precision is not evaluated.
        foreach (var r in f.Contributions.SelectMany(c => c.Links).Select(l => l.Revision).DistinctBy(r => r.Id).OrderBy(r => r.Id))
            items.Add(r.GeometryType switch
            {
                PlotGeometryType.Polygon or PlotGeometryType.MultiPolygon => Item("SOURCE_GEOLOCATION_FORM", g, CompletenessItemStatus.Satisfied, $"The pinned plot revision has a {r.GeometryType} geometry (form check only; not proof of geographic truth).", "PlotRevision", r.Id),
                _ when r.AreaHectares == null => Item("SOURCE_GEOLOCATION_FORM", g, CompletenessItemStatus.Missing, "The pinned plot revision is a Point and has no declared area; a point needs a declared area of at most 4 ha.", "PlotRevision", r.Id),
                _ when r.AreaHectares > PointAreaLimitHectares => Item("SOURCE_GEOLOCATION_FORM", g, CompletenessItemStatus.Invalid, $"The pinned plot revision is a Point with a declared area of {Num(r.AreaHectares.Value)} ha, above 4 ha; a polygon is required.", "PlotRevision", r.Id),
                _ => Item("SOURCE_GEOLOCATION_FORM", g, CompletenessItemStatus.Satisfied, $"The pinned plot revision is a Point with a declared area of {Num(r.AreaHectares.Value)} ha (at most 4 ha; form check only).", "PlotRevision", r.Id),
            });
    }

    private static void Parties(DossierFacts f, List<CompletenessItemDto> items)
    {
        const CompletenessGroup g = CompletenessGroup.Parties; var m = f.MasterData;
        if (m == null)
        {
            items.Add(Item("PARTY_OPERATOR", g, CompletenessItemStatus.Missing, "No case master data copy exists, so the operator facts are unknown."));
            foreach (var code in new[] { "PARTY_SUPPLIER_NAME", "PARTY_SUPPLIER_POSTAL_ADDRESS", "PARTY_SUPPLIER_EMAIL" }) items.Add(Item(code, g, CompletenessItemStatus.Missing, "No case master data copy exists, so the supplier facts are unknown."));
        }
        else
        {
            var blank = new List<string>();
            if (string.IsNullOrWhiteSpace(m.OperatorLegalName)) blank.Add("legal name");
            if (string.IsNullOrWhiteSpace(m.OperatorPostalAddress)) blank.Add("postal address");
            if (string.IsNullOrWhiteSpace(m.OperatorEmail)) blank.Add("email");
            if (string.IsNullOrWhiteSpace(m.OperatorEstablishmentCountryCode)) blank.Add("establishment country");
            items.Add(blank.Count == 0 ? Item("PARTY_OPERATOR", g, CompletenessItemStatus.Satisfied, "The case master data has the operator's legal name, postal address, email and establishment country (EORI is not evaluated).")
                : Item("PARTY_OPERATOR", g, CompletenessItemStatus.Missing, "The case master data lacks the operator's " + string.Join(", ", blank) + "."));
            items.Add(string.IsNullOrWhiteSpace(m.SupplierName) ? Item("PARTY_SUPPLIER_NAME", g, CompletenessItemStatus.Missing, "The case master data has no supplier name.") : Item("PARTY_SUPPLIER_NAME", g, CompletenessItemStatus.Satisfied, "The case master data has the supplier name."));
            items.Add(string.IsNullOrWhiteSpace(m.SupplierPostalAddress) ? Item("PARTY_SUPPLIER_POSTAL_ADDRESS", g, CompletenessItemStatus.Missing, "The case master data has no supplier postal address.") : Item("PARTY_SUPPLIER_POSTAL_ADDRESS", g, CompletenessItemStatus.Satisfied, "The case master data has the supplier postal address."));
            items.Add(string.IsNullOrWhiteSpace(m.SupplierEmail) ? Item("PARTY_SUPPLIER_EMAIL", g, CompletenessItemStatus.Missing, "The case master data has no supplier email.") : Item("PARTY_SUPPLIER_EMAIL", g, CompletenessItemStatus.Satisfied, "The case master data has the supplier email."));
        }
        var s = f.Recipients;
        if (s == null) items.Add(Item("PARTY_RECIPIENTS", g, CompletenessItemStatus.Missing, "Recipients are unresolved: no recipient statement has been recorded."));
        else if (s.Resolution == RecipientResolution.NotApplicable)
            items.Add(string.IsNullOrWhiteSpace(s.NotApplicableBasis) ? Item("PARTY_RECIPIENTS", g, CompletenessItemStatus.Invalid, "The recipient statement is NotApplicable without a recorded basis.", "CaseRecipientStatement", s.Id)
                : Item("PARTY_RECIPIENTS", g, CompletenessItemStatus.NotApplicable, "Not applicable: the recipient statement records a basis.", "CaseRecipientStatement", s.Id));
        else if (f.RecipientEntries.Count is >= 1 and <= 50 && f.RecipientEntries.All(r => !string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(r.PostalAddress) && !string.IsNullOrWhiteSpace(r.Email)))
            items.Add(Item("PARTY_RECIPIENTS", g, CompletenessItemStatus.Satisfied, $"{f.RecipientEntries.Count} recipient(s) are identified with name, postal address and email.", "CaseRecipientStatement", s.Id));
        else items.Add(Item("PARTY_RECIPIENTS", g, CompletenessItemStatus.Invalid, "The recipient statement lists no recipients or incomplete recipients.", "CaseRecipientStatement", s.Id));
    }

    private static void Evidence(DossierFacts f, List<CompletenessItemDto> items)
    {
        const CompletenessGroup g = CompletenessGroup.Evidence;
        foreach (var r in f.Requirements.OrderBy(x => x.Type))
        {
            var code = "REQUIREMENT_" + r.Type;
            items.Add(r.Status switch
            {
                RequirementStatus.Accepted => Item(code, g, CompletenessItemStatus.Satisfied, "Accepted: a requirement-level review decision, not a review of the dossier.", "Requirement", r.Id),
                RequirementStatus.Submitted => Item(code, g, CompletenessItemStatus.AwaitingReview, "Submitted evidence awaits a requirement-level decision.", "Requirement", r.Id),
                RequirementStatus.Rejected => Item(code, g, CompletenessItemStatus.Invalid, "The requirement evidence was rejected.", "Requirement", r.Id),
                RequirementStatus.NotRequired when !string.IsNullOrWhiteSpace(r.ReviewerNote) => Item(code, g, CompletenessItemStatus.NotApplicable, "Not applicable: marked NotRequired with a recorded note.", "Requirement", r.Id),
                RequirementStatus.NotRequired => Item(code, g, CompletenessItemStatus.Missing, "Marked NotRequired without a note; a recorded basis is needed to count as not applicable.", "Requirement", r.Id),
                _ => Item(code, g, CompletenessItemStatus.Missing, "No evidence has been provided for the requirement.", "Requirement", r.Id),
            });
        }
        const string presenceOnly = "presence only; not reviewed and not a sufficiency finding";
        foreach (var (code, purpose, label) in new[] { ("EVIDENCE_LEGALITY_PRESENT", EvidencePurpose.Legality, "legality"), ("EVIDENCE_DEFORESTATION_FREE_PRESENT", EvidencePurpose.DeforestationFree, "deforestation-free") })
        {
            // A link counts only while it is not withdrawn AND its document is itself current (a withdrawn document is not in f.Documents).
            var count = f.EvidenceLinks.Count(l => l.Purpose == purpose && l.WithdrawnAtUtc == null && f.Documents.Any(d => d.Id == l.CaseDocumentId && d.WithdrawnAtUtc == null));
            items.Add(count > 0 ? Item(code, g, CompletenessItemStatus.Satisfied, $"{count} current document(s) are linked for {label} evidence ({presenceOnly}).")
                : Item(code, g, CompletenessItemStatus.Missing, $"No current document is linked for {label} evidence ({presenceOnly})."));
        }
    }

    // Exact value without trailing zeros, culture-independent.
    private static string Num(decimal value) => DossierFingerprint.CanonicalDecimal(value);
}
