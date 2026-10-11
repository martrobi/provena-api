using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Provena.Api.Auth;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;
public sealed class CaseService(AppDbContext db, TenantContext tenant, EudrCaseStatusService statuses, AuditService audit,
    IValidator<ReviewInput> reviewValidator, IValidator<CompleteInput> completeValidator)
{
    // Must be called inside a transaction. Serializes mutations and gives export a coherent snapshot.
    public async Task<EudrCase> Lock(Guid id, CancellationToken ct) => await db.Cases
        .FromSqlInterpolated($"SELECT * FROM \"Cases\" WHERE \"Id\" = {id} AND \"OrganizationId\" = {tenant.OrganizationId} FOR UPDATE")
        .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
    public static void EnsureEditable(EudrCase c)
    { if (c.Status is EudrCaseStatus.Completed or EudrCaseStatus.Archived) throw ApiException.Conflict("Completed or archived cases cannot be changed."); }
    public async Task<CaseDetails> Details(Guid id, CancellationToken ct)
    {
        var c = await db.Cases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound();
        var shipment = await db.Shipments.AsNoTracking().SingleAsync(x => x.Id == c.ShipmentId, ct);
        var supplier = await db.Suppliers.AsNoTracking().SingleAsync(x => x.Id == shipment.SupplierId, ct);
        var product = await db.Products.AsNoTracking().SingleAsync(x => x.Id == shipment.ProductId, ct);
        var submission = await db.Submissions.AsNoTracking().SingleOrDefaultAsync(x => x.EudrCaseId == id, ct);
        var requirements = await db.Requirements.AsNoTracking().Where(x => x.EudrCaseId == id).OrderBy(x => x.Type).ToListAsync(ct);
        var docs = await db.Documents.AsNoTracking().Where(x => x.EudrCaseId == id).OrderBy(x => x.UploadedAtUtc).ToListAsync(ct);
        var geo = await db.Geolocations.AsNoTracking().SingleOrDefaultAsync(x => x.EudrCaseId == id, ct);
        var request = await db.SupplierRequests.AsNoTracking().Where(x => x.EudrCaseId == id).OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
        var events = await db.AuditEvents.AsNoTracking().Where(x => x.EudrCaseId == id).OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
        return new(c.Id, c.Status, shipment, supplier, product, submission, requirements, docs.Select(x => x.ToDto()).ToList(), geo?.ToDto(), request?.ToDto(), events, c.ExternalDdsReference, c.CreatedAtUtc, c.UpdatedAtUtc, c.CompletedAtUtc);
    }
    public async Task<PageResult<CaseListItem>> List(EudrCaseStatus? status, Guid? supplierId, Guid? productId, string? search, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1 || pageSize is < 1 or > 100 || search?.Length > 200 || (status.HasValue && !Enum.IsDefined(status.Value))) throw new ApiException(400, "Invalid filters or pagination. page >= 1 and pageSize 1-100 are required.");
        var query = from c in db.Cases.AsNoTracking() join s in db.Shipments on c.ShipmentId equals s.Id
                    join supplier in db.Suppliers on s.SupplierId equals supplier.Id join product in db.Products on s.ProductId equals product.Id
                    where (!status.HasValue || c.Status == status) && (!supplierId.HasValue || s.SupplierId == supplierId) && (!productId.HasValue || s.ProductId == productId)
                    select new { c, s, supplier, product };
        if (!string.IsNullOrWhiteSpace(search)) { var term = search.ToLower(); query = query.Where(x => x.s.ReferenceNumber.ToLower().Contains(term) || x.supplier.Name.ToLower().Contains(term) || x.product.Name.ToLower().Contains(term)); }
        var count = await query.CountAsync(ct);
        var skip = (long)(page - 1) * pageSize;
        if (skip > int.MaxValue) throw new ApiException(400, "Page is out of range.");
        var items = await query.OrderByDescending(x => x.c.UpdatedAtUtc).ThenBy(x => x.c.Id).Skip((int)skip).Take(pageSize)
            .Select(x => new CaseListItem(x.c.Id, x.s.ReferenceNumber, x.supplier.Name, x.product.Name, x.c.Status,
                db.Requirements.Count(r => r.EudrCaseId == x.c.Id && r.IsRequired && (r.Status == RequirementStatus.Missing || r.Status == RequirementStatus.Rejected)),
                db.Requirements.Count(r => r.EudrCaseId == x.c.Id && r.Status == RequirementStatus.Submitted), x.c.UpdatedAtUtc)).ToListAsync(ct);
        return new(items, page, pageSize, count);
    }
    public async Task Review(Guid caseId, Guid requirementId, ReviewInput input, CancellationToken ct)
    {
        await reviewValidator.ValidateAndThrowAsync(input, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await Lock(caseId, ct); EnsureEditable(c);
        var r = await db.Requirements.SingleOrDefaultAsync(x => x.Id == requirementId && x.EudrCaseId == caseId, ct) ?? throw ApiException.NotFound();
        if (input.Status != RequirementStatus.NotRequired && r.Status != RequirementStatus.Submitted)
            throw ApiException.Conflict("Only submitted evidence may be accepted or rejected.");
        r.Status = input.Status; r.ReviewerNote = input.ReviewerNote; r.ReviewedAtUtc = DateTimeOffset.UtcNow; r.ReviewedByUserId = tenant.UserId;
        audit.Add(c.Id, input.Status == RequirementStatus.Accepted ? "RequirementAccepted" : input.Status == RequirementStatus.Rejected ? "RequirementRejected" : "RequirementMarkedNotRequired", $"{r.Type}: {r.Status}.");
        await statuses.Recalculate(c, ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task Complete(Guid id, CompleteInput input, CancellationToken ct)
    {
        await completeValidator.ValidateAndThrowAsync(input, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct); var c = await Lock(id, ct);
        await statuses.Recalculate(c, ct);
        if (c.Status != EudrCaseStatus.ReadyForExport) throw ApiException.Conflict("Only ReadyForExport cases can be completed.");
        c.Status = EudrCaseStatus.Completed; c.CompletedAtUtc = DateTimeOffset.UtcNow; c.ExternalDdsReference = input.ExternalDdsReference;
        audit.Add(id, "CaseCompleted", "External DDS workflow marked complete."); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task<object> Dashboard(CancellationToken ct)
    {
        var counts = await db.Cases.GroupBy(x => x.Status).Select(x => new { Status = x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Status, x => x.Count, ct);
        int Count(EudrCaseStatus status) => counts.GetValueOrDefault(status);
        return new { totalOpenCases = counts.Where(x => x.Key is not (EudrCaseStatus.Completed or EudrCaseStatus.Archived)).Sum(x => x.Value), draft = Count(EudrCaseStatus.Draft), waitingForSupplier = Count(EudrCaseStatus.WaitingForSupplier), reviewNeeded = Count(EudrCaseStatus.ReviewNeeded), readyForExport = Count(EudrCaseStatus.ReadyForExport) };
    }
}
