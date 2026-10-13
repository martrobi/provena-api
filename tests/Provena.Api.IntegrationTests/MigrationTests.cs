using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
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
    private const string RetainedEvidenceMigration = "20261012225511_RetainedEvidence";
    private const string ProvenanceMigration = "20261012234200_ProductionProvenance";
    private const string DossierMigration = "20261013004014_CaseDossier";
    private static readonly string[] ProvenanceTables = ["PlotRevisions", "ProductionPlots", "Producers"];
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

        Assert.Contains(RetainedEvidenceMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "Geolocations" """).SingleAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "Geolocations" WHERE "SupersededAtUtc" IS NOT NULL""").SingleAsync());
    }

#pragma warning disable EF1002 // Test-only SQL built from constants and locally generated GUIDs; no external input.
    [Fact]
    public async Task Clean_database_has_provenance_tables_indexes_and_composite_foreign_keys()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();
        var tables = await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'").ToListAsync();
        Assert.Subset(tables.ToHashSet(), ProvenanceTables.ToHashSet());

        async Task<string> Index(string name) => await db.Database.SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = {0}", name).SingleAsync();
        var current = await Index("IX_PlotRevisions_ProductionPlotId");
        Assert.Contains("UNIQUE", current); Assert.Contains("SupersededAtUtc\" IS NULL", current);
        var number = await Index("IX_PlotRevisions_ProductionPlotId_Number");
        Assert.Contains("UNIQUE", number); Assert.DoesNotContain("WHERE", number);
        foreach (var name in new[] { "IX_Producers_OrganizationId_ExternalReference", "IX_ProductionPlots_OrganizationId_ExternalReference" })
        { var def = await Index(name); Assert.Contains("UNIQUE", def); Assert.Contains("ExternalReference\" IS NOT NULL", def); }

        var foreignKeys = await db.Database.SqlQueryRaw<string>("""
            SELECT conrelid::regclass::text || ' ' || pg_get_constraintdef(oid) AS "Value" FROM pg_constraint
            WHERE contype = 'f' AND conrelid::regclass::text IN ('"ProductionPlots"', '"PlotRevisions"')
            """).ToListAsync();
        Assert.Contains(foreignKeys, f => f.Contains("(\"OrganizationId\", \"ProducerId\") REFERENCES \"Producers\"(\"OrganizationId\", \"Id\")"));
        Assert.Contains(foreignKeys, f => f.Contains("(\"OrganizationId\", \"ProductionPlotId\") REFERENCES \"ProductionPlots\"(\"OrganizationId\", \"Id\")"));
        Assert.All(foreignKeys, f => Assert.Contains("ON DELETE RESTRICT", f));
    }

    [Fact]
    public async Task Upgrade_from_retained_evidence_keeps_rows_and_leaves_provenance_tables_empty()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        await SeedEvidenceAtInitialSchema(db);
        await db.GetService<IMigrator>().MigrateAsync(RetainedEvidenceMigration);
        Assert.Equal([InitialMigration, RetainedEvidenceMigration], (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Empty(await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name IN ('Producers','ProductionPlots','PlotRevisions')").ToListAsync());

        await db.Database.MigrateAsync();

        Assert.Contains(ProvenanceMigration, await db.Database.GetAppliedMigrationsAsync());
        foreach (var table in new[] { "Documents", "Geolocations", "Submissions", "Cases", "Shipments", "Suppliers", "Products", "Organizations" })
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM \"{table}\"").SingleAsync());
        foreach (var table in ProvenanceTables)
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM \"{table}\"").SingleAsync());
    }

    private static async Task<(Guid Org, Guid Plot)> InsertPlot(AppDbContext db)
    {
        Guid org = Guid.NewGuid(), producer = Guid.NewGuid(), plot = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync($"""
INSERT INTO "Organizations" ("Id","CreatedAtUtc","UpdatedAtUtc","Name") VALUES ('{org}', now(), now(), 'Provenance Org');
INSERT INTO "Producers" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","Name","IsActive") VALUES ('{producer}', now(), now(), '{org}', 'Producer', true);
INSERT INTO "ProductionPlots" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","ProducerId","Name","IsActive") VALUES ('{plot}', now(), now(), '{org}', '{producer}', 'Plot', true);
INSERT INTO "PlotRevisions" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","ProductionPlotId","Number","CountryCode","GeometryType","GeometryJson") VALUES ('{Guid.NewGuid()}', now(), now(), '{org}', '{plot}', 1, 'BR', 1, '{EmptyJson}');
""");
        return (org, plot);
    }

    private static Task InsertRevision(AppDbContext db, Guid org, Guid plot, int number, bool superseded) => db.Database.ExecuteSqlRawAsync($"""
INSERT INTO "PlotRevisions" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","ProductionPlotId","Number","CountryCode","GeometryType","GeometryJson","SupersededAtUtc")
VALUES ('{Guid.NewGuid()}', now(), now(), '{org}', '{plot}', {number}, 'BR', 1, '{EmptyJson}', {(superseded ? "now()" : "NULL")});
""");

    [Fact]
    public async Task Second_current_plot_revision_violates_the_partial_unique_index_but_a_superseded_one_inserts()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();
        var (org, plot) = await InsertPlot(db);

        var second = await Assert.ThrowsAnyAsync<Exception>(() => InsertRevision(db, org, plot, 2, superseded: false));
        Assert.Contains("IX_PlotRevisions_ProductionPlotId", second.ToString());
        var duplicateNumber = await Assert.ThrowsAnyAsync<Exception>(() => InsertRevision(db, org, plot, 1, superseded: true));
        Assert.Contains("IX_PlotRevisions_ProductionPlotId_Number", duplicateNumber.ToString());
        await InsertRevision(db, org, plot, 2, superseded: true);
    }

    [Fact]
    public async Task Provenance_down_is_refused_while_a_producer_exists_and_succeeds_when_tables_are_empty()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();
        await InsertPlot(db);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync(RetainedEvidenceMigration));
        Assert.Contains("Production provenance records exist; rollback would destroy history", failure.ToString());
        Assert.Contains(ProvenanceMigration, await db.Database.GetAppliedMigrationsAsync());
        foreach (var table in ProvenanceTables)
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM \"{table}\"").SingleAsync());

        await db.Database.ExecuteSqlRawAsync("""TRUNCATE TABLE "PlotRevisions", "ProductionPlots", "Producers" """);
        await db.GetService<IMigrator>().MigrateAsync(RetainedEvidenceMigration);

        Assert.DoesNotContain(ProvenanceMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name IN ('Producers','ProductionPlots','PlotRevisions')").ToListAsync());
    }

    private static readonly string[] DossierTables = ["CaseDossiers", "CaseLots", "LotContributions", "ContributionPlots"];

    // Inserts a producer/plot/revision and a complete dossier graph (dossier, current lot, contribution, plot link) into the seeded organization and case.
    private static async Task InsertDossierGraph(AppDbContext db, SeededCase seed)
    {
        Guid producer = Guid.NewGuid(), plot = Guid.NewGuid(), revision = Guid.NewGuid(), dossier = Guid.NewGuid(), lot = Guid.NewGuid(), contribution = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync($"""
INSERT INTO "Producers" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","Name","IsActive") VALUES ('{producer}', now(), now(), '{seed.OrganizationId}', 'Producer', true);
INSERT INTO "ProductionPlots" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","ProducerId","Name","IsActive") VALUES ('{plot}', now(), now(), '{seed.OrganizationId}', '{producer}', 'Plot', true);
INSERT INTO "PlotRevisions" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","ProductionPlotId","Number","CountryCode","GeometryType","GeometryJson") VALUES ('{revision}', now(), now(), '{seed.OrganizationId}', '{plot}', 1, 'BR', 1, '{EmptyJson}');
INSERT INTO "CaseDossiers" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId") VALUES ('{dossier}', now(), now(), '{seed.OrganizationId}', '{seed.CaseId}');
INSERT INTO "CaseLots" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","CaseDossierId","Number","LotReference","Quantity","Unit") VALUES ('{lot}', now(), now(), '{seed.OrganizationId}', '{dossier}', 1, 'LOT-1', 10.5, 0);
INSERT INTO "LotContributions" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","CaseLotId","ProducerId","Quantity","Unit") VALUES ('{contribution}', now(), now(), '{seed.OrganizationId}', '{lot}', '{producer}', 10.5, 0);
INSERT INTO "ContributionPlots" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","LotContributionId","ProducerId","ProductionPlotId","PlotRevisionId","ProductionStartDate","ProductionEndDate") VALUES ('{Guid.NewGuid()}', now(), now(), '{seed.OrganizationId}', '{contribution}', '{producer}', '{plot}', '{revision}', '2024-01-01', '2024-12-31');
""");
    }

    private static async Task<int> Count(AppDbContext db, string table) => await db.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM \"{table}\"").SingleAsync();

    [Fact]
    public async Task Clean_database_has_dossier_tables_columns_indexes_keys_and_composite_foreign_keys()
    {
        await using var db = NewContext();
        Assert.False(db.Database.HasPendingModelChanges(), "The snapshot must match the DbContext.");
        await db.Database.MigrateAsync();
        Assert.Contains(DossierMigration, await db.Database.GetAppliedMigrationsAsync());
        var tables = await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'").ToListAsync();
        Assert.Subset(tables.ToHashSet(), DossierTables.ToHashSet());

        var periodColumns = await db.Database.SqlQueryRaw<string>("""
            SELECT column_name || ':' || data_type || ':' || is_nullable AS "Value" FROM information_schema.columns
            WHERE table_name = 'ContributionPlots' AND column_name IN ('ProductionStartDate','ProductionEndDate') ORDER BY column_name
            """).ToListAsync();
        Assert.Equal(["ProductionEndDate:date:YES", "ProductionStartDate:date:YES"], periodColumns);
        foreach (var table in new[] { "CaseLots", "LotContributions" })
            Assert.Equal("numeric(20,6)", await db.Database.SqlQueryRaw<string>(
                $"SELECT format_type(a.atttypid, a.atttypmod) AS \"Value\" FROM pg_attribute a WHERE a.attrelid = '\"{table}\"'::regclass AND a.attname = 'Quantity'").SingleAsync());

        async Task<string> Index(string name) => await db.Database.SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = {0}", name).SingleAsync();
        var dossierPerCase = await Index("IX_CaseDossiers_EudrCaseId");
        Assert.Contains("UNIQUE", dossierPerCase); Assert.DoesNotContain("WHERE", dossierPerCase);
        var currentLot = await Index("IX_CaseLots_CaseDossierId");
        Assert.Contains("UNIQUE", currentLot); Assert.Contains("SupersededAtUtc\" IS NULL", currentLot);
        var reference = await Index("IX_CaseLots_OrganizationId_LotReference");
        Assert.Contains("UNIQUE", reference); Assert.Contains("(\"OrganizationId\", \"LotReference\")", reference); Assert.Contains("SupersededAtUtc\" IS NULL", reference);
        foreach (var name in new[] { "IX_CaseLots_CaseDossierId_Number", "IX_LotContributions_CaseLotId_ProducerId", "IX_ContributionPlots_LotContributionId_ProductionPlotId" })
        { var def = await Index(name); Assert.Contains("UNIQUE", def); Assert.DoesNotContain("WHERE", def); }

        var constraints = await db.Database.SqlQueryRaw<string>("""
            SELECT conrelid::regclass::text || ' ' || contype::text || ' ' || conname || ' ' || pg_get_constraintdef(oid) AS "Value" FROM pg_constraint
            WHERE conrelid::regclass::text IN ('"CaseDossiers"', '"CaseLots"', '"LotContributions"', '"ContributionPlots"', '"ProductionPlots"', '"PlotRevisions"')
            """).ToListAsync();
        Assert.Contains(constraints, c => c.Contains("\"ProductionPlots\" u AK_ProductionPlots_OrganizationId_ProducerId_Id UNIQUE (\"OrganizationId\", \"ProducerId\", \"Id\")"));
        Assert.Contains(constraints, c => c.Contains("\"PlotRevisions\" u AK_PlotRevisions_OrganizationId_ProductionPlotId_Id UNIQUE (\"OrganizationId\", \"ProductionPlotId\", \"Id\")"));
        Assert.Contains(constraints, c => c.Contains("\"LotContributions\" u AK_LotContributions_OrganizationId_Id_ProducerId UNIQUE (\"OrganizationId\", \"Id\", \"ProducerId\")"));
        Assert.Contains(constraints, c => c.Contains("(\"OrganizationId\", \"EudrCaseId\") REFERENCES \"Cases\"(\"OrganizationId\", \"Id\")"));
        Assert.Contains(constraints, c => c.Contains("(\"OrganizationId\", \"CaseDossierId\") REFERENCES \"CaseDossiers\"(\"OrganizationId\", \"Id\")"));
        Assert.Contains(constraints, c => c.Contains("(\"OrganizationId\", \"CaseLotId\") REFERENCES \"CaseLots\"(\"OrganizationId\", \"Id\")"));
        Assert.Contains(constraints, c => c.Contains("\"LotContributions\" f") && c.Contains("(\"OrganizationId\", \"ProducerId\") REFERENCES \"Producers\"(\"OrganizationId\", \"Id\")"));
        Assert.Contains(constraints, c => c.Contains("(\"OrganizationId\", \"LotContributionId\", \"ProducerId\") REFERENCES \"LotContributions\"(\"OrganizationId\", \"Id\", \"ProducerId\")"));
        Assert.Contains(constraints, c => c.Contains("(\"OrganizationId\", \"ProducerId\", \"ProductionPlotId\") REFERENCES \"ProductionPlots\"(\"OrganizationId\", \"ProducerId\", \"Id\")"));
        Assert.Contains(constraints, c => c.Contains("(\"OrganizationId\", \"ProductionPlotId\", \"PlotRevisionId\") REFERENCES \"PlotRevisions\"(\"OrganizationId\", \"ProductionPlotId\", \"Id\")"));
        Assert.All(constraints.Where(c => c.Contains(" f ")), f => Assert.Contains("ON DELETE RESTRICT", f));
    }

    [Fact]
    public async Task Upgrade_from_production_provenance_keeps_rows_and_leaves_dossier_tables_empty()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        await SeedEvidenceAtInitialSchema(db);
        await db.GetService<IMigrator>().MigrateAsync(ProvenanceMigration);
        await InsertPlot(db);
        Assert.Equal([InitialMigration, RetainedEvidenceMigration, ProvenanceMigration], (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Empty(await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name IN ('CaseDossiers','CaseLots','LotContributions','ContributionPlots')").ToListAsync());

        await db.Database.MigrateAsync();

        Assert.Contains(DossierMigration, await db.Database.GetAppliedMigrationsAsync());
        foreach (var table in new[] { "Documents", "Geolocations", "Submissions", "Cases", "Shipments", "Suppliers", "Products", "Producers", "ProductionPlots", "PlotRevisions" })
            Assert.Equal(1, await Count(db, table));
        Assert.Equal(2, await Count(db, "Organizations"));
        foreach (var table in DossierTables)
            Assert.Equal(0, await Count(db, table));
    }

    [Fact]
    public async Task Dossier_indexes_and_foreign_keys_reject_violating_rows()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        var seed = await SeedEvidenceAtInitialSchema(db);
        await db.Database.MigrateAsync();
        await InsertDossierGraph(db, seed);

        var otherCase = Guid.NewGuid();
        var dossier = await db.Database.SqlQueryRaw<Guid>("""SELECT "Id" AS "Value" FROM "CaseDossiers" """).SingleAsync();
        var lot = await db.Database.SqlQueryRaw<Guid>("""SELECT "Id" AS "Value" FROM "CaseLots" """).SingleAsync();
        var secondDossier = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            $"""INSERT INTO "CaseDossiers" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId") VALUES ('{Guid.NewGuid()}', now(), now(), '{seed.OrganizationId}', '{seed.CaseId}')"""));
        Assert.Contains("IX_CaseDossiers_EudrCaseId", secondDossier.ToString());
        var secondCurrentLot = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            $"""INSERT INTO "CaseLots" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","CaseDossierId","Number","LotReference","Quantity","Unit") VALUES ('{Guid.NewGuid()}', now(), now(), '{seed.OrganizationId}', '{dossier}', 2, 'LOT-2', 1, 0)"""));
        Assert.Contains("IX_CaseLots_CaseDossierId", secondCurrentLot.ToString());
        // Duplicate current reference in the organization, on a different dossier.
        await db.Database.ExecuteSqlRawAsync($"""
INSERT INTO "Shipments" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","ReferenceNumber","SupplierId","ProductId","Quantity","Unit") SELECT '{Guid.NewGuid()}', now(), now(), "OrganizationId", 'SHP-2', "SupplierId", "ProductId", 1, 0 FROM "Shipments";
INSERT INTO "Cases" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","ShipmentId","Status") SELECT '{otherCase}', now(), now(), "OrganizationId", "Id", 0 FROM "Shipments" WHERE "ReferenceNumber" = 'SHP-2';
INSERT INTO "CaseDossiers" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId") VALUES ('{Guid.NewGuid()}', now(), now(), '{seed.OrganizationId}', '{otherCase}');
""");
        var otherDossier = await db.Database.SqlQueryRaw<Guid>($"""SELECT "Id" AS "Value" FROM "CaseDossiers" WHERE "EudrCaseId" = '{otherCase}' """).SingleAsync();
        var duplicateReference = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            $"""INSERT INTO "CaseLots" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","CaseDossierId","Number","LotReference","Quantity","Unit") VALUES ('{Guid.NewGuid()}', now(), now(), '{seed.OrganizationId}', '{otherDossier}', 1, 'LOT-1', 1, 0)"""));
        Assert.Contains("IX_CaseLots_OrganizationId_LotReference", duplicateReference.ToString());
        // A superseded duplicate is allowed.
        await db.Database.ExecuteSqlRawAsync(
            $"""INSERT INTO "CaseLots" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","CaseDossierId","Number","LotReference","Quantity","Unit","SupersededAtUtc") VALUES ('{Guid.NewGuid()}', now(), now(), '{seed.OrganizationId}', '{otherDossier}', 1, 'LOT-1', 1, 0, now())""");
        // Foreign keys: a lot of an unknown dossier, a contribution of an unknown producer.
        var orphanLot = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            $"""INSERT INTO "CaseLots" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","CaseDossierId","Number","LotReference","Quantity","Unit") VALUES ('{Guid.NewGuid()}', now(), now(), '{seed.OrganizationId}', '{Guid.NewGuid()}', 1, 'ORPHAN', 1, 0)"""));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(orphanLot.GetBaseException()).SqlState);
        var unknownProducer = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            $"""INSERT INTO "LotContributions" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","CaseLotId","ProducerId","Quantity","Unit") VALUES ('{Guid.NewGuid()}', now(), now(), '{seed.OrganizationId}', '{lot}', '{Guid.NewGuid()}', 1, 0)"""));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(unknownProducer.GetBaseException()).SqlState);
    }

    [Fact]
    public async Task Dossier_down_is_refused_while_dossier_data_exists_and_leaves_schema_and_rows_intact()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        var seed = await SeedEvidenceAtInitialSchema(db);
        await db.Database.MigrateAsync();
        await InsertDossierGraph(db, seed);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync(ProvenanceMigration));
        Assert.Contains("Case dossier records exist; rollback would destroy history", failure.ToString());

        Assert.Contains(DossierMigration, await db.Database.GetAppliedMigrationsAsync());
        foreach (var table in DossierTables)
            Assert.Equal(1, await Count(db, table));
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM pg_constraint WHERE conname IN ('AK_ProductionPlots_OrganizationId_ProducerId_Id','AK_PlotRevisions_OrganizationId_ProductionPlotId_Id')""").SingleAsync());
    }

    [Fact]
    public async Task Dossier_down_guard_triggers_when_only_a_dossier_anchor_exists()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        var seed = await SeedEvidenceAtInitialSchema(db);
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync($"""INSERT INTO "CaseDossiers" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId") VALUES ('{Guid.NewGuid()}', now(), now(), '{seed.OrganizationId}', '{seed.CaseId}')""");

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync(ProvenanceMigration));
        Assert.Contains("Case dossier records exist; rollback would destroy history", failure.ToString());
        Assert.Equal(1, await Count(db, "CaseDossiers"));
    }

    [Fact]
    public async Task Dossier_down_succeeds_when_dossier_tables_are_empty_and_drops_only_the_new_keys()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        var seed = await SeedEvidenceAtInitialSchema(db);
        await db.Database.MigrateAsync();
        await InsertDossierGraph(db, seed);
        await db.Database.ExecuteSqlRawAsync("""TRUNCATE TABLE "ContributionPlots", "LotContributions", "CaseLots", "CaseDossiers" """);

        await db.GetService<IMigrator>().MigrateAsync(ProvenanceMigration);

        Assert.DoesNotContain(DossierMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name IN ('CaseDossiers','CaseLots','LotContributions','ContributionPlots')").ToListAsync());
        Assert.Empty(await db.Database.SqlQueryRaw<string>("""SELECT conname AS "Value" FROM pg_constraint WHERE conname IN ('AK_ProductionPlots_OrganizationId_ProducerId_Id','AK_PlotRevisions_OrganizationId_ProductionPlotId_Id')""").ToListAsync());
        // The earlier provenance schema, its keys and its rows are untouched.
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM pg_constraint WHERE conname IN ('AK_ProductionPlots_OrganizationId_Id','AK_PlotRevisions_OrganizationId_Id')""").SingleAsync());
        Assert.Equal(1, await Count(db, "Producers"));
        Assert.Equal(1, await Count(db, "ProductionPlots"));
        Assert.Equal(1, await Count(db, "PlotRevisions"));
    }

#pragma warning restore EF1002

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
