using Provena.Api.Domain;
namespace Provena.Api.Services;
// A failing item is always a REAL completeness item (same code, subject and status vocabulary as GET completeness); derived decision requirements that have no
// failing item (a wrong applicability status, unadopted catalogue drift) are stated in Reason instead. No pseudo-item codes exist.
public sealed record ReviewFailingItem(string Code, string? SubjectType, Guid? SubjectId, string Status, string Reason);
public sealed record ReviewPreconditionFailure(string Reason, IReadOnlyList<ReviewFailingItem> FailingItems);
// Pure decision preconditions (ADR 0008), evaluated by ReviewService under the case lock and the catalogue FOR SHARE locks. A positive decision only means
// "accepted as complete under the versioned Provena pilot completeness policy" or "confirmed outside scope / unsupported flow as recorded"; never a legal
// compliance determination. ReturnedForCorrection is always allowed.
public static class ReviewPreconditions
{
    private static readonly CompletenessItemStatus[] Blocking = [CompletenessItemStatus.Missing, CompletenessItemStatus.Invalid, CompletenessItemStatus.AwaitingReview];

    public static ReviewPreconditionFailure? Check(ReviewDecision decision, CompletenessEvaluation evaluation, DossierFacts facts)
    {
        if (decision == ReviewDecision.ReturnedForCorrection) return null;
        var failing = new List<ReviewFailingItem>(); var reasons = new List<string>();
        var shipment = evaluation.Items.Single(i => i.Code == "MASTER_DATA_SHIPMENT_MATCH");
        if (shipment.Status != CompletenessItemStatus.Satisfied) failing.Add(FromItem(shipment));

        if (decision == ReviewDecision.AcceptedAsCompleteUnderPolicy)
        {
            if (evaluation.ApplicabilityStatus != ApplicabilityStatus.WithinSupportedScope)
                reasons.Add($"The derived applicability status is {evaluation.ApplicabilityStatus}; acceptance requires WithinSupportedScope.");
            failing.AddRange(evaluation.Items.Where(i => Blocking.Contains(i.Status) && i.Code != "MASTER_DATA_SHIPMENT_MATCH").Select(FromItem));
            if (facts.Drift.Count > 0)
                reasons.Add("Catalogue values changed since the case master data was copied (" + string.Join("; ", facts.Drift.Select(d => $"{d.Area.ToString().ToLowerInvariant()}: {string.Join(", ", d.Fields)}")) + "); refresh the case master data before accepting.");
        }
        else
        {
            var required = decision == ReviewDecision.ConfirmedOutsideRegulatoryScope ? ApplicabilityStatus.OutsideRegulatoryScope : ApplicabilityStatus.UnsupportedFlow;
            if (evaluation.ApplicabilityStatus != required)
            {
                reasons.Add($"The derived applicability status is {evaluation.ApplicabilityStatus}; {decision} requires {required}.");
                failing.AddRange(evaluation.Items.Where(i => i.Group == CompletenessGroup.Applicability && i.Code is "APPLICABILITY_RECORDED" or "APPLICABILITY_DECIDED" or "APPLICABILITY_PRODUCT_MATCH" && Blocking.Contains(i.Status)).Select(FromItem));
            }
        }
        if (failing.Count == 0 && reasons.Count == 0) return null;
        if (failing.Count > 0) reasons.Add($"{failing.Count} completeness item(s) block {decision}.");
        return new($"{decision} cannot be recorded: " + string.Join(" ", reasons), failing);
    }

    private static ReviewFailingItem FromItem(Contracts.CompletenessItemDto i) => new(i.Code, i.SubjectType, i.SubjectId, i.Status.ToString(), i.Reason);
}
