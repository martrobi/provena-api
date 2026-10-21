using Provena.Api.Domain;
namespace Provena.Api.Services;
// Pure, testable derivation of the applicability status (ADR 0008). Deliberately NOT a rules engine and not a tariff/Annex I interpreter:
// CN code format never implies applicability. Policy v1 flow envelope: Wood, Cattle and Other commodities, Export, a non-single-commodity
// flow and a non-Operator role are outside the supported pilot flow. The commodity is the ASSESSED commodity recorded on the applicability.
public static class ApplicabilityDerivation
{
    // Evaluated in this order; the first matching rule wins.
    public static ApplicabilityStatus Derive(CaseApplicability? applicability, CaseMasterDataVersion? currentMasterData)
    {
        if (applicability == null) return ApplicabilityStatus.NotRecorded;
        if (ProductChanged(applicability, currentMasterData)) return ApplicabilityStatus.ProductChanged;
        switch (applicability.RegulatoryScope)
        {
            case RegulatoryScope.RequiresLegalReview: return ApplicabilityStatus.RequiresLegalReview; // FlowSupport is ignored here.
            case RegulatoryScope.Pending: return ApplicabilityStatus.Pending;
            case RegulatoryScope.OutsideRegulatoryScope: return ApplicabilityStatus.OutsideRegulatoryScope; // FlowSupport is ignored here.
        }
        if (applicability.FlowSupport == FlowSupport.Pending) return ApplicabilityStatus.Pending;
        if (applicability.FlowSupport == FlowSupport.UnsupportedFlow || EnvelopeRejects(applicability)) return ApplicabilityStatus.UnsupportedFlow;
        return ApplicabilityStatus.WithinSupportedScope;
    }

    // Compares two case-owned values (never the live catalogue): the assessed product identity versus the case's current master-data copy.
    // A supplier-only or operator-only refresh does not change them; a different Product id with the same CN code and commodity does.
    public static bool ProductChanged(CaseApplicability applicability, CaseMasterDataVersion? currentMasterData) =>
        currentMasterData == null || applicability.AssessedProductId != currentMasterData.ProductId
        || applicability.AssessedCnCode != currentMasterData.ProductCnCode || applicability.AssessedCommodity != currentMasterData.ProductCommodity;

    public static bool EnvelopeRejects(CaseApplicability a) =>
        a.AssessedCommodity is CommodityType.Wood or CommodityType.Cattle or CommodityType.Other
        || a.ActivityType == ApplicabilityActivityType.Export || !a.SingleRelevantCommodity || a.OperatorRole == OperatorRole.OtherUnsupported;
}
