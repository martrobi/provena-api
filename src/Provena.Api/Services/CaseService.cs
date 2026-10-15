using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Provena.Api.Auth;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;
public sealed class CaseService(AppDbContext db, TenantContext tenant, EudrCaseStatusService statuses, AuditService audit, CaseLifecycleReadService lifecycle,
    IValidator<ReviewInput> reviewValidator)
{
    // Must be called inside a transaction. Serializes mutations and gives export a coherent snapshot.
    public async Task<EudrCase> Lock(Guid id, CancellationToken ct) => await db.Cases
        .FromSqlInterpolated($"SELECT * FROM \"Cases\" WHERE \"Id\" = {id} AND \"OrganizationId\" = {tenant.OrganizationId} FOR UPDATE")
        .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
    // A closed case is read-only until explicitly reopened; an archived case (independent of its workflow status) is read-only until unarchived. Call under the case lock.
    public async Task EnsureMutable(EudrCase c, CancellationToken ct)
    {
        if (c.Status == EudrCaseStatus.Closed) throw ApiException.Conflict("Closed cases cannot be changed; reopen the case first.");
        if (await db.CaseArchivals.Current().AnyAsync(x => x.EudrCaseId == c.Id, ct)) throw ApiException.Conflict("Archived cases cannot be changed; unarchive the case first.");
    }
    public async Task EnsureNotArchived(EudrCase c, CancellationToken ct)
    { if (await db.CaseArchivals.Current().AnyAsync(x => x.EudrCaseId == c.Id, ct)) throw ApiException.Conflict("Archived cases cannot be changed; unarchive the case first."); }
    public async Task<CaseDetails> Details(Guid id, CancellationToken ct)
    {
        var c = await db.Cases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound();
        var shipment = await db.Shipments.AsNoTracking().SingleAsync(x => x.Id == c.ShipmentId, ct);
        var supplier = await db.Suppliers.AsNoTracking().SingleAsync(x => x.Id == shipment.SupplierId, ct);
        var product = await db.Products.AsNoTracking().SingleAsync(x => x.Id == shipment.ProductId, ct);
        var submission = await db.Submissions.AsNoTracking().Current().SingleOrDefaultAsync(x => x.EudrCaseId == id, ct);
        var requirements = await db.Requirements.AsNoTracking().Where(x => x.EudrCaseId == id).OrderBy(x => x.Type).ToListAsync(ct);
        var docs = await db.Documents.AsNoTracking().Current().Where(x => x.EudrCaseId == id).OrderBy(x => x.UploadedAtUtc).ToListAsync(ct);
        var geo = await db.Geolocations.AsNoTracking().Current().SingleOrDefaultAsync(x => x.EudrCaseId == id, ct);
        var request = await db.SupplierRequests.AsNoTracking().Where(x => x.EudrCaseId == id).OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
        var events = await db.AuditEvents.AsNoTracking().Where(x => x.EudrCaseId == id).OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
        var history = await lifecycle.Build(c, ct);
        return new(c.Id, c.Status, shipment, supplier, product, submission, requirements, docs.Select(x => x.ToDto()).ToList(), geo?.ToDto(), request?.ToDto(), events, c.CreatedAtUtc, c.UpdatedAtUtc, history.CurrentClosure, history.CurrentArchival);
    }
    // Archived cases are excluded by default: archive is independent of the workflow status, so a status filter alone would otherwise mix archived and active cases.
    public async Task<PageResult<CaseListItem>> List(EudrCaseStatus? status, Guid? supplierId, Guid? productId, ArchivedFilter archived, string? search, int page, int pageSize, CancellationToken ct)
    {
        if (!Paging.IsValid(page, pageSize) || search?.Length > 200 || (status.HasValue && !Enum.IsDefined(status.Value)) || !Enum.IsDefined(archived)) throw new ApiException(400, "Invalid filters or pagination. page >= 1 and pageSize 1-100 are required.");
        var query = from c in db.Cases.AsNoTracking() join s in db.Shipments on c.ShipmentId equals s.Id
                    join supplier in db.Suppliers on s.SupplierId equals supplier.Id join product in db.Products on s.ProductId equals product.Id
                    let isArchived = db.CaseArchivals.Any(a => a.EudrCaseId == c.Id && a.UnarchivedAtUtc == null)
                    where (!status.HasValue || c.Status == status) && (!supplierId.HasValue || s.SupplierId == supplierId) && (!productId.HasValue || s.ProductId == productId)
                          && (archived == ArchivedFilter.Include || (archived == ArchivedFilter.Only ? isArchived : !isArchived))
                    select new { c, s, supplier, product, isArchived };
        if (!string.IsNullOrWhiteSpace(search)) { var term = search.ToLower(); query = query.Where(x => x.s.ReferenceNumber.ToLower().Contains(term) || x.supplier.Name.ToLower().Contains(term) || x.product.Name.ToLower().Contains(term)); }
        return await query.OrderByDescending(x => x.c.UpdatedAtUtc).ThenBy(x => x.c.Id)
            .ToPageAsync(x => new CaseListItem(x.c.Id, x.s.ReferenceNumber, x.supplier.Name, x.product.Name, x.c.Status,
                db.Requirements.Count(r => r.EudrCaseId == x.c.Id && r.IsRequired && (r.Status == RequirementStatus.Missing || r.Status == RequirementStatus.Rejected)),
                db.Requirements.Count(r => r.EudrCaseId == x.c.Id && r.Status == RequirementStatus.Submitted), x.c.UpdatedAtUtc, x.isArchived,
                x.c.Status == EudrCaseStatus.Closed ? db.CaseClosures.Where(k => k.EudrCaseId == x.c.Id).OrderByDescending(k => k.Number).Select(k => (CaseClosureOutcome?)k.Outcome).FirstOrDefault() : null), page, pageSize, ct);
    }
    public async Task Review(Guid caseId, Guid requirementId, ReviewInput input, CancellationToken ct)
    {
        await reviewValidator.ValidateAndThrowAsync(input, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await Lock(caseId, ct); await EnsureMutable(c, ct);
        var r = await db.Requirements.SingleOrDefaultAsync(x => x.Id == requirementId && x.EudrCaseId == caseId, ct) ?? throw ApiException.NotFound();
        if (input.Status != RequirementStatus.NotRequired && r.Status != RequirementStatus.Submitted)
            throw ApiException.Conflict("Only submitted evidence may be accepted or rejected.");
        r.Status = input.Status; r.ReviewerNote = input.ReviewerNote; r.ReviewedAtUtc = DateTimeOffset.UtcNow; r.ReviewedByUserId = tenant.UserId;
        audit.Add(c.Id, input.Status == RequirementStatus.Accepted ? "RequirementAccepted" : input.Status == RequirementStatus.Rejected ? "RequirementRejected" : "RequirementMarkedNotRequired", $"{r.Type}: {r.Status}.");
        await statuses.Recalculate(c, ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    // Three mutually exclusive buckets cover every case exactly once: ActiveOpen (not archived, an open workflow status), ActiveClosed (not archived, Closed) and Archived (any status).
    // Total = ActiveOpen + ActiveClosed + Archived; the breakdowns sum to their bucket. All counts are read in one repeatable-read snapshot, so they cannot disagree.
    public async Task<DashboardDto> Dashboard(CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var archived = await db.Cases.CountAsync(c => db.CaseArchivals.Any(a => a.EudrCaseId == c.Id && a.UnarchivedAtUtc == null), ct);
        var active = db.Cases.Where(c => !db.CaseArchivals.Any(a => a.EudrCaseId == c.Id && a.UnarchivedAtUtc == null));
        var byStatus = await active.GroupBy(x => x.Status).Select(x => new { Status = x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Status, x => x.Count, ct);
        var outcomes = await active.Where(c => c.Status == EudrCaseStatus.Closed)
            .Select(c => db.CaseClosures.Where(k => k.EudrCaseId == c.Id).OrderByDescending(k => k.Number).Select(k => k.Outcome).First()).ToListAsync(ct);
        await tx.CommitAsync(ct);
        int Count(EudrCaseStatus status) => byStatus.GetValueOrDefault(status);
        var open = new DashboardOpenByStatusDto(Count(EudrCaseStatus.Draft), Count(EudrCaseStatus.WaitingForSupplier), Count(EudrCaseStatus.ReviewNeeded), Count(EudrCaseStatus.ReadyForExport));
        var closed = new DashboardClosedByOutcomeDto(outcomes.Count(x => x == CaseClosureOutcome.ExternalDdsCompleted), outcomes.Count(x => x == CaseClosureOutcome.OutsideRegulatoryScope), outcomes.Count(x => x == CaseClosureOutcome.UnsupportedFlow));
        var activeOpen = open.Draft + open.WaitingForSupplier + open.ReviewNeeded + open.ReadyForExport; var activeClosed = Count(EudrCaseStatus.Closed);
        return new(activeOpen + activeClosed + archived, activeOpen, open, activeClosed, closed, archived);
    }
}
