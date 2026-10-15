using Microsoft.EntityFrameworkCore;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;
public sealed class EudrCaseStatusService(AppDbContext db, AuditService audit)
{
    // Rule order is intentional: unsent request -> Draft; missing required -> Waiting; review -> ReviewNeeded.
    // Pure derivation of the OPEN workflow status from requests and requirements. It never returns Closed: closing is an explicit Owner-backed CaseClosure.
    public static EudrCaseStatus CalculateOpen(bool requestSent, IReadOnlyCollection<EudrRequirement> requirements)
    {
        if (!requestSent) return EudrCaseStatus.Draft;
        if (requirements.Any(x => x.IsRequired && x.Status == RequirementStatus.Missing)) return EudrCaseStatus.WaitingForSupplier;
        if (requirements.Any(x => x.Status is RequirementStatus.Submitted or RequirementStatus.Rejected)) return EudrCaseStatus.ReviewNeeded;
        return requirements.Where(x => x.IsRequired).All(x => x.Status is RequirementStatus.Accepted or RequirementStatus.NotRequired)
            ? EudrCaseStatus.ReadyForExport : EudrCaseStatus.WaitingForSupplier;
    }
    // A Closed case keeps its status until it is explicitly reopened; archive does not appear here because it is independent of the workflow status.
    public static EudrCaseStatus Calculate(EudrCaseStatus current, bool requestSent, IReadOnlyCollection<EudrRequirement> requirements)
        => current == EudrCaseStatus.Closed ? current : CalculateOpen(requestSent, requirements);
    public async Task Recalculate(EudrCase c, CancellationToken ct)
    {
        var requirements = await db.Requirements.Where(x => x.EudrCaseId == c.Id).ToListAsync(ct);
        var sent = await db.SupplierRequests.AnyAsync(x => x.EudrCaseId == c.Id && x.SentAtUtc != null, ct);
        var next = Calculate(c.Status, sent, requirements);
        if (next != c.Status)
        {
            var previous = c.Status;
            c.Status = next;
            audit.Add(c.Id, "CaseStatusChanged", $"Case status changed from {previous} to {next}.");
            c.InternalReviewCompletedAtUtc = next == EudrCaseStatus.ReadyForExport ? DateTimeOffset.UtcNow : null;
            if (next == EudrCaseStatus.ReadyForExport) audit.Add(c.Id, "CaseReadyForExport", "All required items have been reviewed.");
        }
        c.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }
}
