using Provena.Api.Contracts;
using Provena.Api.Domain;
using Provena.Api.Services;
using Xunit;

namespace Provena.Api.UnitTests;

// Case Lifecycle v2: the pure closure rules (outcome mapping, DDS-reference rule) and the request validators.
public sealed class LifecycleDomainTests
{
    [Theory]
    [InlineData(ReviewDecision.AcceptedAsCompleteUnderPolicy, CaseClosureOutcome.ExternalDdsCompleted)]
    [InlineData(ReviewDecision.ConfirmedOutsideRegulatoryScope, CaseClosureOutcome.OutsideRegulatoryScope)]
    [InlineData(ReviewDecision.ConfirmedUnsupportedFlow, CaseClosureOutcome.UnsupportedFlow)]
    public void Every_positive_review_decision_maps_to_exactly_one_closure_outcome(ReviewDecision decision, CaseClosureOutcome outcome) =>
        Assert.Equal(outcome, CaseClosurePolicy.Outcome(decision));

    [Fact]
    public void A_review_that_returned_the_dossier_for_correction_cannot_close_and_every_decision_is_covered()
    {
        Assert.Null(CaseClosurePolicy.Outcome(ReviewDecision.ReturnedForCorrection));
        // Adding a decision must be a deliberate mapping decision: each one either maps to a distinct outcome or is the explicit non-closing decision.
        var mapped = Enum.GetValues<ReviewDecision>().Select(CaseClosurePolicy.Outcome).Where(x => x != null).Select(x => x!.Value).ToList();
        Assert.Equal(Enum.GetValues<CaseClosureOutcome>().Length, mapped.Count);
        Assert.Equal(mapped.Count, mapped.Distinct().Count());
        Assert.Equal(Enum.GetValues<CaseClosureOutcome>().Order(), mapped.Order());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("DDS-1", "DDS-1")]
    [InlineData("  DDS 1 \t", "DDS 1")]
    public void The_dds_reference_is_trimmed_and_blank_means_absent(string? input, string? expected) => Assert.Equal(expected, CaseClosurePolicy.NormalizeReference(input));

    [Fact]
    public void Only_ExternalDdsCompleted_requires_a_dds_reference_and_the_other_outcomes_reject_one()
    {
        Assert.NotNull(CaseClosurePolicy.ReferenceError(CaseClosureOutcome.ExternalDdsCompleted, null));
        Assert.Null(CaseClosurePolicy.ReferenceError(CaseClosureOutcome.ExternalDdsCompleted, "DDS-1"));
        foreach (var outcome in new[] { CaseClosureOutcome.OutsideRegulatoryScope, CaseClosureOutcome.UnsupportedFlow })
        {
            Assert.Null(CaseClosurePolicy.ReferenceError(outcome, null));
            Assert.Contains(outcome.ToString(), CaseClosurePolicy.ReferenceError(outcome, "DDS-1"));
        }
    }

    [Fact]
    public void Lifecycle_names_make_no_compliance_or_legal_claim()
    {
        var names = Enum.GetNames<CaseClosureOutcome>().Concat(Enum.GetNames<EudrCaseStatus>()).Concat(Enum.GetNames<ArchivedFilter>());
        Assert.DoesNotContain(names, n => n.Contains("Compliant", StringComparison.OrdinalIgnoreCase) || n.Contains("Legal", StringComparison.OrdinalIgnoreCase) || n.Contains("Approved", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Close_requires_the_expected_review_and_limits_the_dds_reference()
    {
        var validator = new CloseCaseValidator();
        Assert.True(validator.Validate(new CloseCaseInput(Guid.NewGuid(), null)).IsValid);
        Assert.True(validator.Validate(new CloseCaseInput(Guid.NewGuid(), new string('x', 200))).IsValid);
        Assert.False(validator.Validate(new CloseCaseInput(Guid.NewGuid(), new string('x', 201))).IsValid);
        Assert.False(validator.Validate(new CloseCaseInput(null, "DDS-1")).IsValid);
        Assert.False(validator.Validate(new CloseCaseInput(Guid.Empty, "DDS-1")).IsValid);
    }

    [Fact]
    public void Reopen_requires_the_expected_closure_and_a_non_blank_reason()
    {
        var validator = new ReopenCaseValidator();
        Assert.True(validator.Validate(new ReopenCaseInput(Guid.NewGuid(), "Corrected data.")).IsValid);
        Assert.True(validator.Validate(new ReopenCaseInput(Guid.NewGuid(), new string('r', 4000))).IsValid);
        foreach (var reason in new string?[] { null, "", " ", "\t\n", new string('r', 4001) })
            Assert.False(validator.Validate(new ReopenCaseInput(Guid.NewGuid(), reason)).IsValid, reason);
        Assert.False(validator.Validate(new ReopenCaseInput(null, "r")).IsValid);
        Assert.False(validator.Validate(new ReopenCaseInput(Guid.Empty, "r")).IsValid);
    }

    [Fact]
    public void Archive_and_unarchive_require_a_non_blank_reason_of_at_most_4000_characters()
    {
        foreach (var reason in new string?[] { null, "", " ", "\t", new string('r', 4001) })
        {
            Assert.False(new ArchiveCaseValidator().Validate(new ArchiveCaseInput(reason)).IsValid, reason);
            Assert.False(new UnarchiveCaseValidator().Validate(new UnarchiveCaseInput(reason)).IsValid, reason);
        }
        foreach (var reason in new[] { "Retention hold.", new string('r', 4000) })
        {
            Assert.True(new ArchiveCaseValidator().Validate(new ArchiveCaseInput(reason)).IsValid);
            Assert.True(new UnarchiveCaseValidator().Validate(new UnarchiveCaseInput(reason)).IsValid);
        }
    }
}
