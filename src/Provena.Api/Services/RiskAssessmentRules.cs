using Provena.Api.Domain;
namespace Provena.Api.Services;

public sealed record RiskFindingEntry(string? Code, RiskCriterionFinding? Finding, string? Note);
public sealed record RiskRuleError(string Property, string Message);

// Pure shape rules of a risk assessment (the finding set against the catalogue, the note rules and the one self-contradiction). They derive NOTHING: no rule computes the conclusion from the
// findings or from mitigation, and none claims mitigation is sufficient. A Concern finding is a human-observed indicator, not an automatic workflow requirement, so Concern + Negligible is
// valid (the Owner stays accountable through the findings and the rationale). Messages never echo client-supplied values (codes or notes). Applied on the trimmed input, with blank notes as null.
public static class RiskAssessmentRules
{
    public const int MaxNoteLength = 2000;

    public static IReadOnlyList<RiskRuleError> Check(RiskConclusion conclusion, IReadOnlyList<RiskFindingEntry>? findings)
    {
        var errors = new List<RiskRuleError>();
        if (findings == null || findings.Count == 0) return [new("Findings", "findings must list a finding for every criterion of the catalogue.")];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < findings.Count; i++)
        {
            var f = findings[i]; var at = $"Findings[{i}]";
            if (string.IsNullOrWhiteSpace(f.Code)) errors.Add(new($"{at}.CriterionCode", "criterionCode is required."));
            else if (!RiskCriteriaCatalogue.Codes.Contains(f.Code)) errors.Add(new($"{at}.CriterionCode", "criterionCode is not a criterion of the current catalogue."));
            else if (!seen.Add(f.Code)) errors.Add(new($"{at}.CriterionCode", "criterionCode appears more than once."));
            if (f.Finding is not { } finding || !Enum.IsDefined(finding)) { errors.Add(new($"{at}.Finding", "finding is required and must be NoConcern, Concern or InsufficientInformation.")); continue; }
            if (f.Note != null && f.Note.Length > MaxNoteLength) errors.Add(new($"{at}.Note", $"note must be at most {MaxNoteLength} characters."));
            if (finding != RiskCriterionFinding.NoConcern && string.IsNullOrWhiteSpace(f.Note)) errors.Add(new($"{at}.Note", $"A {finding} finding requires a note."));
        }
        var missing = RiskCriteriaCatalogue.Criteria.Count(c => !seen.Contains(c.Code));
        if (missing > 0 && errors.Count == 0) errors.Add(new("Findings", $"findings must list every criterion of the catalogue exactly once ({missing} missing)."));
        // Self-contradictory only: "not enough information to assess" cannot also be "negligible risk". This is not a sufficiency algorithm.
        if (conclusion == RiskConclusion.Negligible && findings.Any(f => f.Finding == RiskCriterionFinding.InsufficientInformation))
            errors.Add(new("Conclusion", "A Negligible conclusion cannot be recorded while a criterion is marked InsufficientInformation."));
        return errors;
    }
}
