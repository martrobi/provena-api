using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Provena.Api.Auth;
using Provena.Api.Data;
using Provena.Api.Domain;
using Xunit;
namespace Provena.Api.IntegrationTests;

// Slice 3 / commit 1: persistence model and guards for Producer, ProductionPlot and PlotRevision (no API yet).
[Collection("Api")]
public sealed class ProvenanceModelTests(ApiFactory factory)
{
    private const string Square = """{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,1],[0,0]]]}""";

    private async Task<Guid> NewOrganization()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new Organization { Name = "Provenance Org " + Guid.NewGuid().ToString("N") };
        db.Organizations.Add(org); await db.SaveChangesAsync();
        return org.Id;
    }
    private static AppDbContext TenantDb(IServiceScope scope, Guid organizationId)
    {
        var tenant = new TenantContext(new HttpContextAccessor()); tenant.SetOrganization(organizationId);
        return new AppDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>(), tenant);
    }
    private async Task<(Guid ProducerId, Guid PlotId, Guid RevisionId)> Seed(Guid organizationId)
    {
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, organizationId);
        var producer = new Producer { OrganizationId = organizationId, Name = "Producer" };
        var plot = new ProductionPlot { OrganizationId = organizationId, ProducerId = producer.Id, Name = "Plot" };
        var revision = new PlotRevision { OrganizationId = organizationId, ProductionPlotId = plot.Id, Number = 1, CountryCode = "BR", GeometryType = PlotGeometryType.Polygon, GeometryJson = Square, AreaHectares = 1.5m };
        db.Producers.Add(producer); await db.SaveChangesAsync();
        db.ProductionPlots.Add(plot); await db.SaveChangesAsync();
        db.PlotRevisions.Add(revision); await db.SaveChangesAsync();
        return (producer.Id, plot.Id, revision.Id);
    }

    [Fact]
    public async Task Persistence_guard_rejects_deletes_and_mutation_of_provenance_but_allows_one_way_retirement()
    {
        var org = await NewOrganization(); var (producerId, plotId, revisionId) = await Seed(org);
        await using var scope = factory.Services.CreateAsyncScope();
        async Task Rejected(Func<AppDbContext, Task> change)
        {
            await using var db = TenantDb(scope, org);
            await change(db);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        // Deletes.
        await Rejected(async db => db.PlotRevisions.Remove(await db.PlotRevisions.SingleAsync(x => x.Id == revisionId)));
        await Rejected(async db => db.ProductionPlots.Remove(await db.ProductionPlots.SingleAsync(x => x.Id == plotId)));
        await Rejected(async db => db.Producers.Remove(await db.Producers.SingleAsync(x => x.Id == producerId)));
        // Mutation of revision content.
        await Rejected(async db => (await db.PlotRevisions.SingleAsync(x => x.Id == revisionId)).GeometryJson = """{"type":"Point","coordinates":[1,1]}""");
        await Rejected(async db => (await db.PlotRevisions.SingleAsync(x => x.Id == revisionId)).CountryCode = "CO");
        await Rejected(async db => (await db.PlotRevisions.SingleAsync(x => x.Id == revisionId)).Number = 9);
        await Rejected(async db => (await db.PlotRevisions.SingleAsync(x => x.Id == revisionId)).AreaHectares = 2m);
        // A plot never changes producer.
        var otherProducer = Guid.NewGuid();
        await using (var db = TenantDb(scope, org)) { db.Producers.Add(new Producer { Id = otherProducer, OrganizationId = org, Name = "Other" }); await db.SaveChangesAsync(); }
        await Rejected(async db => (await db.ProductionPlots.SingleAsync(x => x.Id == plotId)).ProducerId = otherProducer);

        // Identity metadata stays editable (deactivation is the only way to retire an identity).
        await using (var db = TenantDb(scope, org))
        {
            (await db.Producers.SingleAsync(x => x.Id == producerId)).IsActive = false;
            var plot = await db.ProductionPlots.SingleAsync(x => x.Id == plotId); plot.Name = "Renamed"; plot.IsActive = false;
            await db.SaveChangesAsync();
        }
        // One-way retirement is allowed once; clearing or changing it afterwards is not.
        await using (var db = TenantDb(scope, org))
        { (await db.PlotRevisions.SingleAsync(x => x.Id == revisionId)).SupersededAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); }
        await Rejected(async db => (await db.PlotRevisions.SingleAsync(x => x.Id == revisionId)).SupersededAtUtc = null);
        await Rejected(async db => (await db.PlotRevisions.SingleAsync(x => x.Id == revisionId)).SupersededAtUtc = DateTimeOffset.UtcNow.AddDays(1));

        await using var verify = TenantDb(scope, org);
        Assert.Equal(1, await verify.PlotRevisions.CountAsync());
        Assert.Empty(await verify.PlotRevisions.Current().ToListAsync());
    }

    [Fact]
    public async Task Current_returns_only_the_unsuperseded_revision_and_a_second_current_is_rejected()
    {
        var org = await NewOrganization(); var (_, plotId, firstId) = await Seed(org);
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org);
        (await db.PlotRevisions.SingleAsync(x => x.Id == firstId)).SupersededAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
        db.PlotRevisions.Add(new PlotRevision { OrganizationId = org, ProductionPlotId = plotId, Number = 2, CountryCode = "BR", GeometryType = PlotGeometryType.Point, GeometryJson = """{"type":"Point","coordinates":[1,1]}""" });
        await db.SaveChangesAsync();
        var current = await db.PlotRevisions.Current().SingleAsync(x => x.ProductionPlotId == plotId);
        Assert.Equal(2, current.Number);
        Assert.Equal(2, await db.PlotRevisions.CountAsync(x => x.ProductionPlotId == plotId));

        db.PlotRevisions.Add(new PlotRevision { OrganizationId = org, ProductionPlotId = plotId, Number = 3, CountryCode = "BR", GeometryType = PlotGeometryType.Point, GeometryJson = """{"type":"Point","coordinates":[2,2]}""" });
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("IX_PlotRevisions_ProductionPlotId", duplicate.ToString());
    }

    [Fact]
    public async Task Tenant_boundaries_hold_for_filters_guards_and_composite_foreign_keys()
    {
        var a = await NewOrganization(); var b = await NewOrganization(); var (producerA, plotA, _) = await Seed(a);
        await using var scope = factory.Services.CreateAsyncScope();

        await using (var dbB = TenantDb(scope, b))
        {
            Assert.Empty(await dbB.Producers.ToListAsync());
            Assert.Empty(await dbB.ProductionPlots.ToListAsync());
            Assert.Empty(await dbB.PlotRevisions.ToListAsync());
            // Stamping another organization's id is refused before the database is reached.
            dbB.Producers.Add(new Producer { OrganizationId = a, Name = "Spoofed" });
            await Assert.ThrowsAsync<InvalidOperationException>(() => dbB.SaveChangesAsync());
        }

        // The database itself refuses a plot (or revision) whose parent belongs to another organization.
        var admin = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var plotInsert = await Assert.ThrowsAnyAsync<Exception>(() => admin.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "ProductionPlots" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","ProducerId","Name","IsActive") VALUES ({Guid.NewGuid()}, now(), now(), {b}, {producerA}, 'Cross', true)"""));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(plotInsert.GetBaseException()).SqlState);
        var revisionInsert = await Assert.ThrowsAnyAsync<Exception>(() => admin.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "PlotRevisions" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","ProductionPlotId","Number","CountryCode","GeometryType","GeometryJson","SupersededAtUtc") VALUES ({Guid.NewGuid()}, now(), now(), {b}, {plotA}, 9, 'BR', 1, '[]', now())"""));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(revisionInsert.GetBaseException()).SqlState);
    }

    [Fact]
    public async Task External_reference_is_unique_per_organization_only_when_set()
    {
        var a = await NewOrganization(); var b = await NewOrganization();
        await using var scope = factory.Services.CreateAsyncScope();
        await using (var db = TenantDb(scope, a))
        {
            db.Producers.AddRange(new Producer { OrganizationId = a, Name = "n1" }, new Producer { OrganizationId = a, Name = "n2" }); // null references never collide
            db.Producers.Add(new Producer { OrganizationId = a, Name = "r1", ExternalReference = "REF" });
            await db.SaveChangesAsync();
        }
        await using (var db = TenantDb(scope, a))
        { db.Producers.Add(new Producer { OrganizationId = a, Name = "r2", ExternalReference = "REF" }); await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); }
        await using (var db = TenantDb(scope, b))
        { db.Producers.Add(new Producer { OrganizationId = b, Name = "r3", ExternalReference = "REF" }); await db.SaveChangesAsync(); }
    }
}
