using Provena.Api.Contracts;
namespace Provena.Api.Services;
// Explicit, code-defined completeness policy (ADR 0008). Not a rules engine: no stored or dynamic rule definitions. Changing a rule means changing
// CompletenessEvaluator and bumping Version.
public static class CompletenessPolicy
{
    public const string Id = "provena-pilot-completeness";
    public const string Version = "1";
    public static readonly IReadOnlyList<string> Limitations =
    [
        "Completeness states only whether the recorded facts satisfy this policy's checks. It is not a legal compliance, risk, deforestation-free or DDS-readiness determination.",
        "Coordinate precision is not evaluated; a structurally valid geometry is not proof of geographic truth.",
        "Sufficiency of legality and deforestation-free evidence is human judgement; linked documents establish presence only.",
        "EORI numbers are stored but not evaluated.",
        "Quantities are compared by exact value in the lot's own unit: no unit conversion, no tolerance, no comparison with the shipment or supplier-confirmed quantity.",
        "Unsupported flows (Wood, Cattle or Other commodities, Export, non-single-commodity flows, roles other than Operator) stay unsupported; they are never reported as complete.",
        "Production periods are checked for presence only, not against any legal date window.",
        "A CN code is checked for format only; it does not establish regulatory scope.",
        "An Accepted requirement is a requirement-level decision, not a review of the dossier.",
    ];
    public static CompletenessPolicyDto Dto() => new(Id, Version, Limitations);
}
