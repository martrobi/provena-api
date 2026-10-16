using Provena.Api.Contracts;
namespace Provena.Api.Services;

public sealed record RiskCriterion(string Code, string Title, string Guidance);

// The versioned pilot risk-criteria catalogue (Risk Assessment & Mitigation v1). It is a structured PROMPT LIST for human judgement, grouped after the Article 10(2) themes summarised in
// docs/EUDR_GAP_ANALYSIS.md. It is NOT a legal compliance engine, claims no legal coverage of Article 10 and calculates nothing: every finding is entered by the Owner. A change to the
// criteria needs a new Version (assessments keep the id and version they were recorded under).
public static class RiskCriteriaCatalogue
{
    public const string Id = "provena-pilot-risk-criteria";
    public const string Version = "1";

    public static readonly IReadOnlyList<RiskCriterion> Criteria =
    [
        new("PRODUCTION_COUNTRY_AND_AREA", "Production country and area", "Country classification, presence of forest, and prevalence of deforestation or forest degradation in the countries and areas of production."),
        new("INDIGENOUS_PEOPLES_AND_LAND_RIGHTS", "Indigenous peoples and land rights", "Presence of indigenous peoples, consultation and cooperation, and substantiated land-tenure or land-use claims."),
        new("GOVERNANCE_AND_LEGALITY_CONTEXT", "Governance and legality context", "Corruption, document falsification, law enforcement, human rights, armed conflict or sanctions concerns in the production context."),
        new("EVIDENCE_RELIABILITY", "Reliability of the collected information", "Whether the collected information and documents are reliable and valid, beyond the presence checks of the completeness policy."),
        new("SUPPLY_CHAIN_COMPLEXITY_AND_MIXING", "Supply-chain complexity and mixing", "Complexity of the supply chain, risk of circumvention, and risk of mixing with products of unknown or unassessed origin."),
        new("CONCERNS_AND_PAST_NONCOMPLIANCE", "Concerns and past non-compliance", "Substantiated concerns and any past non-compliance of the supplier, the producers or others in the supply chain."),
        new("OTHER_RISK_INDICATORS", "Other risk indicators", "Any other relevant indicator, including certification or third-party verification information considered."),
    ];

    public static readonly IReadOnlySet<string> Codes = Criteria.Select(c => c.Code).ToHashSet(StringComparer.Ordinal);

    public static RiskCatalogueDto Dto() => new(Id, Version, Criteria.Select(c => new RiskCriterionDto(c.Code, c.Title, c.Guidance)).ToList());
}
