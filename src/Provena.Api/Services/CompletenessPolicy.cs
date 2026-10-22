using Provena.Api.Contracts;
namespace Provena.Api.Services;
// Explicit, code-defined completeness policy (ADR 0008). Not a rules engine: no stored or dynamic rule definitions. Changing a rule means changing
// CompletenessEvaluator and bumping Version.
public static class CompletenessPolicy
{
    public const string Id = "provena-pilot-completeness";
    public const string Version = "3";
    public static readonly IReadOnlyList<string> Limitations =
    [
        "Completeness states only whether the recorded facts satisfy this policy's checks. It is not a legal compliance, risk, deforestation-free or DDS-readiness determination.",
        "Coordinate precision is not evaluated; a structurally valid geometry is not proof of geographic truth.",
        "Sufficiency of legality and deforestation-free evidence is human judgement; linked documents establish presence only.",
        "EORI numbers are stored but not evaluated.",
        "Quantities are compared by exact decimal value with no unit conversion and no tolerance, under a pilot single-lot assumption (one shipment/case is represented by one current case lot; this is not a multi-lot rule): the shipment against the current case lot, every existing supplier-confirmed quantity against the shipment (it has no unit of its own and is taken to be in the shipment unit), and producer contributions against the lot. A missing supplier-confirmed quantity is waived only when the quantity confirmation requirement is NotRequired with a recorded note. This is not a legal net-mass finding and a multi-lot or split flow is not modeled.",
        "Source Quantity Allocation (policy v3) is optional quantity attribution of the lot to production plots, in the lot unit by exact decimal equality with no conversion, tolerance or percentage. Not recorded is not applicable; once any plot allocation is recorded it must reconcile exactly. It is not a legal net-mass, yield, country-risk or compliance finding.",
        "Unsupported flows (Wood, Cattle or Other commodities, Export, non-single-commodity flows, roles other than Operator) stay unsupported; they are never reported as complete.",
        "Production periods are checked for presence only, not against any legal date window.",
        "A CN code is checked for format only; it does not establish regulatory scope.",
        "An Accepted requirement is a requirement-level decision, not a review of the dossier.",
    ];
    public static CompletenessPolicyDto Dto() => new(Id, Version, Limitations);
}
