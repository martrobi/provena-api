using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Provena.Api.Auth;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;
public sealed class DevelopmentSeeder(AppDbContext db, AuthService auth, TenantContext tenant, UserManager<ApplicationUser> users,
    CatalogService catalog, ShipmentService shipments, IConfiguration config)
{
    public async Task Seed(CancellationToken ct)
    {
        var password = config["Seed:Password"];
        if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("SeedDemoData requires Seed__Password. No default password is provided.");
        var user = await users.FindByEmailAsync("demo@provena.local");
        if (user == null)
        { await auth.Register(new RegisterRequest("demo@provena.local", password, "Demo", "User", "Adria Coffee Import d.o.o.", null), ct); user = await users.FindByEmailAsync("demo@provena.local"); }
        var org = await db.OrganizationUsers.SingleAsync(x => x.UserId == user!.Id, ct); tenant.SetOrganization(org.OrganizationId);
        var supplier = await db.Suppliers.SingleOrDefaultAsync(x => x.ExternalReference == "DEMO-SUP", ct)
            ?? await catalog.SaveSupplier(null, new("Coffee Brasil Ltd.", "DEMO-SUP", "contact@example.com", "Joao Silva", "BR", null), ct);
        var product = await db.Products.SingleOrDefaultAsync(x => x.ExternalReference == "DEMO-PROD", ct)
            ?? await catalog.SaveProduct(null, new("Arabica Coffee", "DEMO-PROD", "0901", CommodityType.Coffee, "Green arabica coffee"), ct);
        if (!await db.Shipments.AnyAsync(x => x.ReferenceNumber == "IMP-2027-00124", ct))
            await shipments.Save(null, new("IMP-2027-00124", supplier.Id, product.Id, 18500, QuantityUnit.Kg, "BR", null, null, null), ct);
    }
}
