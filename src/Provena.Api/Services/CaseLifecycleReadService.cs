using Microsoft.EntityFrameworkCore;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;

// Read model of the lifecycle history (Case Lifecycle v2): every closure with the Owner review that backed it and its reopening, and every archive period, oldest first.
// Pure reads under the tenant filter; used by GET lifecycle, the case details and the working export (inside the export's case-locked transaction).
public sealed class CaseLifecycleReadService(AppDbContext db)
{
    public async Task<CaseLifecycleDto> Get(Guid caseId, CancellationToken ct)
    {
        var c = await db.Cases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == caseId, ct) ?? throw ApiException.NotFound();
        return await Build(c, ct);
    }

    public async Task<CaseLifecycleDto> Build(EudrCase c, CancellationToken ct)
    {
        var rows = await (from k in db.CaseClosures.AsNoTracking()
                          join r in db.ReviewRecords.AsNoTracking() on k.ReviewRecordId equals r.Id
                          join v in db.CaseRevisions.AsNoTracking() on r.CaseRevisionId equals v.Id
                          where k.EudrCaseId == c.Id orderby k.Number
                          select new { Closure = k, r.Decision, RevisionNumber = v.Number }).ToListAsync(ct);
        var reopenings = (await db.CaseReopenings.AsNoTracking().Where(x => x.EudrCaseId == c.Id).ToListAsync(ct)).ToDictionary(x => x.CaseClosureId);
        var closures = rows.Select(x => ToDto(x.Closure, x.Decision, x.RevisionNumber, reopenings.GetValueOrDefault(x.Closure.Id))).ToList();
        var archivals = (await db.CaseArchivals.AsNoTracking().Where(x => x.EudrCaseId == c.Id).OrderBy(x => x.Number).ToListAsync(ct)).Select(ToDto).ToList();
        var currentArchival = archivals.LastOrDefault(x => x.UnarchivedAtUtc == null);
        var currentClosure = c.Status == EudrCaseStatus.Closed ? closures.LastOrDefault(x => x.Reopening == null) : null;
        return new(c.Status, currentArchival != null, currentClosure, currentArchival, closures, archivals);
    }

    public static CaseReopeningDto ToDto(CaseReopening r) => new(r.Id, r.CaseClosureId, r.Reason, r.ReopenedByUserId, r.ReopenedAtUtc);
    public static CaseClosureDto ToDto(CaseClosure k, ReviewDecision decision, int revisionNumber, CaseReopening? reopening) =>
        new(k.Id, k.Number, k.Outcome, k.ReviewRecordId, revisionNumber, decision, k.ExternalDdsReference, k.RiskAssessmentId, k.ClosedByUserId, k.ClosedAtUtc, reopening == null ? null : ToDto(reopening));
    public static CaseArchivalDto ToDto(CaseArchival a) => new(a.Id, a.Number, a.Reason, a.ArchivedByUserId, a.ArchivedAtUtc, a.UnarchiveReason, a.UnarchivedByUserId, a.UnarchivedAtUtc);
}
