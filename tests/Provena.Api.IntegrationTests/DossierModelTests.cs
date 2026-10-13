using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Provena.Api.Auth;
using Provena.Api.Data;
using Provena.Api.Domain;
using Xunit;
namespace Provena.Api.IntegrationTests;

// Slice 4 / commit 1: persistence model and guards for the case dossier, lot versions, producer contributions and plot links (no API yet).
[Collection("Api")]
public sealed class DossierModelTests(ApiFactory factory)
{
    private const string Square = """{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,1],[0,0]]]}""";

    private sealed record Chain(Guid ProducerId, Guid PlotId, Guid RevisionId);
    private sealed record World(Guid OrgId, Guid CaseId, Guid DossierId, Guid LotId, Guid ContributionId, Guid LinkId, Chain First, Chain Second);

    private async Task<Guid> NewOrganization()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new Organization { Name = "Dossier Org " + Guid.NewGuid().ToString("N") };
        db.Organizations.Add(org); await db.SaveChangesAsync();
        return org.Id;
    }
    private static AppDbContext TenantDb(IServiceScope scope, Guid organizationId)
    {
        var tenant = new TenantContext(new HttpContextAccessor()); tenant.SetOrganization(organizationId);
        return new AppDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>(), tenant);
    }
    private static async Task<Chain> SeedChain(AppDbContext db, Guid org)
    {
        var producer = new Producer { OrganizationId = org, Name = "Producer" };
        var plot = new ProductionPlot { OrganizationId = org, ProducerId = producer.Id, Name = "Plot" };
        var revision = new PlotRevision { OrganizationId = org, ProductionPlotId = plot.Id, Number = 1, CountryCode = "BR", GeometryType = PlotGeometryType.Polygon, GeometryJson = Square };
        db.Producers.Add(producer); await db.SaveChangesAsync();
        db.ProductionPlots.Add(plot); await db.SaveChangesAsync();
        db.PlotRevisions.Add(revision); await db.SaveChangesAsync();
        return new(producer.Id, plot.Id, revision.Id);
    }
    private static async Task<Guid> SeedCase(AppDbContext db, Guid org)
    {
        var supplier = new Supplier { OrganizationId = org, Name = "Supplier" };
        var product = new Product { OrganizationId = org, Name = "Product", CnCode = "1801" };
        db.Suppliers.Add(supplier); db.Products.Add(product); await db.SaveChangesAsync();
        var shipment = new Shipment { OrganizationId = org, ReferenceNumber = "SHP-" + Guid.NewGuid().ToString("N"), SupplierId = supplier.Id, ProductId = product.Id, Quantity = 10, Unit = QuantityUnit.Kg };
        db.Shipments.Add(shipment); await db.SaveChangesAsync();
        var eudrCase = new EudrCase { OrganizationId = org, ShipmentId = shipment.Id };
        db.Cases.Add(eudrCase); await db.SaveChangesAsync();
        return eudrCase.Id;
    }
    private static async Task<(Guid DossierId, Guid LotId)> SeedDossier(AppDbContext db, Guid org, Guid caseId, string reference)
    {
        var dossier = new CaseDossier { OrganizationId = org, EudrCaseId = caseId };
        db.CaseDossiers.Add(dossier); await db.SaveChangesAsync();
        var lot = new CaseLot { OrganizationId = org, CaseDossierId = dossier.Id, Number = 1, LotReference = reference, Quantity = 10, Unit = QuantityUnit.Kg };
        db.CaseLots.Add(lot); await db.SaveChangesAsync();
        return (dossier.Id, lot.Id);
    }
    private async Task<World> Seed(Guid org, string? reference = null)
    {
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org);
        var first = await SeedChain(db, org); var second = await SeedChain(db, org);
        var caseId = await SeedCase(db, org);
        var (dossierId, lotId) = await SeedDossier(db, org, caseId, reference ?? "LOT-" + Guid.NewGuid().ToString("N"));
        var contribution = new LotContribution { OrganizationId = org, CaseLotId = lotId, ProducerId = first.ProducerId, Quantity = 4, Unit = QuantityUnit.Kg };
        db.LotContributions.Add(contribution); await db.SaveChangesAsync();
        var link = new ContributionPlot { OrganizationId = org, LotContributionId = contribution.Id, ProducerId = first.ProducerId, ProductionPlotId = first.PlotId, PlotRevisionId = first.RevisionId, ProductionStartDate = new DateOnly(2024, 1, 1), ProductionEndDate = new DateOnly(2024, 12, 31) };
        db.ContributionPlots.Add(link); await db.SaveChangesAsync();
        return new(org, caseId, dossierId, lotId, contribution.Id, link.Id, first, second);
    }

    private async Task Rejected(Guid org, Func<AppDbContext, Task> change)
    {
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org);
        await change(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Case_dossier_is_fully_immutable_and_never_deleted()
    {
        var w = await Seed(await NewOrganization());
        await Rejected(w.OrgId, async db => (await db.CaseDossiers.SingleAsync(x => x.Id == w.DossierId)).CreatedByUserId = Guid.NewGuid());
        await Rejected(w.OrgId, async db => (await db.CaseDossiers.SingleAsync(x => x.Id == w.DossierId)).EudrCaseId = Guid.NewGuid());
        await Rejected(w.OrgId, async db => (await db.CaseDossiers.SingleAsync(x => x.Id == w.DossierId)).UpdatedAtUtc = DateTimeOffset.UtcNow);
        await Rejected(w.OrgId, async db => db.CaseDossiers.Remove(await db.CaseDossiers.SingleAsync(x => x.Id == w.DossierId)));
    }

    [Fact]
    public async Task Case_lot_facts_are_immutable_but_allow_one_way_supersede()
    {
        var w = await Seed(await NewOrganization());
        await Rejected(w.OrgId, async db => (await db.CaseLots.SingleAsync(x => x.Id == w.LotId)).LotReference = "CHANGED");
        await Rejected(w.OrgId, async db => (await db.CaseLots.SingleAsync(x => x.Id == w.LotId)).Quantity = 99);
        await Rejected(w.OrgId, async db => (await db.CaseLots.SingleAsync(x => x.Id == w.LotId)).Unit = QuantityUnit.Ton);
        await Rejected(w.OrgId, async db => (await db.CaseLots.SingleAsync(x => x.Id == w.LotId)).Note = "edited");
        await Rejected(w.OrgId, async db => (await db.CaseLots.SingleAsync(x => x.Id == w.LotId)).Number = 7);
        await Rejected(w.OrgId, async db => (await db.CaseLots.SingleAsync(x => x.Id == w.LotId)).CreatedByUserId = Guid.NewGuid());
        await Rejected(w.OrgId, async db => (await db.CaseLots.SingleAsync(x => x.Id == w.LotId)).CaseDossierId = Guid.NewGuid());
        await Rejected(w.OrgId, async db => db.CaseLots.Remove(await db.CaseLots.SingleAsync(x => x.Id == w.LotId)));
        // Retiring while also editing a fact is not a clean retirement.
        await Rejected(w.OrgId, async db => { var lot = await db.CaseLots.SingleAsync(x => x.Id == w.LotId); lot.SupersededAtUtc = DateTimeOffset.UtcNow; lot.Quantity = 99; });

        await using var scope = factory.Services.CreateAsyncScope();
        await using (var db = TenantDb(scope, w.OrgId))
        { (await db.CaseLots.SingleAsync(x => x.Id == w.LotId)).SupersededAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); }
        await Rejected(w.OrgId, async db => (await db.CaseLots.SingleAsync(x => x.Id == w.LotId)).SupersededAtUtc = null);
        await Rejected(w.OrgId, async db => (await db.CaseLots.SingleAsync(x => x.Id == w.LotId)).SupersededAtUtc = DateTimeOffset.UtcNow.AddDays(1));

        await using var verify = TenantDb(scope, w.OrgId);
        Assert.Equal(1, await verify.CaseLots.CountAsync(x => x.Id == w.LotId));
        Assert.Empty(await verify.CaseLots.Current().ToListAsync());
    }

    [Fact]
    public async Task Lot_contributions_and_plot_links_cannot_be_modified_or_deleted()
    {
        var w = await Seed(await NewOrganization());
        await Rejected(w.OrgId, async db => (await db.LotContributions.SingleAsync(x => x.Id == w.ContributionId)).Quantity = 5);
        await Rejected(w.OrgId, async db => (await db.LotContributions.SingleAsync(x => x.Id == w.ContributionId)).Unit = QuantityUnit.Ton);
        await Rejected(w.OrgId, async db => (await db.LotContributions.SingleAsync(x => x.Id == w.ContributionId)).ProducerId = w.Second.ProducerId);
        await Rejected(w.OrgId, async db => db.LotContributions.Remove(await db.LotContributions.SingleAsync(x => x.Id == w.ContributionId)));
        await Rejected(w.OrgId, async db => (await db.ContributionPlots.SingleAsync(x => x.Id == w.LinkId)).ProductionStartDate = new DateOnly(2023, 1, 1));
        await Rejected(w.OrgId, async db => (await db.ContributionPlots.SingleAsync(x => x.Id == w.LinkId)).ProductionEndDate = null);
        await Rejected(w.OrgId, async db => (await db.ContributionPlots.SingleAsync(x => x.Id == w.LinkId)).PlotRevisionId = w.Second.RevisionId);
        await Rejected(w.OrgId, async db => db.ContributionPlots.Remove(await db.ContributionPlots.SingleAsync(x => x.Id == w.LinkId)));

        await using var scope = factory.Services.CreateAsyncScope(); await using var verify = TenantDb(scope, w.OrgId);
        Assert.Equal(1, await verify.LotContributions.CountAsync());
        var link = await verify.ContributionPlots.SingleAsync();
        Assert.Equal(new DateOnly(2024, 1, 1), link.ProductionStartDate);
        Assert.Equal(new DateOnly(2024, 12, 31), link.ProductionEndDate);
    }

    [Fact]
    public async Task Production_period_dates_are_optional_in_the_database()
    {
        var w = await Seed(await NewOrganization());
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, w.OrgId);
        // The API validator owns the "both or neither, start <= end" rule; the schema only stores what it is given.
        var contribution = new LotContribution { OrganizationId = w.OrgId, CaseLotId = w.LotId, ProducerId = w.Second.ProducerId, Quantity = 1, Unit = QuantityUnit.Kg };
        db.LotContributions.Add(contribution); await db.SaveChangesAsync();
        db.ContributionPlots.Add(new ContributionPlot { OrganizationId = w.OrgId, LotContributionId = contribution.Id, ProducerId = w.Second.ProducerId, ProductionPlotId = w.Second.PlotId, PlotRevisionId = w.Second.RevisionId });
        await db.SaveChangesAsync();
        Assert.Null((await db.ContributionPlots.SingleAsync(x => x.LotContributionId == contribution.Id)).ProductionStartDate);
    }

    [Fact]
    public async Task One_dossier_per_case_and_one_current_lot_per_dossier()
    {
        var w = await Seed(await NewOrganization());
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, w.OrgId);

        db.CaseDossiers.Add(new CaseDossier { OrganizationId = w.OrgId, EudrCaseId = w.CaseId });
        var secondDossier = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("IX_CaseDossiers_EudrCaseId", secondDossier.ToString());
        db.ChangeTracker.Clear();

        db.CaseLots.Add(new CaseLot { OrganizationId = w.OrgId, CaseDossierId = w.DossierId, Number = 2, LotReference = "OTHER-" + Guid.NewGuid().ToString("N"), Quantity = 1, Unit = QuantityUnit.Kg });
        var secondCurrent = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("IX_CaseLots_CaseDossierId", secondCurrent.ToString());
        db.ChangeTracker.Clear();

        db.CaseLots.Add(new CaseLot { OrganizationId = w.OrgId, CaseDossierId = w.DossierId, Number = 1, LotReference = "SUP-" + Guid.NewGuid().ToString("N"), Quantity = 1, Unit = QuantityUnit.Kg, SupersededAtUtc = DateTimeOffset.UtcNow });
        var duplicateNumber = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("IX_CaseLots_CaseDossierId_Number", duplicateNumber.ToString());
        db.ChangeTracker.Clear();

        // A contribution is unique per lot version and producer; a link per contribution and plot.
        db.LotContributions.Add(new LotContribution { OrganizationId = w.OrgId, CaseLotId = w.LotId, ProducerId = w.First.ProducerId, Quantity = 1, Unit = QuantityUnit.Kg });
        Assert.Contains("IX_LotContributions_CaseLotId_ProducerId", (await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).ToString());
        db.ChangeTracker.Clear();
        db.ContributionPlots.Add(new ContributionPlot { OrganizationId = w.OrgId, LotContributionId = w.ContributionId, ProducerId = w.First.ProducerId, ProductionPlotId = w.First.PlotId, PlotRevisionId = w.First.RevisionId });
        Assert.Contains("IX_ContributionPlots_LotContributionId_ProductionPlotId", (await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).ToString());
    }

    [Fact]
    public async Task Lot_reference_is_unique_among_current_lots_per_organization_only()
    {
        var a = await NewOrganization(); var b = await NewOrganization();
        var reference = "REF-" + Guid.NewGuid().ToString("N");
        var first = await Seed(a, reference);
        await using var scope = factory.Services.CreateAsyncScope();

        // A second current lot in the same organization with the same reference is refused.
        await using (var db = TenantDb(scope, a))
        {
            var caseId = await SeedCase(db, a);
            var dossier = new CaseDossier { OrganizationId = a, EudrCaseId = caseId };
            db.CaseDossiers.Add(dossier); await db.SaveChangesAsync();
            db.CaseLots.Add(new CaseLot { OrganizationId = a, CaseDossierId = dossier.Id, Number = 1, LotReference = reference, Quantity = 1, Unit = QuantityUnit.Kg });
            var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("IX_CaseLots_OrganizationId_LotReference", duplicate.ToString());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(duplicate.GetBaseException()).SqlState);
        }

        // Another organization may use the same reference.
        await using (var db = TenantDb(scope, b))
        { var caseId = await SeedCase(db, b); await SeedDossier(db, b, caseId, reference); }

        // Once the lot version is superseded the reference is free again: the same case may keep it on its next version,
        // and a superseded duplicate may coexist with a current one.
        await using (var db = TenantDb(scope, a))
        {
            (await db.CaseLots.SingleAsync(x => x.Id == first.LotId)).SupersededAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
            db.CaseLots.Add(new CaseLot { OrganizationId = a, CaseDossierId = first.DossierId, Number = 2, LotReference = reference, Quantity = 2, Unit = QuantityUnit.Kg });
            await db.SaveChangesAsync();
            var caseId = await SeedCase(db, a);
            var dossier = new CaseDossier { OrganizationId = a, EudrCaseId = caseId };
            db.CaseDossiers.Add(dossier); await db.SaveChangesAsync();
            db.CaseLots.Add(new CaseLot { OrganizationId = a, CaseDossierId = dossier.Id, Number = 1, LotReference = reference, Quantity = 1, Unit = QuantityUnit.Kg, SupersededAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            Assert.Equal(3, await db.CaseLots.CountAsync(x => x.LotReference == reference));
            Assert.Single(await db.CaseLots.Current().Where(x => x.LotReference == reference).ToListAsync());
        }
    }

    private async Task AssertForeignKeyViolation(Func<AppDbContext, Task> insert)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => insert(admin));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(failure.GetBaseException()).SqlState);
    }
    private static Task InsertLink(AppDbContext db, Guid org, Guid contribution, Guid producer, Guid plot, Guid revision) => db.Database.ExecuteSqlInterpolatedAsync(
        $"""INSERT INTO "ContributionPlots" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","LotContributionId","ProducerId","ProductionPlotId","PlotRevisionId") VALUES ({Guid.NewGuid()}, now(), now(), {org}, {contribution}, {producer}, {plot}, {revision})""");
    // A contribution on its own superseded lot version (no link yet), so a negative link insert cannot be masked by the unique (contribution, plot) index.
    private static async Task<Guid> FreshContribution(AppDbContext db, World w, Guid producer)
    {
        Guid lot = Guid.NewGuid(), contribution = Guid.NewGuid(); var number = Random.Shared.Next(1000, 1_000_000); var reference = "FRESH-" + lot.ToString("N");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "CaseLots" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","CaseDossierId","Number","LotReference","Quantity","Unit","SupersededAtUtc") VALUES ({lot}, now(), now(), {w.OrgId}, {w.DossierId}, {number}, {reference}, 1, 0, now())""");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "LotContributions" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","CaseLotId","ProducerId","Quantity","Unit") VALUES ({contribution}, now(), now(), {w.OrgId}, {lot}, {producer}, 1, 0)""");
        return contribution;
    }

    [Fact]
    public async Task Database_enforces_the_producer_plot_revision_chain_of_a_plot_link()
    {
        var w = await Seed(await NewOrganization());
        var secondContribution = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await admin.Database.ExecuteSqlInterpolatedAsync(
                $"""INSERT INTO "LotContributions" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","CaseLotId","ProducerId","Quantity","Unit") VALUES ({secondContribution}, now(), now(), {w.OrgId}, {w.LotId}, {w.Second.ProducerId}, 1, 0)""");
            // The consistent chain is accepted (second producer -> its own plot -> its own revision).
            await InsertLink(admin, w.OrgId, secondContribution, w.Second.ProducerId, w.Second.PlotId, w.Second.RevisionId);
        }
        Guid fresh;
        await using (var scope = factory.Services.CreateAsyncScope()) fresh = await FreshContribution(scope.ServiceProvider.GetRequiredService<AppDbContext>(), w, w.First.ProducerId);
        // A revision of another plot.
        await AssertForeignKeyViolation(db => InsertLink(db, w.OrgId, fresh, w.First.ProducerId, w.First.PlotId, w.Second.RevisionId));
        // A plot of another producer.
        await AssertForeignKeyViolation(db => InsertLink(db, w.OrgId, fresh, w.First.ProducerId, w.Second.PlotId, w.Second.RevisionId));
        // A link ProducerId that differs from the contribution's producer (even with a self-consistent plot and revision).
        await AssertForeignKeyViolation(db => InsertLink(db, w.OrgId, fresh, w.Second.ProducerId, w.Second.PlotId, w.Second.RevisionId));
    }

    [Fact]
    public async Task Database_refuses_cross_organization_dossier_references()
    {
        var a = await Seed(await NewOrganization()); var b = await NewOrganization();
        Guid bCase, bDossier, bLot, bContribution, bProducer;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await using var db = TenantDb(scope, b);
            bCase = await SeedCase(db, b);
            (bDossier, bLot) = await SeedDossier(db, b, bCase, "B-" + Guid.NewGuid().ToString("N"));
            var contribution = new LotContribution { OrganizationId = b, CaseLotId = bLot, ProducerId = bProducer = (await SeedChain(db, b)).ProducerId, Quantity = 1, Unit = QuantityUnit.Kg };
            db.LotContributions.Add(contribution); await db.SaveChangesAsync(); bContribution = contribution.Id;
        }
        Guid aFresh;
        await using (var scope = factory.Services.CreateAsyncScope()) aFresh = await FreshContribution(scope.ServiceProvider.GetRequiredService<AppDbContext>(), a, a.First.ProducerId);
        Guid aFreshCase; // A case of organization A without a dossier, so the unique dossier-per-case index cannot mask the FK.
        await using (var scope = factory.Services.CreateAsyncScope()) { await using var dbA = TenantDb(scope, a.OrgId); aFreshCase = await SeedCase(dbA, a.OrgId); }
        // Organization B rows pointing at organization A's case / dossier / producer / plot / revision / contribution.
        await AssertForeignKeyViolation(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "CaseDossiers" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId") VALUES ({Guid.NewGuid()}, now(), now(), {b}, {aFreshCase})"""));
        await AssertForeignKeyViolation(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "CaseLots" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","CaseDossierId","Number","LotReference","Quantity","Unit","SupersededAtUtc") VALUES ({Guid.NewGuid()}, now(), now(), {b}, {a.DossierId}, 5, 'X', 1, 0, now())"""));
        await AssertForeignKeyViolation(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "LotContributions" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","CaseLotId","ProducerId","Quantity","Unit") VALUES ({Guid.NewGuid()}, now(), now(), {b}, {a.LotId}, {Guid.NewGuid()}, 1, 0)"""));
        await AssertForeignKeyViolation(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "LotContributions" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","CaseLotId","ProducerId","Quantity","Unit") VALUES ({Guid.NewGuid()}, now(), now(), {b}, {bLot}, {a.First.ProducerId}, 1, 0)"""));
        await AssertForeignKeyViolation(db => InsertLink(db, b, aFresh, a.First.ProducerId, a.First.PlotId, a.First.RevisionId));
        await AssertForeignKeyViolation(db => InsertLink(db, b, bContribution, bProducer, a.First.PlotId, a.First.RevisionId));
        await AssertForeignKeyViolation(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "ContributionPlots" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","LotContributionId","ProducerId","ProductionPlotId","PlotRevisionId") VALUES ({Guid.NewGuid()}, now(), now(), {b}, {bContribution}, {Guid.NewGuid()}, {a.First.PlotId}, {a.First.RevisionId})"""));
    }

    [Fact]
    public async Task Tenant_filter_and_organization_stamp_guard_apply_to_dossier_types()
    {
        var a = await Seed(await NewOrganization()); var b = await NewOrganization();
        await using var scope = factory.Services.CreateAsyncScope(); await using var dbB = TenantDb(scope, b);
        Assert.Empty(await dbB.CaseDossiers.ToListAsync());
        Assert.Empty(await dbB.CaseLots.ToListAsync());
        Assert.Empty(await dbB.LotContributions.ToListAsync());
        Assert.Empty(await dbB.ContributionPlots.ToListAsync());
        dbB.CaseDossiers.Add(new CaseDossier { OrganizationId = a.OrgId, EudrCaseId = a.CaseId });
        await Assert.ThrowsAsync<InvalidOperationException>(() => dbB.SaveChangesAsync());
    }
}
