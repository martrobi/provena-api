using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Provena.Api.Auth;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;
public sealed class ShipmentService(AppDbContext db, TenantContext tenant, IValidator<ShipmentInput> validator, CaseService cases, AuditService audit)
{
    public async Task<object> Get(Guid id, CancellationToken ct)
    {
        var shipment = await db.Shipments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound();
        var caseId = await db.Cases.Where(x => x.ShipmentId == id).Select(x => x.Id).SingleAsync(ct);
        return new { shipment, eudrCaseId = caseId };
    }
    public Task<List<Shipment>> List(CancellationToken ct) => db.Shipments.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
    public async Task<object> Save(Guid? id, ShipmentInput input, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(input, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.Suppliers.AnyAsync(x => x.Id == input.SupplierId, ct) || !await db.Products.AnyAsync(x => x.Id == input.ProductId, ct))
            throw new ApiException(400, "Supplier or product was not found in your organization.");
        Shipment shipment; EudrCase c;
        if (id.HasValue)
        {
            var caseId = await db.Cases.Where(x => x.ShipmentId == id).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
            c = await cases.Lock(caseId, ct); CaseService.EnsureEditable(c);
            if (await db.SupplierRequests.AnyAsync(x => x.EudrCaseId == c.Id, ct) || await db.Requirements.AnyAsync(x => x.EudrCaseId == c.Id && x.Status != RequirementStatus.Missing && x.Status != RequirementStatus.NotRequired, ct)) throw ApiException.Conflict("Shipment cannot be edited after a supplier request or evidence submission. Archive and create a new shipment to preserve reviewed evidence.");
            shipment = await db.Shipments.SingleAsync(x => x.Id == id, ct);
        }
        else
        {
            shipment = new Shipment { OrganizationId = tenant.OrganizationId };
            c = new EudrCase { OrganizationId = tenant.OrganizationId, ShipmentId = shipment.Id };
            db.Shipments.Add(shipment); db.Cases.Add(c);
            foreach (var type in Enum.GetValues<RequirementType>())
                db.Requirements.Add(new EudrRequirement { OrganizationId = tenant.OrganizationId, EudrCaseId = c.Id, Type = type, Title = type.ToString() });
            audit.Add(c.Id, "CaseCreated", "Shipment and default checklist created.");
        }
        shipment.ReferenceNumber = input.ReferenceNumber.Trim(); shipment.SupplierId = input.SupplierId; shipment.ProductId = input.ProductId;
        shipment.Quantity = input.Quantity; shipment.Unit = input.Unit; shipment.OriginCountryCode = string.IsNullOrWhiteSpace(input.OriginCountryCode) ? null : input.OriginCountryCode;
        shipment.ExpectedArrivalDate = input.ExpectedArrivalDate; shipment.ReceivedDate = input.ReceivedDate; shipment.Notes = input.Notes;
        if (id.HasValue) audit.Add(c.Id, "ShipmentUpdated", "Shipment details updated.");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new { shipment, eudrCaseId = c.Id };
    }
    public async Task Archive(Guid shipmentId, CancellationToken ct)
    {
        var caseId = await db.Cases.Where(x => x.ShipmentId == shipmentId).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
        await using var tx = await db.Database.BeginTransactionAsync(ct); var c = await cases.Lock(caseId, ct);
        c.Status = EudrCaseStatus.Archived;
        foreach (var r in await db.SupplierRequests.Where(x => x.EudrCaseId == c.Id && x.RevokedAtUtc == null).ToListAsync(ct)) r.RevokedAtUtc = DateTimeOffset.UtcNow;
        audit.Add(c.Id, "CaseArchived", "Shipment and case archived; evidence retained."); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}
