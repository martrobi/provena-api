using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Domain;
namespace Provena.Api.Services;

// Pure closure rules (Case Lifecycle v2). The closure outcome is derived from the Owner review that backs it, never chosen by the client. ReturnedForCorrection cannot close.
// None of this is a legal-compliance or DDS-submission claim: ExternalDdsCompleted only records that the Owner accepted the dossier as complete under the pilot policy and that an
// external DDS reference was recorded (unverified).
public static class CaseClosurePolicy
{
    public static CaseClosureOutcome? Outcome(ReviewDecision decision) => decision switch
    {
        ReviewDecision.AcceptedAsCompleteUnderPolicy => CaseClosureOutcome.ExternalDdsCompleted,
        ReviewDecision.ConfirmedOutsideRegulatoryScope => CaseClosureOutcome.OutsideRegulatoryScope,
        ReviewDecision.ConfirmedUnsupportedFlow => CaseClosureOutcome.UnsupportedFlow,
        _ => null,
    };

    // The trimmed reference ExternalDdsCompleted requires; null when blank. Other outcomes reject any reference.
    public static string? NormalizeReference(string? reference) => string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();

    // Quantity Reconciliation v1: closing as ExternalDdsCompleted needs the CURRENT reconciliation to be Matched, even for a Current review made under an older policy version
    // (a policy-version change never stales a review). OutsideRegulatoryScope and UnsupportedFlow closures have no quantity gate. Returns the message, or null when closing is allowed.
    public static string? QuantityError(CaseClosureOutcome outcome, QuantityReconciliationResultDto reconciliation) =>
        outcome == CaseClosureOutcome.ExternalDdsCompleted && reconciliation.Outcome != QuantityReconciliationOutcome.Matched
            ? $"The case cannot be closed as ExternalDdsCompleted while the quantity reconciliation is {reconciliation.Outcome}; correct the quantities and record a new review."
            : null;

    // Returns the validation message, or null when the request's (legacy, optional) reference satisfies the outcome's rule. ExternalDdsCompleted no longer needs it (the structured
    // ExternalDdsRecord is authoritative; a supplied value must equal it, see ExternalDdsError); the other outcomes still reject any reference.
    public static string? ReferenceError(CaseClosureOutcome outcome, string? normalizedReference) =>
        outcome != CaseClosureOutcome.ExternalDdsCompleted && normalizedReference != null ? $"An external DDS reference is only accepted for ExternalDdsCompleted, not for {outcome}." : null;

    // External DDS Record v1 gate, evaluated AFTER the stale-review and quantity gates: closing as ExternalDdsCompleted needs a current ExternalDdsRecord that points at exactly the latest
    // (Current, accepted) review, and a legacy request reference, when supplied, must equal the record's reference number. Returns the machine-readable reason and message, or null.
    public static (string Reason, string Message)? ExternalDdsError(CaseClosureOutcome outcome, Guid latestReviewId, ExternalDdsRecord? current, string? normalizedRequestReference)
    {
        if (outcome != CaseClosureOutcome.ExternalDdsCompleted) return null;
        if (current == null) return ("ExternalDdsRecordMissing", "Record the external DDS submission of the latest review before closing the case as ExternalDdsCompleted.");
        if (current.ReviewId != latestReviewId) return ("ExternalDdsReviewMismatch", "The recorded external DDS submission belongs to a different review than the latest one; record the submission of the latest review.");
        if (normalizedRequestReference != null && !string.Equals(normalizedRequestReference, current.ReferenceNumber, StringComparison.Ordinal))
            return ("ExternalDdsReferenceMismatch", "The supplied external DDS reference differs from the recorded external DDS reference number; the recorded value is authoritative.");
        return null;
    }

    // The one typed conflict for "this review already backs a closure". Close and risk-assessment creation both use it (same reason, same detail), so a client matches errors.reason and never
    // the detail text. The detail text is unchanged from before the reason was added.
    public const string ReviewAlreadyBacksClosureReason = "ReviewAlreadyBacksClosure";
    public static ApiException ReviewAlreadyBacksClosure()
        => new(409, "This review already backs a closure; record a new review before closing the case again.", new { reason = ReviewAlreadyBacksClosureReason });

    // Risk Assessment v1 gate, evaluated AFTER the stale-review and quantity gates and BEFORE the External DDS gate: closing as ExternalDdsCompleted needs the LATEST risk assessment of the case to
    // rest on exactly the latest review and to be Negligible. It reads no timestamps (the assessment's server time says nothing about when the human judgement was made, so there is no chronology
    // rule against the External DDS record). Returns the machine-readable reason and message, or null; the other outcomes have no risk gate.
    public static (string Reason, string Message)? RiskError(CaseClosureOutcome outcome, Guid latestReviewId, RiskAssessment? latest)
    {
        if (outcome != CaseClosureOutcome.ExternalDdsCompleted) return null;
        if (latest == null) return ("RiskAssessmentMissing", "Record a risk assessment of the latest review before closing the case as ExternalDdsCompleted.");
        if (latest.ReviewRecordId != latestReviewId) return ("RiskAssessmentReviewMismatch", "The latest risk assessment belongs to a different review than the latest one; record a risk assessment of the latest review.");
        if (latest.Conclusion != RiskConclusion.Negligible) return ("RiskNotNegligible", "The latest risk assessment concluded NotNegligible; the case cannot be closed as ExternalDdsCompleted on it.");
        return null;
    }
}
