using Provena.Api.Contracts;
using Provena.Api.Domain;
using Provena.Api.Services;
using Xunit;

namespace Provena.Api.UnitTests;

// Risk Assessment & Mitigation v1: the criteria catalogue, the pure finding rules, the input validators and the pure close gate. None of this derives a conclusion from findings or mitigation.
public sealed class RiskDomainTests
{
    private static readonly Guid Review = Guid.NewGuid();
    private static List<RiskFindingEntry> All(RiskCriterionFinding finding = RiskCriterionFinding.NoConcern, string? note = null)
        => RiskCriteriaCatalogue.Criteria.Select(c => new RiskFindingEntry(c.Code, finding, note)).ToList();
    private static List<string> Properties(RiskConclusion conclusion, IReadOnlyList<RiskFindingEntry>? findings) => RiskAssessmentRules.Check(conclusion, findings).Select(e => e.Property).ToList();

    // ---- catalogue ----------------------------------------------------------------------------------------------------------------------
    [Fact]
    public void The_catalogue_is_versioned_complete_and_has_unique_nonblank_codes()
    {
        Assert.Equal("provena-pilot-risk-criteria", RiskCriteriaCatalogue.Id); Assert.Equal("1", RiskCriteriaCatalogue.Version);
        Assert.Equal(7, RiskCriteriaCatalogue.Criteria.Count);
        Assert.Equal(RiskCriteriaCatalogue.Criteria.Count, RiskCriteriaCatalogue.Criteria.Select(c => c.Code).Distinct(StringComparer.Ordinal).Count());
        Assert.All(RiskCriteriaCatalogue.Criteria, c => { Assert.False(string.IsNullOrWhiteSpace(c.Code)); Assert.False(string.IsNullOrWhiteSpace(c.Title)); Assert.False(string.IsNullOrWhiteSpace(c.Guidance)); Assert.True(c.Code.Length <= 100); });
        Assert.Equal(RiskCriteriaCatalogue.Criteria.Count, RiskCriteriaCatalogue.Codes.Count);
        var dto = RiskCriteriaCatalogue.Dto(); Assert.Equal(RiskCriteriaCatalogue.Criteria.Count, dto.Criteria.Count); Assert.Equal("1", dto.Version);
    }

    // ---- finding rules ------------------------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData(RiskConclusion.Negligible)]
    [InlineData(RiskConclusion.NotNegligible)]
    public void Every_criterion_exactly_once_with_no_concern_is_valid(RiskConclusion conclusion) => Assert.Empty(RiskAssessmentRules.Check(conclusion, All()));

    [Fact]
    public void A_missing_unknown_or_duplicate_code_and_an_empty_set_are_rejected()
    {
        Assert.Contains("Findings", Properties(RiskConclusion.Negligible, null)); Assert.Contains("Findings", Properties(RiskConclusion.Negligible, []));
        Assert.Contains("Findings", Properties(RiskConclusion.Negligible, All().Skip(1).ToList())); // one criterion missing
        var unknown = All(); unknown[0] = unknown[0] with { Code = "NOT_A_CRITERION" };
        Assert.Contains("Findings[0].CriterionCode", Properties(RiskConclusion.Negligible, unknown));
        var duplicate = All(); duplicate[1] = duplicate[1] with { Code = duplicate[0].Code };
        Assert.Contains("Findings[1].CriterionCode", Properties(RiskConclusion.Negligible, duplicate));
        var blank = All(); blank[2] = blank[2] with { Code = "  " };
        Assert.Contains("Findings[2].CriterionCode", Properties(RiskConclusion.Negligible, blank));
        var codeCase = All(); codeCase[0] = codeCase[0] with { Code = codeCase[0].Code!.ToLowerInvariant() };
        Assert.NotEmpty(RiskAssessmentRules.Check(RiskConclusion.Negligible, codeCase)); // ordinal: codes are exact
    }

    [Theory]
    [InlineData(RiskCriterionFinding.Concern)]
    [InlineData(RiskCriterionFinding.InsufficientInformation)]
    public void A_concern_or_insufficient_information_finding_requires_a_nonblank_note(RiskCriterionFinding finding)
    {
        foreach (var note in new string?[] { null, "", "   " })
        {
            var findings = All(); findings[3] = findings[3] with { Finding = finding, Note = note };
            Assert.Contains("Findings[3].Note", Properties(RiskConclusion.NotNegligible, findings));
        }
        var good = All(); good[3] = good[3] with { Finding = finding, Note = "Observed by the Owner." };
        Assert.DoesNotContain("Findings[3].Note", Properties(RiskConclusion.NotNegligible, good));
    }

    [Fact]
    public void A_no_concern_finding_needs_no_note_and_a_note_may_not_exceed_the_limit()
    {
        Assert.Empty(RiskAssessmentRules.Check(RiskConclusion.Negligible, All(RiskCriterionFinding.NoConcern, null)));
        var findings = All(); findings[0] = findings[0] with { Finding = RiskCriterionFinding.NoConcern, Note = new string('x', RiskAssessmentRules.MaxNoteLength + 1) };
        Assert.Contains("Findings[0].Note", Properties(RiskConclusion.Negligible, findings));
        var boundary = All(); boundary[0] = boundary[0] with { Finding = RiskCriterionFinding.Concern, Note = new string('x', RiskAssessmentRules.MaxNoteLength) };
        Assert.Empty(RiskAssessmentRules.Check(RiskConclusion.NotNegligible, boundary));
    }

    [Fact]
    public void A_missing_or_undefined_finding_value_is_rejected()
    {
        var findings = All(); findings[0] = findings[0] with { Finding = null }; findings[1] = findings[1] with { Finding = (RiskCriterionFinding)99 };
        var errors = Properties(RiskConclusion.Negligible, findings);
        Assert.Contains("Findings[0].Finding", errors); Assert.Contains("Findings[1].Finding", errors);
    }

    [Fact]
    public void Concern_with_a_negligible_conclusion_is_valid_because_no_rule_derives_the_conclusion_or_requires_mitigation()
    {
        var findings = All(); findings[4] = findings[4] with { Finding = RiskCriterionFinding.Concern, Note = "Cooperative mixes lots." };
        Assert.Empty(RiskAssessmentRules.Check(RiskConclusion.Negligible, findings));
        Assert.Empty(RiskAssessmentRules.Check(RiskConclusion.NotNegligible, All())); // and NotNegligible needs no Concern finding either
    }

    [Fact]
    public void Negligible_with_insufficient_information_is_the_only_contradiction()
    {
        var findings = All(); findings[0] = findings[0] with { Finding = RiskCriterionFinding.InsufficientInformation, Note = "No data for the area." };
        Assert.Contains("Conclusion", Properties(RiskConclusion.Negligible, findings));
        Assert.Empty(RiskAssessmentRules.Check(RiskConclusion.NotNegligible, findings));
    }

    [Fact]
    public void Rule_messages_never_echo_client_supplied_values()
    {
        var findings = All(); findings[0] = findings[0] with { Code = "SECRET_CODE_VALUE" }; findings[1] = findings[1] with { Finding = RiskCriterionFinding.Concern, Note = null };
        Assert.DoesNotContain(RiskAssessmentRules.Check(RiskConclusion.Negligible, findings), e => e.Message.Contains("SECRET_CODE_VALUE"));
    }

    // ---- validators ---------------------------------------------------------------------------------------------------------------------
    [Fact]
    public void The_assessment_validator_requires_review_catalogue_version_conclusion_and_rationale()
    {
        var validator = new CreateRiskAssessmentValidator();
        var ok = new CreateRiskAssessmentInput(Guid.NewGuid(), null, null, "1", RiskConclusion.Negligible, "Reasoned.", []);
        Assert.True(validator.Validate(ok).IsValid);
        foreach (var bad in new[] { ok with { ReviewId = null }, ok with { ReviewId = Guid.Empty }, ok with { ExpectedLatestRiskAssessmentId = Guid.Empty }, ok with { ExpectedLatestMitigationMeasureId = Guid.Empty },
                     ok with { CriteriaCatalogueVersion = "" }, ok with { CriteriaCatalogueVersion = new string('1', 51) }, ok with { Conclusion = null }, ok with { Conclusion = (RiskConclusion)9 },
                     ok with { Rationale = "" }, ok with { Rationale = new string('x', 4001) } })
            Assert.False(validator.Validate(bad).IsValid);
        var tooLong = validator.Validate(ok with { Rationale = "R" + new string('y', 4100) });
        Assert.DoesNotContain(tooLong.Errors, e => e.ErrorMessage.Contains("yyyy"));
    }

    [Fact]
    public void The_mitigation_validator_requires_the_expected_assessment_description_and_time()
    {
        var validator = new RecordRiskMitigationValidator();
        var ok = new RecordRiskMitigationInput(Guid.NewGuid(), "Segregated the lot.", DateTimeOffset.UtcNow, null);
        Assert.True(validator.Validate(ok).IsValid);
        foreach (var bad in new[] { ok with { ExpectedRiskAssessmentId = null }, ok with { ExpectedRiskAssessmentId = Guid.Empty }, ok with { Description = "" }, ok with { Description = new string('x', 4001) },
                     ok with { PerformedAtUtc = null }, ok with { EvidenceDocumentId = Guid.Empty } })
            Assert.False(validator.Validate(bad).IsValid);
    }

    // ---- close gate ---------------------------------------------------------------------------------------------------------------------
    private static RiskAssessment Assessment(Guid? review = null, RiskConclusion conclusion = RiskConclusion.Negligible) => new() { ReviewRecordId = review ?? Review, Conclusion = conclusion, Rationale = "Reasoned." };

    [Fact]
    public void The_gate_checks_missing_then_review_mismatch_then_not_negligible_in_that_order()
    {
        string? Gate(RiskAssessment? a) => CaseClosurePolicy.RiskError(CaseClosureOutcome.ExternalDdsCompleted, Review, a)?.Reason;
        Assert.Equal("RiskAssessmentMissing", Gate(null));
        Assert.Equal("RiskAssessmentReviewMismatch", Gate(Assessment(Guid.NewGuid(), RiskConclusion.NotNegligible))); // a mismatch wins over a NotNegligible conclusion
        Assert.Equal("RiskNotNegligible", Gate(Assessment(conclusion: RiskConclusion.NotNegligible)));
        Assert.Null(Gate(Assessment()));
    }

    [Theory]
    [InlineData(CaseClosureOutcome.OutsideRegulatoryScope)]
    [InlineData(CaseClosureOutcome.UnsupportedFlow)]
    public void The_other_outcomes_never_need_or_look_at_a_risk_assessment(CaseClosureOutcome outcome)
    {
        Assert.Null(CaseClosurePolicy.RiskError(outcome, Review, null));
        Assert.Null(CaseClosurePolicy.RiskError(outcome, Review, Assessment(Guid.NewGuid(), RiskConclusion.NotNegligible)));
    }

    [Fact]
    public void The_gate_reads_no_timestamps_so_there_is_no_chronology_rule()
    {
        var early = Assessment(); early.AssessedAtUtc = DateTimeOffset.UtcNow.AddDays(30); // however the server time compares with any later External DDS record, only the review and the conclusion matter
        Assert.Null(CaseClosurePolicy.RiskError(CaseClosureOutcome.ExternalDdsCompleted, Review, early));
    }

    [Fact]
    public void The_shared_review_already_backs_a_closure_conflict_is_typed_and_keeps_its_detail()
    {
        var error = CaseClosurePolicy.ReviewAlreadyBacksClosure();
        Assert.Equal(409, error.Status); Assert.Equal("This review already backs a closure; record a new review before closing the case again.", error.Message);
        Assert.Equal("ReviewAlreadyBacksClosure", CaseClosurePolicy.ReviewAlreadyBacksClosureReason);
        Assert.Equal("ReviewAlreadyBacksClosure", error.Errors!.GetType().GetProperty("reason")!.GetValue(error.Errors));
    }

    [Fact]
    public void The_summary_carries_only_ids_number_and_conclusion()
    {
        var a = Assessment(); a.Number = 3;
        var summary = RiskAssessmentService.ToSummary(a);
        Assert.Equal(new RiskAssessmentSummaryDto(a.Id, 3, Review, RiskConclusion.Negligible), summary);
        Assert.DoesNotContain(typeof(RiskAssessmentSummaryDto).GetProperties(), p => p.Name is "Rationale" or "Findings");
    }
}
