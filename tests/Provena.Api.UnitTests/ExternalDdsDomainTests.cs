using Provena.Api.Contracts;
using Provena.Api.Domain;
using Provena.Api.Services;
using Xunit;

namespace Provena.Api.UnitTests;

// External DDS Record v1: the pure close gate for ExternalDdsCompleted and the input validator.
public sealed class ExternalDdsDomainTests
{
    private static readonly Guid Review = Guid.NewGuid();
    private static ExternalDdsRecord Record(Guid? review = null, string reference = "DDS-1") => new() { ReviewId = review ?? Review, ReferenceNumber = reference, VerificationNumber = "VERIF-FIXTURE" };

    [Fact]
    public void The_gate_checks_missing_record_then_review_then_reference_in_that_order()
    {
        var gate = (ExternalDdsRecord? record, string? reference) => CaseClosurePolicy.ExternalDdsError(CaseClosureOutcome.ExternalDdsCompleted, Review, record, reference)?.Reason;
        Assert.Equal("ExternalDdsRecordMissing", gate(null, "DDS-1"));
        Assert.Equal("ExternalDdsReviewMismatch", gate(Record(Guid.NewGuid()), "OTHER")); // a review mismatch wins over a reference mismatch
        Assert.Equal("ExternalDdsReferenceMismatch", gate(Record(), "dds-1")); // exact, case-sensitive
        Assert.Null(gate(Record(), "DDS-1")); Assert.Null(gate(Record(), null)); // an absent legacy value is fine: the record is authoritative
    }

    [Theory]
    [InlineData(CaseClosureOutcome.OutsideRegulatoryScope)]
    [InlineData(CaseClosureOutcome.UnsupportedFlow)]
    public void The_other_outcomes_never_need_or_look_at_an_external_dds_record(CaseClosureOutcome outcome) =>
        Assert.Null(CaseClosurePolicy.ExternalDdsError(outcome, Review, null, null));

    [Fact]
    public void The_input_validator_requires_review_numbers_and_time_and_never_echoes_values()
    {
        var validator = new CreateExternalDdsRecordValidator();
        var ok = new CreateExternalDdsRecordInput(null, Guid.NewGuid(), "REF", "VERIF-FIXTURE", DateTimeOffset.UtcNow, null, null);
        Assert.True(validator.Validate(ok).IsValid);
        foreach (var bad in new[] { ok with { ReviewId = null }, ok with { ReviewId = Guid.Empty }, ok with { ReferenceNumber = "" }, ok with { VerificationNumber = "" }, ok with { SubmittedAtUtc = null },
                     ok with { ReferenceNumber = new string('x', 201) }, ok with { VerificationNumber = new string('x', 201) }, ok with { ReplacementReason = new string('x', 4001) }, ok with { ReceiptDocumentId = Guid.Empty } })
            Assert.False(validator.Validate(bad).IsValid);
        var tooLong = validator.Validate(ok with { VerificationNumber = "V" + new string('y', 250) });
        Assert.DoesNotContain(tooLong.Errors, e => e.ErrorMessage.Contains("yyyy"));
    }
}
