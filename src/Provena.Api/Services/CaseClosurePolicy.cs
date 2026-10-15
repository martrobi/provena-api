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

    // Returns the validation message, or null when the reference satisfies the outcome's rule.
    public static string? ReferenceError(CaseClosureOutcome outcome, string? normalizedReference) => outcome switch
    {
        CaseClosureOutcome.ExternalDdsCompleted when normalizedReference == null => "An external DDS reference is required to close a case as ExternalDdsCompleted.",
        CaseClosureOutcome.ExternalDdsCompleted => null,
        _ when normalizedReference != null => $"An external DDS reference is only accepted for ExternalDdsCompleted, not for {outcome}.",
        _ => null,
    };
}
