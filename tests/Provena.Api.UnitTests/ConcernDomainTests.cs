using Provena.Api.Contracts;
using Provena.Api.Domain;
using Xunit;

namespace Provena.Api.UnitTests;

// Post-completion Concern & Corrective Action v1: the input validators (required, non-blank free text, defined enums, bounded lengths). The clock rules need the service and are
// covered over HTTP.
public sealed class ConcernDomainTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void A_concern_needs_a_defined_category_nonblank_summary_and_details_and_a_time()
    {
        var validator = new CreateConcernValidator();
        var ok = new CreateConcernInput(ConcernCategory.NewInformation, "Summary", "Details", Now);
        Assert.True(validator.Validate(ok).IsValid);
        foreach (var bad in new[] { ok with { Category = null }, ok with { Category = (ConcernCategory)99 }, ok with { Summary = "" }, ok with { Summary = null }, ok with { Details = "" }, ok with { Details = null },
                     ok with { RaisedAtUtc = null }, ok with { Summary = new string('x', 201) }, ok with { Details = new string('x', 4001) } })
            Assert.False(validator.Validate(bad).IsValid);
    }

    [Fact]
    public void An_action_needs_a_description_and_time_and_rejects_an_empty_evidence_id()
    {
        var validator = new RecordConcernActionValidator();
        var ok = new RecordConcernActionInput("Recalled the batch from the warehouse.", Now, null);
        Assert.True(validator.Validate(ok).IsValid);
        foreach (var bad in new[] { ok with { Description = "" }, ok with { Description = null }, ok with { PerformedAtUtc = null }, ok with { EvidenceDocumentId = Guid.Empty }, ok with { Description = new string('x', 4001) } })
            Assert.False(validator.Validate(bad).IsValid);
    }

    [Fact]
    public void A_notification_needs_a_recipient_defined_channel_and_time_and_a_bounded_note()
    {
        var validator = new RecordConcernNotificationValidator();
        var ok = new RecordConcernNotificationInput(Guid.NewGuid(), ConcernNotificationChannel.Email, Now, null, null);
        Assert.True(validator.Validate(ok).IsValid);
        foreach (var bad in new[] { ok with { RecipientId = null }, ok with { RecipientId = Guid.Empty }, ok with { Channel = null }, ok with { Channel = (ConcernNotificationChannel)99 }, ok with { NotifiedAtUtc = null },
                     ok with { Note = new string('x', 1001) }, ok with { EvidenceDocumentId = Guid.Empty } })
            Assert.False(validator.Validate(bad).IsValid);
    }

    [Fact]
    public void A_resolution_needs_a_defined_outcome_and_a_nonblank_rationale()
    {
        var validator = new ResolveConcernValidator();
        var ok = new ResolveConcernInput(ConcernResolutionOutcome.NoFurtherActionRequired, "Reviewed; nothing further is needed.");
        Assert.True(validator.Validate(ok).IsValid);
        foreach (var bad in new[] { ok with { Outcome = null }, ok with { Outcome = (ConcernResolutionOutcome)99 }, ok with { Outcome = (ConcernResolutionOutcome)2 }, ok with { Rationale = "" }, ok with { Rationale = null }, ok with { Rationale = new string('x', 4001) } })
            Assert.False(validator.Validate(bad).IsValid);
    }
}
