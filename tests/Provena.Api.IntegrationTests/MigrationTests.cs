using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Provena.Api.Auth;
using Provena.Api.Data;
using Provena.Api.Data.Migrations;
using Testcontainers.PostgreSql;
using Xunit;
namespace Provena.Api.IntegrationTests;

// Uses its own disposable database so the shared ApiFactory database is never migrated backwards or forwards.
public sealed class MigrationTests : IAsyncLifetime
{
    private const string InitialMigration = "20261010180000_InitialCreate";
    private static readonly string[] InitialTables =
    [
        "AspNetRoleClaims", "AspNetRoles", "AspNetUserClaims", "AspNetUserLogins", "AspNetUserRoles", "AspNetUserTokens", "AspNetUsers",
        "AuditEvents", "Cases", "Documents", "Geolocations", "OrganizationUsers", "Organizations", "Products", "Requirements",
        "Shipments", "SupplierRequests", "Submissions", "Suppliers"
    ];
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder().WithImage("postgres:17").Build();
    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();
    private AppDbContext NewContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(postgres.GetConnectionString()).Options,
        new TenantContext(new HttpContextAccessor()));

    [Fact]
    public void Initial_migration_target_model_still_describes_the_initial_schema()
    {
        var target = new InitialCreate().TargetModel;
        var tables = target.GetEntityTypes().Select(e => e.GetTableName()).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(InitialTables.Order(StringComparer.Ordinal).ToArray(), tables);
    }

    [Fact]
    public async Task Clean_database_migrates_to_the_current_model_with_expected_schema()
    {
        await using var db = NewContext();
        Assert.False(db.Database.HasPendingModelChanges(), "The snapshot must match the DbContext.");
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Contains(InitialMigration, await db.Database.GetAppliedMigrationsAsync());
        var tables = await db.Database.SqlQueryRaw<string>(
            "SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'").ToListAsync();
        Assert.Subset(tables.ToHashSet(), InitialTables.ToHashSet());
        var partialUnique = await db.Database.SqlQueryRaw<string>(
            "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'SupplierRequests' AND indexdef ILIKE '%UNIQUE%' AND indexdef ILIKE '%RevokedAtUtc%IS NULL%'").ToListAsync();
        Assert.NotEmpty(partialUnique);
    }

    [Fact]
    public async Task Database_at_the_initial_schema_upgrades_to_latest_and_keeps_existing_rows()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        Assert.Equal([InitialMigration], (await db.Database.GetAppliedMigrationsAsync()).ToArray());

        var organizationId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "Organizations" ("Id", "CreatedAtUtc", "UpdatedAtUtc", "Name") VALUES ({organizationId}, now(), now(), {"Upgrade Org"})""");

        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var names = await db.Database.SqlQueryRaw<string>("""SELECT "Name" AS "Value" FROM "Organizations" """).ToListAsync();
        Assert.Equal(["Upgrade Org"], names);
    }

    private const string EmptyJson = "[]"; // Any valid jsonb; avoids braces, which ExecuteSqlRawAsync treats as format placeholders.
    private const string LatestMigration = "20261012225511_RetainedEvidence";
    private sealed record SeededCase(Guid OrganizationId, Guid CaseId, Guid GeoId);

#pragma warning disable EF1002 // Test-only SQL built from locally generated GUIDs and constants; no external input.
    // Inserts a minimal org/supplier/product/shipment/case plus one document, geolocation and submission by SQL, valid at the InitialCreate schema.
    private static async Task<SeededCase> SeedEvidenceAtInitialSchema(AppDbContext db)
    {
        Guid org = Guid.NewGuid(), supplier = Guid.NewGuid(), product = Guid.NewGuid(), shipment = Guid.NewGuid(), eudrCase = Guid.NewGuid(), geo = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync($"""
INSERT INTO "Organizations" ("Id","CreatedAtUtc","UpdatedAtUtc","Name") VALUES ('{org}', now(), now(), 'Evidence Org');
INSERT INTO "Suppliers" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","Name") VALUES ('{supplier}', now(), now(), '{org}', 'Supplier');
INSERT INTO "Products" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","Name","CnCode","CommodityType","IsActive") VALUES ('{product}', now(), now(), '{org}', 'Product', '1801', 0, true);
INSERT INTO "Shipments" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","ReferenceNumber","SupplierId","ProductId","Quantity","Unit") VALUES ('{shipment}', now(), now(), '{org}', 'SHP-1', '{supplier}', '{product}', 1, 0);
INSERT INTO "Cases" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","ShipmentId","Status") VALUES ('{eudrCase}', now(), now(), '{org}', '{shipment}', 0);
INSERT INTO "Documents" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","OriginalFileName","StoredFileName","ContentType","SizeBytes","Category","StorageKey","UploadedByType","UploadedAtUtc") VALUES ('{Guid.NewGuid()}', now(), now(), '{org}', '{eudrCase}', 'a.pdf', 'a.pdf', 'application/pdf', 1, 0, 'key-a', 0, now());
INSERT INTO "Geolocations" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","OriginalFileName","StorageKey","RawGeoJson","FeatureCount","GeometryTypes","ValidationStatus","UploadedAtUtc") VALUES ('{geo}', now(), now(), '{org}', '{eudrCase}', 'g.geojson', 'key-g', '{EmptyJson}', 1, 'Polygon', 0, now());
INSERT INTO "Submissions" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId") VALUES ('{Guid.NewGuid()}', now(), now(), '{org}', '{eudrCase}');
""");
        return new(org, eudrCase, geo);
    }

    private static Task InsertGeolocation(AppDbContext db, SeededCase seed, bool superseded) => db.Database.ExecuteSqlRawAsync($"""
INSERT INTO "Geolocations" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","OriginalFileName","StorageKey","RawGeoJson","FeatureCount","GeometryTypes","ValidationStatus","UploadedAtUtc","SupersededAtUtc")
VALUES ('{Guid.NewGuid()}', now(), now(), '{seed.OrganizationId}', '{seed.CaseId}', 'g2.geojson', 'key-g2', '{EmptyJson}', 1, 'Polygon', 0, now(), {(superseded ? "now()" : "NULL")});
""");

#pragma warning restore EF1002

    [Fact]
    public async Task Clean_database_has_partial_unique_current_version_indexes()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();
        foreach (var index in new[] { "IX_Geolocations_EudrCaseId", "IX_Submissions_EudrCaseId" })
        {
            var def = await db.Database.SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = {0}", index).SingleAsync();
            Assert.Contains("UNIQUE", def);
            Assert.Contains("SupersededAtUtc\" IS NULL", def);
        }
    }

    [Fact]
    public async Task Upgrade_keeps_existing_evidence_as_current_with_null_new_columns_and_enforces_one_current_geometry()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        var seed = await SeedEvidenceAtInitialSchema(db);
        await db.Database.MigrateAsync();

        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "Documents" WHERE "WithdrawnAtUtc" IS NULL AND "WithdrawnByUserId" IS NULL AND "UploadedByUserId" IS NULL""").SingleAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "Geolocations" WHERE "SupersededAtUtc" IS NULL AND "UploadedByUserId" IS NULL""").SingleAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "Submissions" WHERE "SupersededAtUtc" IS NULL AND "SupplierRequestId" IS NULL""").SingleAsync());

        var duplicate = await Assert.ThrowsAnyAsync<Exception>(() => InsertGeolocation(db, seed, superseded: false));
        Assert.Contains("IX_Geolocations_EudrCaseId", duplicate.ToString());
        await InsertGeolocation(db, seed, superseded: true);
    }

    [Fact]
    public async Task Down_is_refused_while_retained_versions_exist_and_leaves_schema_and_rows_intact()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        var seed = await SeedEvidenceAtInitialSchema(db);
        await db.Database.MigrateAsync();
        await InsertGeolocation(db, seed, superseded: true);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync(InitialMigration));
        Assert.Contains("Retained evidence versions exist; rollback would destroy history", failure.ToString());

        Assert.Contains(LatestMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "Geolocations" """).SingleAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "Geolocations" WHERE "SupersededAtUtc" IS NOT NULL""").SingleAsync());
    }

    [Fact]
    public async Task Down_succeeds_when_no_retained_versions_exist()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        await SeedEvidenceAtInitialSchema(db);
        await db.Database.MigrateAsync();

        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);

        Assert.Equal([InitialMigration], (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "Geolocations" """).SingleAsync());
    }
}
