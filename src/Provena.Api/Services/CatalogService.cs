using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Provena.Api.Auth;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;
public sealed class CatalogService(AppDbContext db, TenantContext tenant, IValidator<SupplierInput> suppliers, IValidator<ProductInput> products, AuditService audit)
{
    public Task<PageResult<Supplier>> Suppliers(string? search, int page, int pageSize, CancellationToken ct)
    {
        if (search?.Length > 200) throw new ApiException(400, "Search is limited to 200 characters.");
        var query = db.Suppliers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search)) { var term = search.ToLowerInvariant(); query = query.Where(x => x.Name.ToLower().Contains(term)); }
        return query.OrderBy(x => x.Name).ThenBy(x => x.Id).ToPageAsync(page, pageSize, ct);
    }
    public Task<PageResult<Product>> Products(string? search, bool? isActive, int page, int pageSize, CancellationToken ct)
    {
        if (search?.Length > 200) throw new ApiException(400, "Search is limited to 200 characters.");
        var query = db.Products.AsNoTracking();
        if (isActive.HasValue) query = query.Where(x => x.IsActive == isActive.Value);
        if (!string.IsNullOrWhiteSpace(search)) { var term = search.ToLowerInvariant(); query = query.Where(x => x.Name.ToLower().Contains(term) || x.CnCode.Contains(term)); }
        return query.OrderBy(x => x.Name).ThenBy(x => x.Id).ToPageAsync(page, pageSize, ct);
    }
    public async Task<Supplier> Supplier(Guid id, CancellationToken ct) => await db.Suppliers.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound();
    public async Task<Product> Product(Guid id, CancellationToken ct) => await db.Products.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound();
    public async Task<Supplier> SaveSupplier(Guid? id, SupplierInput input, CancellationToken ct)
    {
        await suppliers.ValidateAndThrowAsync(input, ct); var s = id.HasValue ? await Supplier(id.Value, ct) : new Supplier { OrganizationId = tenant.OrganizationId };
        s.Name = input.Name.Trim(); s.Email = NullIfBlank(input.Email); s.ExternalReference = NullIfBlank(input.ExternalReference); s.ContactPerson = input.ContactPerson; s.CountryCode = NullIfBlank(input.CountryCode); s.Notes = input.Notes;
        if (!id.HasValue) db.Suppliers.Add(s);
        audit.Add(null, id.HasValue ? "SupplierUpdated" : "SupplierCreated", $"Supplier {s.Id} saved.");
        await db.SaveChangesAsync(ct); return s;
    }
    public async Task<Product> SaveProduct(Guid? id, ProductInput input, CancellationToken ct)
    {
        await products.ValidateAndThrowAsync(input, ct); var p = id.HasValue ? await Product(id.Value, ct) : new Product { OrganizationId = tenant.OrganizationId };
        p.Name = input.Name.Trim(); p.CnCode = input.CnCode; p.CommodityType = input.CommodityType; p.ExternalReference = NullIfBlank(input.ExternalReference); p.Description = input.Description; p.IsActive = input.IsActive;
        if (!id.HasValue) db.Products.Add(p);
        audit.Add(null, id.HasValue ? "ProductUpdated" : "ProductCreated", $"Product {p.Id} saved.");
        await db.SaveChangesAsync(ct); return p;
    }
    public async Task DeleteSupplier(Guid id, CancellationToken ct)
    { var s = await Supplier(id, ct); if (await db.Shipments.AnyAsync(x => x.SupplierId == id, ct)) throw ApiException.Conflict("Supplier is used by a shipment."); db.Suppliers.Remove(s); audit.Add(null, "SupplierDeleted", $"Supplier {id} deleted."); await db.SaveChangesAsync(ct); }
    public async Task DeleteProduct(Guid id, CancellationToken ct)
    { var p = await Product(id, ct); if (await db.Shipments.AnyAsync(x => x.ProductId == id, ct)) throw ApiException.Conflict("Product is used by a shipment."); db.Products.Remove(p); audit.Add(null, "ProductDeleted", $"Product {id} deleted."); await db.SaveChangesAsync(ct); }
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
