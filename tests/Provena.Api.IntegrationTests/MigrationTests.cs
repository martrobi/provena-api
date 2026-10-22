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
        await db.Database.ExecuteSqlRawAsync("""TRUNCATE TABLE "RiskMitigationMeasures", "RiskAssessmentFindings", "RiskAssessments", "ExternalDdsRecords", "CaseReopenings", "CaseClosures", "ReviewRecords", "CaseRevisionEvidenceLinks", "CaseRevisionDocuments", "CaseRevisions", "ContributionPlots", "LotContributions", "CaseLots", "CaseDossiers" """);

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

    private const string CompletenessMigration = "20261013213911_CompletenessFacts";
    private static readonly string[] CompletenessTables = ["OperatorProfiles", "CaseMasterDataVersions", "CaseApplicabilities", "CaseRecipientStatements", "CaseRecipients", "CaseEvidenceLinks"];

    [Fact]
    public async Task Clean_database_has_completeness_tables_columns_indexes_keys_and_composite_foreign_keys()
    {
        await using var db = NewContext();
        Assert.False(db.Database.HasPendingModelChanges(), "The snapshot must match the DbContext.");
        await db.Database.MigrateAsync();
        Assert.Contains(CompletenessMigration, await db.Database.GetAppliedMigrationsAsync());
        var tables = await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'").ToListAsync();
        Assert.Subset(tables.ToHashSet(), CompletenessTables.ToHashSet());
        Assert.Equal("character varying(1000):YES", await db.Database.SqlQueryRaw<string>(
            """SELECT format_type(a.atttypid, a.atttypmod) || ':' || CASE WHEN a.attnotnull THEN 'NO' ELSE 'YES' END AS "Value" FROM pg_attribute a WHERE a.attrelid = '"Suppliers"'::regclass AND a.attname = 'PostalAddress'""").SingleAsync());
        Assert.Equal(["date:YES", "date:YES"], await db.Database.SqlQueryRaw<string>(
            """SELECT data_type || ':' || is_nullable AS "Value" FROM information_schema.columns WHERE table_name = 'CaseApplicabilities' AND column_name IN ('ActivityDate','EffectiveDate') ORDER BY column_name""").ToListAsync());

        async Task<string> Index(string name) => await db.Database.SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = {0}", name).SingleAsync();
        foreach (var name in new[] { "IX_CaseMasterDataVersions_EudrCaseId", "IX_CaseApplicabilities_EudrCaseId", "IX_CaseRecipientStatements_EudrCaseId" })
        { var def = await Index(name); Assert.Contains("UNIQUE", def); Assert.Contains("SupersededAtUtc\" IS NULL", def); }
        var link = await Index("IX_CaseEvidenceLinks_EudrCaseId_CaseDocumentId_Purpose");
        Assert.Contains("UNIQUE", link); Assert.Contains("(\"EudrCaseId\", \"CaseDocumentId\", \"Purpose\")", link); Assert.Contains("WithdrawnAtUtc\" IS NULL", link);
        foreach (var name in new[] { "IX_CaseMasterDataVersions_EudrCaseId_Number", "IX_CaseApplicabilities_EudrCaseId_Number", "IX_CaseRecipientStatements_EudrCaseId_Number", "IX_OperatorProfiles_OrganizationId" })
        { var def = await Index(name); Assert.Contains("UNIQUE", def); Assert.DoesNotContain("WHERE", def); }
        Assert.Contains("IX_Documents_OrganizationId_EudrCaseId", await db.Database.SqlQueryRaw<string>("""SELECT indexname AS "Value" FROM pg_indexes WHERE tablename = 'Documents'""").ToListAsync()); // the pre-existing FK index is kept

        var constraints = await db.Database.SqlQueryRaw<string>("""
            SELECT conrelid::regclass::text || ' ' || contype::text || ' ' || conname || ' ' || pg_get_constraintdef(oid) AS "Value" FROM pg_constraint
            WHERE conrelid::regclass::text IN ('"Documents"', '"OperatorProfiles"', '"CaseMasterDataVersions"', '"CaseApplicabilities"', '"CaseRecipientStatements"', '"CaseRecipients"', '"CaseEvidenceLinks"')
            """).ToListAsync();
        Assert.Contains(constraints, c => c.Contains("\"Documents\" u AK_Documents_OrganizationId_EudrCaseId_Id UNIQUE (\"OrganizationId\", \"EudrCaseId\", \"Id\")"));
        Assert.Contains(constraints, c => c.Contains("\"CaseEvidenceLinks\" f") && c.Contains("(\"OrganizationId\", \"EudrCaseId\", \"CaseDocumentId\") REFERENCES \"Documents\"(\"OrganizationId\", \"EudrCaseId\", \"Id\")"));
        Assert.Contains(constraints, c => c.Contains("\"CaseMasterDataVersions\" f") && c.Contains("(\"OrganizationId\", \"SupplierId\") REFERENCES \"Suppliers\"(\"OrganizationId\", \"Id\")"));
        Assert.Contains(constraints, c => c.Contains("\"CaseMasterDataVersions\" f") && c.Contains("(\"OrganizationId\", \"ProductId\") REFERENCES \"Products\"(\"OrganizationId\", \"Id\")"));
        Assert.Contains(constraints, c => c.Contains("\"CaseApplicabilities\" f") && c.Contains("(\"OrganizationId\", \"AssessedProductId\") REFERENCES \"Products\"(\"OrganizationId\", \"Id\")"));
        Assert.Contains(constraints, c => c.Contains("\"CaseRecipients\" f") && c.Contains("(\"OrganizationId\", \"CaseRecipientStatementId\") REFERENCES \"CaseRecipientStatements\"(\"OrganizationId\", \"Id\")"));
        foreach (var table in new[] { "CaseMasterDataVersions", "CaseApplicabilities", "CaseRecipientStatements", "CaseEvidenceLinks" })
            Assert.Contains(constraints, c => c.Contains($"\"{table}\" f") && c.Contains("(\"OrganizationId\", \"EudrCaseId\") REFERENCES \"Cases\"(\"OrganizationId\", \"Id\")"));
        Assert.All(constraints.Where(c => c.Contains(" f ")), f => Assert.Contains("ON DELETE RESTRICT", f));
        // No CHECK constraint belongs to this migration (that exception arrives with the review tables).
        Assert.DoesNotContain(constraints, c => c.Contains(" c "));
    }

    [Fact]
    public async Task Upgrade_from_case_dossier_keeps_every_row_and_creates_empty_completeness_tables_without_backfill()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        var seed = await SeedEvidenceAtInitialSchema(db);
        await db.GetService<IMigrator>().MigrateAsync(DossierMigration);
        await InsertDossierGraph(db, seed);
        Assert.Equal([InitialMigration, RetainedEvidenceMigration, ProvenanceMigration, DossierMigration], (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Empty(await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name IN ('OperatorProfiles','CaseMasterDataVersions','CaseApplicabilities','CaseRecipientStatements','CaseRecipients','CaseEvidenceLinks')").ToListAsync());

        await db.Database.MigrateAsync();

        Assert.Contains(CompletenessMigration, await db.Database.GetAppliedMigrationsAsync());
        foreach (var table in new[] { "Documents", "Geolocations", "Submissions", "Cases", "Shipments", "Suppliers", "Products", "Producers", "ProductionPlots", "PlotRevisions", "CaseDossiers", "CaseLots", "LotContributions", "ContributionPlots" })
            Assert.Equal(1, await Count(db, table));
        foreach (var table in CompletenessTables) Assert.Equal(0, await Count(db, table));
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "Suppliers" WHERE "PostalAddress" IS NOT NULL""").SingleAsync());
    }

    // Inserts one row of the named completeness table (or populates a supplier postal address) for the seeded organization and case.
    private static async Task InsertCompletenessRow(AppDbContext db, SeededCase seed, string category)
    {
        var org = seed.OrganizationId; var caseId = seed.CaseId; var id = Guid.NewGuid();
        var sql = category switch
        {
            "OperatorProfiles" => $"""INSERT INTO "OperatorProfiles" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","LegalName","PostalAddress","Email","EstablishmentCountryCode") VALUES ('{id}', now(), now(), '{org}', 'Op', 'Addr', 'op@example.test', 'HR')""",
            "CaseMasterDataVersions" => $"""INSERT INTO "CaseMasterDataVersions" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","SupplierId","SupplierName","ProductId","ProductCnCode","ProductCommodity","ProductName") SELECT '{id}', now(), now(), '{org}', '{caseId}', 1, s."Id", 'S', p."Id", '1801', 0, 'P' FROM "Suppliers" s, "Products" p WHERE s."OrganizationId" = '{org}' AND p."OrganizationId" = '{org}'""",
            "CaseApplicabilities" => $"""INSERT INTO "CaseApplicabilities" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","AssessedProductId","AssessedCnCode","AssessedCommodity","SingleRelevantCommodity","ActivityType","OperatorRole","ActivityDateKind","RegulatoryScope","FlowSupport") SELECT '{id}', now(), now(), '{org}', '{caseId}', 1, p."Id", '1801', 0, true, 0, 0, 0, 0, 0 FROM "Products" p WHERE p."OrganizationId" = '{org}'""",
            "CaseRecipientStatements" => $"""INSERT INTO "CaseRecipientStatements" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","Resolution","NotApplicableBasis") VALUES ('{id}', now(), now(), '{org}', '{caseId}', 1, 1, 'basis')""",
            "CaseRecipients" => $"""
INSERT INTO "CaseRecipientStatements" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","Resolution") VALUES ('{id}', now(), now(), '{org}', '{caseId}', 1, 0);
INSERT INTO "CaseRecipients" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","CaseRecipientStatementId","Name","PostalAddress","Email") VALUES ('{Guid.NewGuid()}', now(), now(), '{org}', '{id}', 'N', 'A', 'r@example.test')
""",
            "CaseEvidenceLinks" => $"""INSERT INTO "CaseEvidenceLinks" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","CaseDocumentId","Purpose") SELECT '{id}', now(), now(), '{org}', '{caseId}', d."Id", 0 FROM "Documents" d WHERE d."EudrCaseId" = '{caseId}'""",
            "Suppliers.PostalAddress" => $"""UPDATE "Suppliers" SET "PostalAddress" = 'Street 1' WHERE "OrganizationId" = '{org}'""",
            _ => throw new ArgumentOutOfRangeException(nameof(category)),
        };
        await db.Database.ExecuteSqlRawAsync(sql);
    }
    private static async Task ClearCompleteness(AppDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""TRUNCATE TABLE "RiskMitigationMeasures", "RiskAssessmentFindings", "RiskAssessments", "ConcernResolutions", "ConcernNotifications", "ConcernActions", "CaseConcerns", "ExternalDdsRecords", "CaseReopenings", "CaseClosures", "ReviewRecords", "CaseRevisionEvidenceLinks", "CaseRevisionDocuments", "CaseRevisions", "CaseRecipients", "CaseRecipientStatements", "CaseEvidenceLinks", "CaseApplicabilities", "CaseMasterDataVersions", "OperatorProfiles" """);
        await db.Database.ExecuteSqlRawAsync("""UPDATE "Suppliers" SET "PostalAddress" = NULL""");
    }

    [Theory]
    [InlineData("OperatorProfiles")]
    [InlineData("CaseMasterDataVersions")]
    [InlineData("CaseApplicabilities")]
    [InlineData("CaseRecipientStatements")]
    [InlineData("CaseRecipients")]
    [InlineData("CaseEvidenceLinks")]
    [InlineData("Suppliers.PostalAddress")]
    public async Task Completeness_down_is_refused_for_every_category_and_leaves_schema_and_rows_intact(string category)
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        var seed = await SeedEvidenceAtInitialSchema(db);
        await db.Database.MigrateAsync();
        await InsertCompletenessRow(db, seed, category);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync(DossierMigration));
        Assert.Contains("Completeness fact records exist; rollback would destroy history", failure.ToString());

        Assert.Contains(CompletenessMigration, await db.Database.GetAppliedMigrationsAsync());
        var tables = await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'").ToListAsync();
        Assert.Subset(tables.ToHashSet(), CompletenessTables.ToHashSet());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM pg_constraint WHERE conname = 'AK_Documents_OrganizationId_EudrCaseId_Id'""").SingleAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM information_schema.columns WHERE table_name = 'Suppliers' AND column_name = 'PostalAddress'""").SingleAsync());
        if (category == "Suppliers.PostalAddress") Assert.Equal("Street 1", await db.Database.SqlQueryRaw<string>("""SELECT "PostalAddress" AS "Value" FROM "Suppliers" """).SingleAsync());
        else Assert.Equal(1, await Count(db, category));
    }

    [Fact]
    public async Task Completeness_down_succeeds_when_empty_and_drops_only_its_own_objects()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        var seed = await SeedEvidenceAtInitialSchema(db);
        await db.Database.MigrateAsync();
        await InsertDossierGraph(db, seed);
        // Rows may have existed and been removed administratively; only an empty state may roll back.
        foreach (var category in new[] { "OperatorProfiles", "CaseMasterDataVersions", "CaseApplicabilities", "CaseRecipients", "CaseEvidenceLinks", "Suppliers.PostalAddress" }) await InsertCompletenessRow(db, seed, category);
        await ClearCompleteness(db);

        await db.GetService<IMigrator>().MigrateAsync(DossierMigration);

        Assert.DoesNotContain(CompletenessMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name IN ('OperatorProfiles','CaseMasterDataVersions','CaseApplicabilities','CaseRecipientStatements','CaseRecipients','CaseEvidenceLinks')").ToListAsync());
        Assert.Empty(await db.Database.SqlQueryRaw<string>("""SELECT conname AS "Value" FROM pg_constraint WHERE conname = 'AK_Documents_OrganizationId_EudrCaseId_Id'""").ToListAsync());
        Assert.Empty(await db.Database.SqlQueryRaw<string>("""SELECT column_name AS "Value" FROM information_schema.columns WHERE table_name = 'Suppliers' AND column_name = 'PostalAddress'""").ToListAsync());
        // Earlier schema, keys and rows are untouched.
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM pg_constraint WHERE conname = 'AK_Documents_OrganizationId_Id'""").SingleAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM pg_indexes WHERE indexname = 'IX_Documents_OrganizationId_EudrCaseId'""").SingleAsync());
        foreach (var table in DossierTables.Concat(["Documents", "Suppliers", "Products", "Cases", "Shipments"])) Assert.Equal(1, await Count(db, table));
    }

    private const string ReviewMigration = "20261013224510_ReviewedDossierHistory";
    private static readonly string[] ReviewTables = ["CaseRevisions", "CaseRevisionDocuments", "CaseRevisionEvidenceLinks", "ReviewRecords"];

    [Fact]
    public async Task Clean_database_has_review_history_tables_check_constraint_indexes_alternate_keys_and_restrict_foreign_keys()
    {
        await using var db = NewContext();
        Assert.False(db.Database.HasPendingModelChanges(), "The snapshot must match the DbContext.");
        await db.Database.MigrateAsync();
        Assert.Contains(ReviewMigration, await db.Database.GetAppliedMigrationsAsync());
        var tables = await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'").ToListAsync();
        Assert.Subset(tables.ToHashSet(), ReviewTables.ToHashSet());
        foreach (var column in new[] { "SectionHashes", "SnapshotJson" })
            Assert.Equal("jsonb", await db.Database.SqlQueryRaw<string>($"SELECT data_type AS \"Value\" FROM information_schema.columns WHERE table_name = 'CaseRevisions' AND column_name = '{column}'").SingleAsync());

        async Task<string> Index(string name) => await db.Database.SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = {0}", name).SingleAsync();
        var number = await Index("IX_CaseRevisions_EudrCaseId_Number"); Assert.Contains("UNIQUE", number); Assert.Contains("(\"EudrCaseId\", \"Number\")", number);
        var oneReview = await Index("IX_ReviewRecords_CaseRevisionId"); Assert.Contains("UNIQUE", oneReview);
        var timestamp = await Index("IX_ReviewRecords_EudrCaseId_ReviewedAtUtc"); Assert.DoesNotContain("UNIQUE", timestamp); Assert.Contains("(\"EudrCaseId\", \"ReviewedAtUtc\")", timestamp);
        var fingerprint = await Index("IX_CaseRevisions_Fingerprint"); Assert.DoesNotContain("UNIQUE", fingerprint);
        foreach (var name in new[] { "IX_CaseRevisionDocuments_CaseRevisionId_CaseDocumentId", "IX_CaseRevisionEvidenceLinks_CaseRevisionId_CaseEvidenceLinkId" }) Assert.Contains("UNIQUE", await Index(name));

        var constraints = await db.Database.SqlQueryRaw<string>("""
            SELECT conrelid::regclass::text || ' ' || contype::text || ' ' || conname || ' ' || pg_get_constraintdef(oid) AS "Value" FROM pg_constraint
            WHERE conrelid::regclass::text IN ('"CaseRevisions"', '"CaseRevisionDocuments"', '"CaseRevisionEvidenceLinks"', '"ReviewRecords"', '"CaseDossiers"', '"CaseLots"', '"Geolocations"', '"Submissions"',
                                                '"CaseApplicabilities"', '"CaseRecipientStatements"', '"CaseEvidenceLinks"', '"CaseMasterDataVersions"')
            """).ToListAsync();
        var check = Assert.Single(constraints, c => c.Contains(" c CK_CaseRevisions_DossierLotPair "));
        Assert.Contains("\"CaseDossierId\" IS NULL", check); Assert.Contains("\"CaseLotId\" IS NULL", check); Assert.Contains("\"CaseDossierId\" IS NOT NULL", check); Assert.Contains("\"CaseLotId\" IS NOT NULL", check);
        foreach (var key in new[] { "AK_CaseDossiers_OrganizationId_EudrCaseId_Id UNIQUE (\"OrganizationId\", \"EudrCaseId\", \"Id\")", "AK_CaseLots_OrganizationId_CaseDossierId_Id UNIQUE (\"OrganizationId\", \"CaseDossierId\", \"Id\")",
            "AK_Geolocations_OrganizationId_EudrCaseId_Id UNIQUE (\"OrganizationId\", \"EudrCaseId\", \"Id\")", "AK_Submissions_OrganizationId_EudrCaseId_Id UNIQUE (\"OrganizationId\", \"EudrCaseId\", \"Id\")",
            "AK_CaseApplicabilities_OrganizationId_EudrCaseId_Id UNIQUE (\"OrganizationId\", \"EudrCaseId\", \"Id\")", "AK_CaseRecipientStatements_OrganizationId_EudrCaseId_Id UNIQUE (\"OrganizationId\", \"EudrCaseId\", \"Id\")",
            "AK_CaseEvidenceLinks_OrganizationId_EudrCaseId_Id UNIQUE (\"OrganizationId\", \"EudrCaseId\", \"Id\")", "AK_CaseMasterDataVersions_OrganizationId_EudrCaseId_Id UNIQUE (\"OrganizationId\", \"EudrCaseId\", \"Id\")" })
            Assert.Contains(constraints, c => c.Contains(" u " + key));
        foreach (var fk in new[] { "(\"OrganizationId\", \"EudrCaseId\", \"CaseDossierId\") REFERENCES \"CaseDossiers\"(\"OrganizationId\", \"EudrCaseId\", \"Id\")", "(\"OrganizationId\", \"CaseDossierId\", \"CaseLotId\") REFERENCES \"CaseLots\"(\"OrganizationId\", \"CaseDossierId\", \"Id\")",
            "(\"OrganizationId\", \"EudrCaseId\", \"CaseApplicabilityId\") REFERENCES \"CaseApplicabilities\"(\"OrganizationId\", \"EudrCaseId\", \"Id\")", "(\"OrganizationId\", \"EudrCaseId\", \"CaseRecipientStatementId\") REFERENCES \"CaseRecipientStatements\"(\"OrganizationId\", \"EudrCaseId\", \"Id\")",
            "(\"OrganizationId\", \"EudrCaseId\", \"CaseMasterDataVersionId\") REFERENCES \"CaseMasterDataVersions\"(\"OrganizationId\", \"EudrCaseId\", \"Id\")", "(\"OrganizationId\", \"EudrCaseId\", \"GeoSubmissionId\") REFERENCES \"Geolocations\"(\"OrganizationId\", \"EudrCaseId\", \"Id\")",
            "(\"OrganizationId\", \"EudrCaseId\", \"SupplierSubmissionId\") REFERENCES \"Submissions\"(\"OrganizationId\", \"EudrCaseId\", \"Id\")", "(\"OrganizationId\", \"EudrCaseId\", \"CaseDocumentId\") REFERENCES \"Documents\"(\"OrganizationId\", \"EudrCaseId\", \"Id\")",
            "(\"OrganizationId\", \"EudrCaseId\", \"CaseEvidenceLinkId\") REFERENCES \"CaseEvidenceLinks\"(\"OrganizationId\", \"EudrCaseId\", \"Id\")", "(\"OrganizationId\", \"EudrCaseId\", \"CaseRevisionId\") REFERENCES \"CaseRevisions\"(\"OrganizationId\", \"EudrCaseId\", \"Id\")" })
            Assert.Contains(constraints, c => c.Contains(" f ") && c.Contains(fk));
        Assert.All(constraints.Where(c => c.Contains(" f ")), f => Assert.Contains("ON DELETE RESTRICT", f));
        // The migration is additive: earlier completeness tables and the case-owned FK indexes it must not drop are still there.
        foreach (var name in new[] { "IX_CaseLots_OrganizationId_CaseDossierId", "IX_CaseDossiers_OrganizationId_EudrCaseId", "IX_Geolocations_OrganizationId_EudrCaseId", "IX_Submissions_OrganizationId_EudrCaseId", "IX_CaseApplicabilities_OrganizationId_EudrCaseId",
            "IX_CaseRecipientStatements_OrganizationId_EudrCaseId", "IX_CaseMasterDataVersions_OrganizationId_EudrCaseId" })
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE indexname = {0}", name).SingleAsync());
    }

    [Fact]
    public async Task Upgrade_from_completeness_facts_keeps_every_row_and_creates_empty_review_tables_without_fabricating_anything()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        var seed = await SeedEvidenceAtInitialSchema(db);
        await db.GetService<IMigrator>().MigrateAsync(CompletenessMigration);
        await InsertDossierGraph(db, seed);
        await InsertCompletenessRow(db, seed, "OperatorProfiles"); await InsertCompletenessRow(db, seed, "CaseMasterDataVersions"); await InsertCompletenessRow(db, seed, "CaseApplicabilities");
        await InsertCompletenessRow(db, seed, "CaseRecipientStatements"); await InsertCompletenessRow(db, seed, "CaseEvidenceLinks");
        Assert.Equal(MigratedUpTo(CompletenessMigration), (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Empty(await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name IN ('CaseRevisions','CaseRevisionDocuments','CaseRevisionEvidenceLinks','ReviewRecords')").ToListAsync());

        await db.Database.MigrateAsync();

        Assert.Contains(ReviewMigration, await db.Database.GetAppliedMigrationsAsync());
        foreach (var table in new[] { "Documents", "Geolocations", "Submissions", "Cases", "Shipments", "Suppliers", "Products", "CaseDossiers", "CaseLots", "OperatorProfiles", "CaseMasterDataVersions", "CaseApplicabilities", "CaseRecipientStatements", "CaseEvidenceLinks" })
            Assert.Equal(1, await Count(db, table));
        foreach (var table in ReviewTables) Assert.Equal(0, await Count(db, table)); // no revisions, reviews, reviewers or approvals are fabricated
    }
    private static string[] MigratedUpTo(string last) => new[] { InitialMigration, RetainedEvidenceMigration, ProvenanceMigration, DossierMigration, CompletenessMigration }.TakeWhile(m => m != last).Append(last).ToArray();

    // Inserts one row of the named review-history table for the seeded case (a revision first where the row needs one).
    private static async Task InsertReviewRow(AppDbContext db, SeededCase seed, string category)
    {
        var org = seed.OrganizationId; var caseId = seed.CaseId; var revision = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync($"""
INSERT INTO "CaseRevisions" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","PolicyId","PolicyVersion","SnapshotSchemaVersion","FingerprintVersion","Fingerprint","SectionHashes","SnapshotJson") VALUES ('{revision}', now(), now(), '{org}', '{caseId}', 1, 'p', '1', 1, 1, 'f', '[]'::jsonb, '[]'::jsonb)
""");
        switch (category)
        {
            case "CaseRevisions": break;
            case "CaseRevisionDocuments":
                await db.Database.ExecuteSqlRawAsync($"""INSERT INTO "CaseRevisionDocuments" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","CaseRevisionId","CaseDocumentId") SELECT '{Guid.NewGuid()}', now(), now(), '{org}', '{caseId}', '{revision}', d."Id" FROM "Documents" d WHERE d."EudrCaseId" = '{caseId}'"""); break;
            case "CaseRevisionEvidenceLinks":
                await InsertCompletenessRow(db, seed, "CaseEvidenceLinks");
                await db.Database.ExecuteSqlRawAsync($"""INSERT INTO "CaseRevisionEvidenceLinks" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","CaseRevisionId","CaseEvidenceLinkId") SELECT '{Guid.NewGuid()}', now(), now(), '{org}', '{caseId}', '{revision}', l."Id" FROM "CaseEvidenceLinks" l WHERE l."EudrCaseId" = '{caseId}'"""); break;
            case "ReviewRecords":
                await db.Database.ExecuteSqlRawAsync($"""INSERT INTO "ReviewRecords" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","CaseRevisionId","Decision","Rationale","ReviewerUserId","ReviewedAtUtc","PolicyVersion") VALUES ('{Guid.NewGuid()}', now(), now(), '{org}', '{caseId}', '{revision}', 3, 'r', '{Guid.NewGuid()}', now(), '1')"""); break;
            default: throw new ArgumentOutOfRangeException(nameof(category));
        }
    }

    [Theory]
    [InlineData("CaseRevisions")]
    [InlineData("CaseRevisionDocuments")]
    [InlineData("CaseRevisionEvidenceLinks")]
    [InlineData("ReviewRecords")]
    public async Task Review_history_down_is_refused_for_every_table_and_leaves_schema_and_rows_intact(string category)
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        var seed = await SeedEvidenceAtInitialSchema(db);
        await db.Database.MigrateAsync();
        await InsertReviewRow(db, seed, category);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync(CompletenessMigration));
        Assert.Contains("Review history records exist; rollback would destroy history", failure.ToString());

        Assert.Contains(ReviewMigration, await db.Database.GetAppliedMigrationsAsync());
        var tables = await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'").ToListAsync();
        Assert.Subset(tables.ToHashSet(), ReviewTables.ToHashSet());
        Assert.Equal(1, await Count(db, category));
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM pg_constraint WHERE conname = 'AK_CaseDossiers_OrganizationId_EudrCaseId_Id'""").SingleAsync());
    }

    [Fact]
    public async Task Review_history_down_succeeds_when_empty_and_drops_only_its_own_objects()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        var seed = await SeedEvidenceAtInitialSchema(db);
        await db.Database.MigrateAsync();
        await InsertDossierGraph(db, seed);
        await InsertCompletenessRow(db, seed, "CaseApplicabilities"); await InsertCompletenessRow(db, seed, "CaseMasterDataVersions");
        await InsertReviewRow(db, seed, "ReviewRecords");
        await db.Database.ExecuteSqlRawAsync("""TRUNCATE TABLE "RiskMitigationMeasures", "RiskAssessmentFindings", "RiskAssessments", "ExternalDdsRecords", "CaseReopenings", "CaseClosures", "ReviewRecords", "CaseRevisionEvidenceLinks", "CaseRevisionDocuments", "CaseRevisions" """);

        await db.GetService<IMigrator>().MigrateAsync(CompletenessMigration);

        Assert.DoesNotContain(ReviewMigration, await db.Database.GetAppliedMigrationsAsync()); Assert.Contains(CompletenessMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name IN ('CaseRevisions','CaseRevisionDocuments','CaseRevisionEvidenceLinks','ReviewRecords')").ToListAsync());
        Assert.Empty(await db.Database.SqlQueryRaw<string>("""SELECT conname AS "Value" FROM pg_constraint WHERE conname IN ('AK_CaseDossiers_OrganizationId_EudrCaseId_Id','AK_CaseLots_OrganizationId_CaseDossierId_Id','AK_Geolocations_OrganizationId_EudrCaseId_Id','AK_Submissions_OrganizationId_EudrCaseId_Id','AK_CaseApplicabilities_OrganizationId_EudrCaseId_Id','AK_CaseRecipientStatements_OrganizationId_EudrCaseId_Id','AK_CaseEvidenceLinks_OrganizationId_EudrCaseId_Id','AK_CaseMasterDataVersions_OrganizationId_EudrCaseId_Id')""").ToListAsync());
        // The earlier schema, keys, indexes and rows are untouched.
        Assert.Equal(3, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM pg_constraint WHERE conname IN ('AK_CaseDossiers_OrganizationId_Id','AK_CaseLots_OrganizationId_Id','AK_Documents_OrganizationId_EudrCaseId_Id')""").SingleAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM pg_indexes WHERE indexname = 'IX_CaseLots_OrganizationId_CaseDossierId'""").SingleAsync());
        foreach (var table in new[] { "CaseDossiers", "CaseLots", "CaseApplicabilities", "CaseMasterDataVersions", "Documents", "Geolocations", "Submissions" }) Assert.Equal(1, await Count(db, table));
    }

    // ---- Case lifecycle v2 (CaseLifecycleV2) ---------------------------------------------------------------------------------------------
    private const string LifecycleMigration = "20261015014806_CaseLifecycleV2";
    private static readonly string[] LifecycleTables = ["CaseClosures", "CaseReopenings", "CaseArchivals"];
    private const string LegacyEvent = "LegacyCompletionInvalidatedByMigration";

    [Fact]
    public async Task Clean_database_has_lifecycle_tables_without_the_legacy_case_columns_and_with_unique_keys_indexes_and_restrict_foreign_keys()
    {
        await using var db = NewContext();
        Assert.False(db.Database.HasPendingModelChanges(), "The generated snapshot must match the DbContext.");
        await db.Database.MigrateAsync();
        Assert.Contains(LifecycleMigration, await db.Database.GetAppliedMigrationsAsync());
        var tables = await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'").ToListAsync();
        Assert.Subset(tables.ToHashSet(), LifecycleTables.ToHashSet());
        var caseColumns = await db.Database.SqlQueryRaw<string>("SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'Cases'").ToListAsync();
        Assert.DoesNotContain("CompletedAtUtc", caseColumns); Assert.DoesNotContain("ExternalDdsReference", caseColumns);
        Assert.Subset(caseColumns.ToHashSet(), new HashSet<string> { "Status", "SupplierSubmissionCompletedAtUtc", "InternalReviewCompletedAtUtc", "InternalNotes" });
        Assert.Equal(200, await db.Database.SqlQueryRaw<int>("SELECT character_maximum_length AS \"Value\" FROM information_schema.columns WHERE table_name = 'CaseClosures' AND column_name = 'ExternalDdsReference'").SingleAsync());
        Assert.Equal("NO", await db.Database.SqlQueryRaw<string>("SELECT is_nullable AS \"Value\" FROM information_schema.columns WHERE table_name = 'CaseReopenings' AND column_name = 'Reason'").SingleAsync());
        Assert.Equal("NO", await db.Database.SqlQueryRaw<string>("SELECT is_nullable AS \"Value\" FROM information_schema.columns WHERE table_name = 'CaseArchivals' AND column_name = 'Reason'").SingleAsync());
        Assert.Equal("YES", await db.Database.SqlQueryRaw<string>("SELECT is_nullable AS \"Value\" FROM information_schema.columns WHERE table_name = 'CaseArchivals' AND column_name = 'ArchivedByUserId'").SingleAsync());

        async Task<string> Index(string name) => await db.Database.SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = {0}", name).SingleAsync();
        var number = await Index("IX_CaseClosures_EudrCaseId_Number"); Assert.Contains("UNIQUE", number); Assert.Contains("(\"EudrCaseId\", \"Number\")", number);
        Assert.Contains("UNIQUE", await Index("IX_CaseClosures_ReviewRecordId")); // one review backs at most one closure
        Assert.Contains("UNIQUE", await Index("IX_CaseReopenings_CaseClosureId")); // a closure is reopened at most once
        var current = await Index("IX_CaseArchivals_EudrCaseId"); Assert.Contains("UNIQUE", current); Assert.Contains("\"UnarchivedAtUtc\" IS NULL", current); // at most one current archive period
        Assert.Contains("UNIQUE", await Index("IX_CaseArchivals_EudrCaseId_Number"));

        var constraints = await db.Database.SqlQueryRaw<string>("""
            SELECT conrelid::regclass::text || ' ' || contype::text || ' ' || conname || ' ' || pg_get_constraintdef(oid) AS "Value" FROM pg_constraint
            WHERE conrelid::regclass::text IN ('"CaseClosures"', '"CaseReopenings"', '"CaseArchivals"', '"ReviewRecords"')
            """).ToListAsync();
        Assert.Contains(constraints, c => c.Contains(" u AK_ReviewRecords_OrganizationId_EudrCaseId_Id UNIQUE (\"OrganizationId\", \"EudrCaseId\", \"Id\")"));
        Assert.Contains(constraints, c => c.Contains(" u AK_CaseClosures_OrganizationId_EudrCaseId_Id UNIQUE (\"OrganizationId\", \"EudrCaseId\", \"Id\")"));
        Assert.Contains(constraints, c => c.Contains(" f ") && c.Contains("(\"OrganizationId\", \"EudrCaseId\", \"ReviewRecordId\") REFERENCES \"ReviewRecords\"(\"OrganizationId\", \"EudrCaseId\", \"Id\")"));
        Assert.Contains(constraints, c => c.Contains(" f ") && c.Contains("(\"OrganizationId\", \"EudrCaseId\", \"CaseClosureId\") REFERENCES \"CaseClosures\"(\"OrganizationId\", \"EudrCaseId\", \"Id\")"));
        var foreignKeys = constraints.Where(c => c.Contains(" f ") && LifecycleTables.Any(t => c.StartsWith($"\"{t}\" "))).ToList();
        Assert.Equal(9, foreignKeys.Count); // closures: case, organization, review, risk assessment (Risk Assessment v1); reopenings: closure, case, organization; archivals: case, organization
        Assert.All(foreignKeys, f => Assert.Contains("ON DELETE RESTRICT", f));
    }

    private sealed record LegacyIds(Guid Org, Guid Supplier, Guid Product);
    private static async Task<LegacyIds> InsertLegacyOrganization(AppDbContext db)
    {
        var ids = new LegacyIds(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlRawAsync($"""
INSERT INTO "Organizations" ("Id","CreatedAtUtc","UpdatedAtUtc","Name") VALUES ('{ids.Org}', now(), now(), 'Legacy Org');
INSERT INTO "Suppliers" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","Name") VALUES ('{ids.Supplier}', now(), now(), '{ids.Org}', 'Supplier');
INSERT INTO "Products" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","Name","CnCode","CommodityType","IsActive") VALUES ('{ids.Product}', now(), now(), '{ids.Org}', 'Product', '1801', 0, true);
""");
        return ids;
    }

    // A case as the pre-lifecycle schema stored it: status integer (4 = legacy Completed, 5 = legacy Archived), completion timestamp, DDS reference, a sent request and six required requirements.
    private static async Task<Guid> InsertLegacyCase(AppDbContext db, LegacyIds ids, string reference, int status, DateTimeOffset? completedAt, string? ddsReference, bool requestSent, int[] requirementStatuses, DateTimeOffset updatedAt)
    {
        Guid shipment = Guid.NewGuid(), eudrCase = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Shipments" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","ReferenceNumber","SupplierId","ProductId","Quantity","Unit")
            VALUES ({shipment}, now(), now(), {ids.Org}, {reference}, {ids.Supplier}, {ids.Product}, 1, 0)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Cases" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","ShipmentId","Status","CompletedAtUtc","ExternalDdsReference")
            VALUES ({eudrCase}, now(), {updatedAt}, {ids.Org}, {shipment}, {status}, {completedAt}, {ddsReference})
            """);
        for (var type = 0; type < requirementStatuses.Length; type++)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Requirements" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Type","Title","IsRequired","Status")
                VALUES ({Guid.NewGuid()}, now(), now(), {ids.Org}, {eudrCase}, {type}, {"Requirement " + type}, true, {requirementStatuses[type]})
                """);
        if (requestSent)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "SupplierRequests" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","SupplierId","TokenHash","ExpiresAtUtc","SentAtUtc")
                VALUES ({Guid.NewGuid()}, now(), now(), {ids.Org}, {eudrCase}, {ids.Supplier}, {Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")}, now() + interval '30 days', now())
                """);
        return eudrCase;
    }

    [Fact]
    public async Task Upgrade_converts_legacy_completed_and_archived_cases_without_inventing_closures_and_preserves_the_legacy_completion_as_audit_evidence()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(ReviewMigration);
        var ids = await InsertLegacyOrganization(db);
        var completedB = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero); var completedE = new DateTimeOffset(2026, 3, 2, 11, 30, 0, TimeSpan.Zero);
        var archivedAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        int[] allAccepted = [2, 2, 2, 2, 2, 2];
        var open = await InsertLegacyCase(db, ids, "OPEN", 2, null, null, true, [1, 2, 2, 2, 2, 2], archivedAt);                    // not terminal: untouched
        var completedReady = await InsertLegacyCase(db, ids, "DONE-READY", 4, completedB, "DDS-B", true, allAccepted, archivedAt);  // Completed, everything accepted -> ReadyForExport
        var completedDraft = await InsertLegacyCase(db, ids, "DONE-DRAFT", 4, completedB, null, false, allAccepted, archivedAt);     // Completed, no request ever sent -> Draft
        var archivedReview = await InsertLegacyCase(db, ids, "ARCH-REVIEW", 5, null, null, true, [1, 2, 2, 2, 2, 2], archivedAt);    // Archived, never completed -> ReviewNeeded
        var archivedCompleted = await InsertLegacyCase(db, ids, "ARCH-DONE", 5, completedE, "DDS-E", true, [0, 2, 2, 2, 2, 2], archivedAt); // Completed, then archived -> WaitingForSupplier
        async Task<int> Scalar(string sql, params object[] args) => await db.Database.SqlQueryRaw<int>(sql, args).SingleAsync();

        await db.Database.MigrateAsync();

        Assert.Contains(LifecycleMigration, await db.Database.GetAppliedMigrationsAsync());
        // Statuses: the legacy Completed/Archived values are recalculated into an open status; nothing else is touched.
        async Task<int> Status(Guid id) => await Scalar("""SELECT "Status" AS "Value" FROM "Cases" WHERE "Id" = {0}""", id);
        Assert.Equal(2, await Status(open)); Assert.Equal(3, await Status(completedReady)); Assert.Equal(0, await Status(completedDraft));
        Assert.Equal(2, await Status(archivedReview)); Assert.Equal(1, await Status(archivedCompleted));
        Assert.Equal(1, await Scalar("""SELECT count(*)::int AS "Value" FROM "Cases" WHERE "Id" = {0} AND "InternalReviewCompletedAtUtc" IS NOT NULL""", completedReady)); // ReadyForExport carries its timestamp, like Recalculate
        Assert.Equal(0, await Scalar("""SELECT count(*)::int AS "Value" FROM "Cases" WHERE "Id" IN ({0}, {1}, {2}) AND "InternalReviewCompletedAtUtc" IS NOT NULL""", completedDraft, archivedReview, archivedCompleted));
        Assert.Equal(5, await Count(db, "Cases"));
        // No closure and no reopening is invented: legacy completions had no qualifying Owner review and no Owner reopened anything.
        Assert.Equal(0, await Count(db, "CaseClosures")); Assert.Equal(0, await Count(db, "CaseReopenings"));
        // Archive history: exactly the two legacy Archived cases, each one open period that records what is known and nothing more.
        Assert.Equal(2, await Count(db, "CaseArchivals"));
        foreach (var archived in new[] { archivedReview, archivedCompleted })
            Assert.Equal(1, await Scalar("""
                SELECT count(*)::int AS "Value" FROM "CaseArchivals" WHERE "EudrCaseId" = {0} AND "Number" = 1 AND "ArchivedByUserId" IS NULL AND "UnarchivedAtUtc" IS NULL
                AND "UnarchivedByUserId" IS NULL AND "UnarchiveReason" IS NULL AND "Reason" = 'Archived before case lifecycle v2.' AND "ArchivedAtUtc" = {1}
                """, archived, archivedAt));
        // Legacy completion evidence: one System event per case that had a completion (Completed, or Completed then Archived), with the old values preserved.
        Assert.Equal(3, await Scalar("""SELECT count(*)::int AS "Value" FROM "AuditEvents" WHERE "EventType" = {0}""", LegacyEvent));
        foreach (var notCompleted in new[] { open, archivedReview })
            Assert.Equal(0, await Scalar("""SELECT count(*)::int AS "Value" FROM "AuditEvents" WHERE "EudrCaseId" = {0} AND "EventType" = {1}""", notCompleted, LegacyEvent));
        async Task<string> Metadata(Guid id, string key) => await db.Database.SqlQueryRaw<string>("""SELECT COALESCE("MetadataJson"->>{1}, '<null>') AS "Value" FROM "AuditEvents" WHERE "EudrCaseId" = {0} AND "EventType" = {2}""", id, key, LegacyEvent).SingleAsync();
        Assert.Equal("DDS-B", await Metadata(completedReady, "legacyExternalDdsReference")); Assert.Equal("Completed", await Metadata(completedReady, "legacyStatus"));
        Assert.Equal("<null>", await Metadata(completedDraft, "legacyExternalDdsReference")); Assert.Equal("Completed", await Metadata(completedDraft, "legacyStatus"));
        Assert.Equal("DDS-E", await Metadata(archivedCompleted, "legacyExternalDdsReference")); Assert.Equal("Archived", await Metadata(archivedCompleted, "legacyStatus"));
        Assert.Equal(1, await Scalar("""SELECT count(*)::int AS "Value" FROM "AuditEvents" WHERE "EudrCaseId" = {0} AND "EventType" = {2} AND ("MetadataJson"->>'legacyCompletedAtUtc')::timestamptz = {1}""", completedReady, completedB, LegacyEvent));
        Assert.Equal(1, await Scalar("""SELECT count(*)::int AS "Value" FROM "AuditEvents" WHERE "EudrCaseId" = {0} AND "EventType" = {2} AND ("MetadataJson"->>'legacyCompletedAtUtc')::timestamptz = {1}""", archivedCompleted, completedE, LegacyEvent));
        Assert.Equal(3, await Scalar("""SELECT count(*)::int AS "Value" FROM "AuditEvents" WHERE "EventType" = {0} AND "ActorType" = 2 AND "ActorUserId" IS NULL AND "SupplierRequestId" IS NULL""", LegacyEvent)); // System events
        // The legacy columns are gone and the other rows are untouched.
        Assert.Equal(0, await Scalar("""SELECT count(*)::int AS "Value" FROM information_schema.columns WHERE table_name = 'Cases' AND column_name IN ('CompletedAtUtc', 'ExternalDdsReference')"""));
        Assert.Equal(30, await Count(db, "Requirements")); Assert.Equal(4, await Count(db, "SupplierRequests"));
    }

    [Fact]
    public async Task Upgrade_of_a_database_without_terminal_cases_creates_empty_lifecycle_tables_and_no_events()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(ReviewMigration);
        var ids = await InsertLegacyOrganization(db);
        await InsertLegacyCase(db, ids, "OPEN-ONLY", 1, null, null, true, [0, 2, 2, 2, 2, 2], DateTimeOffset.UtcNow);
        await db.Database.MigrateAsync();
        foreach (var table in LifecycleTables) Assert.Equal(0, await Count(db, table));
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "AuditEvents" WHERE "EventType" = {0}""", LegacyEvent).SingleAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""SELECT "Status" AS "Value" FROM "Cases" """).SingleAsync());
    }

    // Inserts one row of the named lifecycle table for the seeded case (the review and closure it needs first where the row needs one).
    private static async Task InsertLifecycleRow(AppDbContext db, SeededCase seed, string category)
    {
        var org = seed.OrganizationId; var caseId = seed.CaseId;
        if (category == "CaseArchivals")
        {
            await db.Database.ExecuteSqlRawAsync($"""INSERT INTO "CaseArchivals" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","ArchivedAtUtc","Reason") VALUES ('{Guid.NewGuid()}', now(), now(), '{org}', '{caseId}', 1, now(), 'r')""");
            return;
        }
        await InsertReviewRow(db, seed, "ReviewRecords");
        var closure = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync($"""INSERT INTO "CaseClosures" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","Outcome","ReviewRecordId","ClosedByUserId","ClosedAtUtc") SELECT '{closure}', now(), now(), '{org}', '{caseId}', 1, 0, "Id", '{Guid.NewGuid()}', now() FROM "ReviewRecords" WHERE "EudrCaseId" = '{caseId}'""");
        if (category == "CaseReopenings")
            await db.Database.ExecuteSqlRawAsync($"""INSERT INTO "CaseReopenings" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","CaseClosureId","Reason","ReopenedByUserId","ReopenedAtUtc") VALUES ('{Guid.NewGuid()}', now(), now(), '{org}', '{caseId}', '{closure}', 'r', '{Guid.NewGuid()}', now())""");
    }

    [Theory]
    [InlineData("CaseClosures")]
    [InlineData("CaseReopenings")]
    [InlineData("CaseArchivals")]
    public async Task Lifecycle_down_is_refused_for_every_table_and_leaves_schema_and_rows_intact(string category)
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        var seed = await SeedEvidenceAtInitialSchema(db);
        await db.Database.MigrateAsync();
        await InsertLifecycleRow(db, seed, category);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync(ReviewMigration));
        Assert.Contains("Case lifecycle records exist; rollback would destroy history", failure.ToString());

        Assert.Contains(LifecycleMigration, await db.Database.GetAppliedMigrationsAsync());
        var tables = await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'").ToListAsync();
        Assert.Subset(tables.ToHashSet(), LifecycleTables.ToHashSet());
        Assert.Equal(1, await Count(db, category));
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM information_schema.columns WHERE table_name = 'Cases' AND column_name IN ('CompletedAtUtc', 'ExternalDdsReference')""").SingleAsync());
    }

    [Fact]
    public async Task Lifecycle_down_succeeds_when_empty_restoring_the_empty_legacy_columns_and_keeping_the_preserved_audit_evidence()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        var seed = await SeedEvidenceAtInitialSchema(db);
        await db.Database.MigrateAsync();
        await InsertReviewRow(db, seed, "ReviewRecords");
        await db.Database.ExecuteSqlRawAsync($"""INSERT INTO "AuditEvents" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","ActorType","EventType","Description") VALUES ('{Guid.NewGuid()}', now(), now(), '{seed.OrganizationId}', '{seed.CaseId}', 2, '{LegacyEvent}', 'preserved')""");

        await db.GetService<IMigrator>().MigrateAsync(ReviewMigration);

        Assert.DoesNotContain(LifecycleMigration, await db.Database.GetAppliedMigrationsAsync()); Assert.Contains(ReviewMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name IN ('CaseClosures','CaseReopenings','CaseArchivals')").ToListAsync());
        var columns = await db.Database.SqlQueryRaw<string>("SELECT column_name || ':' || is_nullable AS \"Value\" FROM information_schema.columns WHERE table_name = 'Cases' AND column_name IN ('CompletedAtUtc', 'ExternalDdsReference')").ToListAsync();
        Assert.Equal(["CompletedAtUtc:YES", "ExternalDdsReference:YES"], columns.Order().ToArray()); // restored empty: values live only in the preserved audit events
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM pg_constraint WHERE conname = 'AK_ReviewRecords_OrganizationId_EudrCaseId_Id'""").SingleAsync());
        Assert.Equal(1, await Count(db, "ReviewRecords")); Assert.Equal(1, await Count(db, "Cases"));
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "AuditEvents" WHERE "EventType" = {0}""", LegacyEvent).SingleAsync());
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

    // ---- External DDS Record v1 (ExternalDdsRecord) -----------------------------------------------------------------------------------------
    private const string ExternalDdsMigration = "20261015203340_ExternalDdsRecord";

    [Fact]
    public async Task Clean_database_has_the_external_dds_table_with_partial_unique_indexes_check_constraints_and_restrict_tenant_aware_foreign_keys()
    {
        await using var db = NewContext();
        Assert.False(db.Database.HasPendingModelChanges(), "The generated snapshot must match the DbContext.");
        await db.Database.MigrateAsync();
        Assert.Contains(ExternalDdsMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        async Task<string> Index(string name) => await db.Database.SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = {0}", name).SingleAsync();
        var current = await Index("IX_ExternalDdsRecords_EudrCaseId"); Assert.Contains("UNIQUE", current); Assert.Contains("\"SupersededAtUtc\" IS NULL", current); // at most one current record per case
        Assert.Contains("UNIQUE", await Index("IX_ExternalDdsRecords_EudrCaseId_Number"));
        var supersedes = await Index("IX_ExternalDdsRecords_SupersedesExternalDdsRecordId"); Assert.Contains("UNIQUE", supersedes); Assert.Contains("IS NOT NULL", supersedes); // a record is superseded at most once
        var constraints = await db.Database.SqlQueryRaw<string>("""
            SELECT contype::text || ' ' || conname || ' ' || pg_get_constraintdef(oid) AS "Value" FROM pg_constraint WHERE conrelid = '"ExternalDdsRecords"'::regclass
            """).ToListAsync();
        var foreignKeys = constraints.Where(x => x.StartsWith("f ")).ToList();
        Assert.Equal(5, foreignKeys.Count); Assert.All(foreignKeys, x => Assert.Contains("ON DELETE RESTRICT", x));
        foreach (var target in new[] { "REFERENCES \"ReviewRecords\"(\"OrganizationId\", \"EudrCaseId\", \"Id\")", "REFERENCES \"Documents\"(\"OrganizationId\", \"EudrCaseId\", \"Id\")", "REFERENCES \"ExternalDdsRecords\"(\"OrganizationId\", \"EudrCaseId\", \"Id\")", "REFERENCES \"Cases\"(\"OrganizationId\", \"Id\")" })
            Assert.Contains(foreignKeys, x => x.Contains(target));
        Assert.Equal(2, constraints.Count(x => x.StartsWith("c ")));
        Assert.Equal(["ReferenceNumber:NO", "VerificationNumber:NO"], (await db.Database.SqlQueryRaw<string>("SELECT column_name || ':' || is_nullable AS \"Value\" FROM information_schema.columns WHERE table_name = 'ExternalDdsRecords' AND column_name IN ('ReferenceNumber', 'VerificationNumber')").ToListAsync()).Order().ToArray());
    }

    [Fact]
    public async Task The_external_dds_migration_is_purely_additive_over_existing_lifecycle_data_and_its_down_drops_only_its_table()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        var seed = await SeedEvidenceAtInitialSchema(db);
        await db.GetService<IMigrator>().MigrateAsync(LifecycleMigration);
        await InsertReviewRow(db, seed, "ReviewRecords");
        Assert.Empty(await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name = 'ExternalDdsRecords'").ToListAsync());

        await db.Database.MigrateAsync();
        Assert.Equal(0, await Count(db, "ExternalDdsRecords")); Assert.Equal(1, await Count(db, "ReviewRecords")); Assert.Equal(1, await Count(db, "Cases"));

        await db.GetService<IMigrator>().MigrateAsync(LifecycleMigration);
        Assert.DoesNotContain(ExternalDdsMigration, await db.Database.GetAppliedMigrationsAsync()); Assert.Contains(LifecycleMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name = 'ExternalDdsRecords'").ToListAsync());
        Assert.Equal(1, await Count(db, "ReviewRecords")); Assert.Equal(1, await Count(db, "Cases"));
    }

    [Fact]
    public async Task External_dds_down_is_refused_while_a_record_exists_and_leaves_the_table_and_row_intact_and_succeeds_when_empty()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        var seed = await SeedEvidenceAtInitialSchema(db);
        await db.GetService<IMigrator>().MigrateAsync(LifecycleMigration);
        await InsertReviewRow(db, seed, "ReviewRecords");
        await db.GetService<IMigrator>().MigrateAsync(ExternalDdsMigration);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ExternalDdsRecords" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","ReviewId","ReferenceNumber","VerificationNumber","SubmittedAtUtc","RecordedByUserId")
            SELECT {Guid.NewGuid()}, now(), now(), r."OrganizationId", r."EudrCaseId", 1, r."Id", 'REF-1', 'VERIF-FIXTURE', now(), {Guid.NewGuid()} FROM "ReviewRecords" r LIMIT 1
            """);
        Assert.Equal(1, await Count(db, "ExternalDdsRecords"));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync(LifecycleMigration));
        Assert.Contains("External DDS records exist; rollback would destroy history", failure.ToString());
        Assert.Contains(ExternalDdsMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(1, await Count(db, "ExternalDdsRecords"));
        Assert.Equal("REF-1", await db.Database.SqlQueryRaw<string>("""SELECT "ReferenceNumber" AS "Value" FROM "ExternalDdsRecords" """).SingleAsync());

        // With no record the same rollback succeeds.
        await db.Database.ExecuteSqlRawAsync("""TRUNCATE TABLE "ExternalDdsRecords" """);
        await db.GetService<IMigrator>().MigrateAsync(LifecycleMigration);
        Assert.DoesNotContain(ExternalDdsMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name = 'ExternalDdsRecords'").ToListAsync());
        Assert.Equal(1, await Count(db, "ReviewRecords"));
    }

#pragma warning disable EF1002 // Test-only SQL built from constants, test parameters and locally generated GUIDs; no external input.
    // ---- Post-completion Concern & Corrective Action v1 --------------------------------------------------------------------------------------
    private const string ConcernMigration = "20261015224634_PostCompletionConcerns";
    private static readonly string[] ConcernTables = ["CaseConcerns", "ConcernActions", "ConcernNotifications", "ConcernResolutions"];

    [Fact]
    public async Task Clean_database_has_the_concern_tables_with_unique_keys_nonblank_checks_and_restrict_tenant_aware_foreign_keys()
    {
        await using var db = NewContext();
        Assert.False(db.Database.HasPendingModelChanges(), "The generated snapshot must match the DbContext.");
        await db.Database.MigrateAsync();
        Assert.Contains(ConcernMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        async Task<string> Index(string name) => await db.Database.SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = {0}", name).SingleAsync();
        Assert.Contains("UNIQUE", await Index("IX_CaseConcerns_EudrCaseId_Number")); // per-case concern number
        Assert.Contains("UNIQUE", await Index("IX_ConcernResolutions_ConcernId")); // one resolution per concern
        async Task<List<string>> Constraints(string table) => await db.Database.SqlQueryRaw<string>($"SELECT contype::text || ' ' || conname || ' ' || pg_get_constraintdef(oid) AS \"Value\" FROM pg_constraint WHERE conrelid = '\"{table}\"'::regclass").ToListAsync();
        foreach (var (table, foreignKeys, checks) in new[] { ("CaseConcerns", 2, 1), ("ConcernActions", 4, 1), ("ConcernNotifications", 5, 1), ("ConcernResolutions", 3, 1) })
        {
            var constraints = await Constraints(table);
            var fks = constraints.Where(x => x.StartsWith("f ")).ToList();
            Assert.Equal(foreignKeys, fks.Count); Assert.All(fks, x => Assert.Contains("ON DELETE RESTRICT", x));
            Assert.Equal(checks, constraints.Count(x => x.StartsWith("c ")));
        }
        var notifications = await Constraints("ConcernNotifications");
        Assert.Contains(notifications, x => x.Contains("REFERENCES \"CaseRecipients\"(\"OrganizationId\", \"Id\")")); // the immutable recipient row
        Assert.Contains(notifications, x => x.Contains("REFERENCES \"Documents\"(\"OrganizationId\", \"EudrCaseId\", \"Id\")")); // evidence of the row's own case
        Assert.Contains(notifications, x => x.Contains("REFERENCES \"CaseConcerns\"(\"OrganizationId\", \"EudrCaseId\", \"Id\")")); // concern of the row's own case
    }

    [Fact]
    public async Task The_concern_migration_is_purely_additive_over_existing_data_and_its_down_drops_only_its_tables()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        await SeedEvidenceAtInitialSchema(db);
        await db.GetService<IMigrator>().MigrateAsync(ExternalDdsMigration);
        Assert.Empty(await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name = 'CaseConcerns'").ToListAsync());

        await db.Database.MigrateAsync();
        foreach (var table in ConcernTables) Assert.Equal(0, await Count(db, table));
        Assert.Equal(1, await Count(db, "Cases")); Assert.Equal(1, await Count(db, "Documents"));

        await db.GetService<IMigrator>().MigrateAsync(ExternalDdsMigration);
        Assert.DoesNotContain(ConcernMigration, await db.Database.GetAppliedMigrationsAsync()); Assert.Contains(ExternalDdsMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name IN ('CaseConcerns','ConcernActions','ConcernNotifications','ConcernResolutions')").ToListAsync());
        Assert.Equal(1, await Count(db, "Cases")); Assert.Equal(1, await Count(db, "Documents"));
    }

    // Every scenario holds a concern (the parent of the other three); the named table is the one that additionally holds a row, and any such row blocks the rollback.
    [Theory]
    [InlineData("CaseConcerns")]
    [InlineData("ConcernActions")]
    [InlineData("ConcernNotifications")]
    [InlineData("ConcernResolutions")]
    public async Task Concern_down_is_refused_while_any_concern_row_exists_and_succeeds_when_empty(string populated)
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        var seed = await SeedEvidenceAtInitialSchema(db);
        await db.GetService<IMigrator>().MigrateAsync(ConcernMigration);
        var org = seed.OrganizationId; var caseId = seed.CaseId; var concern = Guid.NewGuid(); var statement = Guid.NewGuid(); var recipient = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync($"""
INSERT INTO "CaseConcerns" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","Category","Summary","Details","RaisedAtUtc","RecordedByUserId") VALUES ('{concern}', now(), now(), '{org}', '{caseId}', 1, 4, 'S', 'D', now(), '{Guid.NewGuid()}');
INSERT INTO "CaseRecipientStatements" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","Resolution") VALUES ('{statement}', now(), now(), '{org}', '{caseId}', 1, 0);
INSERT INTO "CaseRecipients" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","CaseRecipientStatementId","Name","PostalAddress","Email") VALUES ('{recipient}', now(), now(), '{org}', '{statement}', 'R', 'A', 'r@example.test');
""");
        switch (populated)
        {
            case "ConcernActions":
                await db.Database.ExecuteSqlRawAsync($"""INSERT INTO "ConcernActions" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","ConcernId","Description","PerformedAtUtc","RecordedByUserId") VALUES ('{Guid.NewGuid()}', now(), now(), '{org}', '{caseId}', '{concern}', 'd', now(), '{Guid.NewGuid()}')"""); break;
            case "ConcernNotifications":
                await db.Database.ExecuteSqlRawAsync($"""INSERT INTO "ConcernNotifications" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","ConcernId","CaseRecipientId","Channel","NotifiedAtUtc","RecordedByUserId") VALUES ('{Guid.NewGuid()}', now(), now(), '{org}', '{caseId}', '{concern}', '{recipient}', 0, now(), '{Guid.NewGuid()}')"""); break;
            case "ConcernResolutions":
                await db.Database.ExecuteSqlRawAsync($"""INSERT INTO "ConcernResolutions" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","ConcernId","Outcome","Rationale","ResolvedByUserId","ResolvedAtUtc") VALUES ('{Guid.NewGuid()}', now(), now(), '{org}', '{caseId}', '{concern}', 0, 'r', '{Guid.NewGuid()}', now())"""); break;
        }

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync(ExternalDdsMigration));
        Assert.Contains("Concern records exist; rollback would destroy history", failure.ToString());
        Assert.Contains(ConcernMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(1, await Count(db, "CaseConcerns"));
        if (populated != "CaseConcerns") Assert.Equal(1, await Count(db, populated));
        Assert.Equal("S", await db.Database.SqlQueryRaw<string>("""SELECT "Summary" AS "Value" FROM "CaseConcerns" """).SingleAsync());

        // With no concern record the same rollback succeeds.
        await db.Database.ExecuteSqlRawAsync("""TRUNCATE TABLE "ConcernResolutions", "ConcernNotifications", "ConcernActions", "CaseConcerns" """);
        await db.GetService<IMigrator>().MigrateAsync(ExternalDdsMigration);
        Assert.DoesNotContain(ConcernMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name IN ('CaseConcerns','ConcernActions','ConcernNotifications','ConcernResolutions')").ToListAsync());
        Assert.Equal(1, await Count(db, "CaseRecipients"));
    }

    // ---- Risk Assessment & Mitigation v1 ---------------------------------------------------------------------------------------------------
    private const string RiskMigration = "20261016000117_RiskAssessmentAndMitigation";
    private static readonly string[] RiskTables = ["RiskAssessments", "RiskAssessmentFindings", "RiskMitigationMeasures"];

    [Fact]
    public async Task Clean_database_has_the_risk_tables_the_closure_consistency_foreign_key_and_the_backstop_indexes_and_checks()
    {
        await using var db = NewContext();
        Assert.False(db.Database.HasPendingModelChanges(), "The generated snapshot must match the DbContext.");
        await db.Database.MigrateAsync();
        Assert.Contains(RiskMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        async Task<string> Index(string name) => await db.Database.SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = {0}", name).SingleAsync();
        Assert.Contains("UNIQUE", await Index("IX_RiskAssessments_EudrCaseId_Number")); // per-case assessment number
        Assert.Contains("UNIQUE", await Index("IX_RiskMitigationMeasures_EudrCaseId_Number")); // per-case measure number
        var finding = await Index("IX_RiskAssessmentFindings_RiskAssessmentId_CriterionCode"); Assert.Contains("UNIQUE", finding); Assert.Contains("(\"RiskAssessmentId\", \"CriterionCode\")", finding); // one finding per criterion
        var backed = await Index("IX_CaseClosures_RiskAssessmentId"); Assert.Contains("UNIQUE", backed); Assert.Contains("\"RiskAssessmentId\" IS NOT NULL", backed); // an assessment backs at most one closure
        async Task<List<string>> Constraints(string table) => await db.Database.SqlQueryRaw<string>($"SELECT contype::text || ' ' || conname || ' ' || pg_get_constraintdef(oid) AS \"Value\" FROM pg_constraint WHERE conrelid = '\"{table}\"'::regclass").ToListAsync();
        foreach (var (table, foreignKeys, checks) in new[] { ("RiskAssessments", 3, 2), ("RiskAssessmentFindings", 3, 2), ("RiskMitigationMeasures", 4, 1) })
        {
            var constraints = await Constraints(table);
            var fks = constraints.Where(x => x.StartsWith("f ")).ToList();
            Assert.Equal(foreignKeys, fks.Count); Assert.All(fks, x => Assert.Contains("ON DELETE RESTRICT", x));
            Assert.Equal(checks, constraints.Count(x => x.StartsWith("c ")));
        }
        var assessments = await Constraints("RiskAssessments");
        Assert.Contains(assessments, x => x.StartsWith("u ") && x.Contains("UNIQUE (\"OrganizationId\", \"EudrCaseId\", \"Id\")"));
        Assert.Contains(assessments, x => x.StartsWith("u ") && x.Contains("UNIQUE (\"OrganizationId\", \"EudrCaseId\", \"ReviewRecordId\", \"Id\")")); // the principal of the closure FK
        Assert.Contains(assessments, x => x.StartsWith("f ") && x.Contains("FOREIGN KEY (\"OrganizationId\", \"EudrCaseId\", \"ReviewRecordId\") REFERENCES \"ReviewRecords\"(\"OrganizationId\", \"EudrCaseId\", \"Id\")"));
        var closures = await Constraints("CaseClosures");
        Assert.Contains(closures, x => x.StartsWith("f ") && x.Contains("FOREIGN KEY (\"OrganizationId\", \"EudrCaseId\", \"ReviewRecordId\", \"RiskAssessmentId\") REFERENCES \"RiskAssessments\"(\"OrganizationId\", \"EudrCaseId\", \"ReviewRecordId\", \"Id\") ON DELETE RESTRICT"));
        Assert.Contains(closures, x => x.StartsWith("c ") && x.Contains("CK_CaseClosures_RiskAssessmentOutcome")); // only ExternalDdsCompleted carries an assessment
        var measures = await Constraints("RiskMitigationMeasures");
        Assert.Contains(measures, x => x.Contains("REFERENCES \"Documents\"(\"OrganizationId\", \"EudrCaseId\", \"Id\")")); // evidence of the row's own case
    }

    [Fact]
    public async Task The_risk_migration_is_purely_additive_over_existing_data_and_its_down_drops_only_its_objects()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        await SeedEvidenceAtInitialSchema(db);
        await db.GetService<IMigrator>().MigrateAsync(ConcernMigration);
        Assert.Empty(await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name IN ('RiskAssessments','RiskAssessmentFindings','RiskMitigationMeasures')").ToListAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'CaseClosures' AND column_name = 'RiskAssessmentId'").SingleAsync());

        await db.Database.MigrateAsync(); // no backfill: nothing is invented for any existing case
        foreach (var table in RiskTables) Assert.Equal(0, await Count(db, table));
        Assert.Equal(1, await Count(db, "Cases")); Assert.Equal(1, await Count(db, "Documents"));
        Assert.Equal("YES", await db.Database.SqlQueryRaw<string>("SELECT is_nullable AS \"Value\" FROM information_schema.columns WHERE table_name = 'CaseClosures' AND column_name = 'RiskAssessmentId'").SingleAsync());

        await db.GetService<IMigrator>().MigrateAsync(ConcernMigration);
        Assert.DoesNotContain(RiskMigration, await db.Database.GetAppliedMigrationsAsync()); Assert.Contains(ConcernMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name IN ('RiskAssessments','RiskAssessmentFindings','RiskMitigationMeasures')").ToListAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'CaseClosures' AND column_name = 'RiskAssessmentId'").SingleAsync());
        Assert.Contains("(\"OrganizationId\", \"EudrCaseId\", \"ReviewRecordId\")", await db.Database.SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'IX_CaseClosures_OrganizationId_EudrCaseId_ReviewRecordId'").SingleAsync()); // the earlier index is back
        Assert.Equal(1, await Count(db, "Cases")); Assert.Equal(1, await Count(db, "Documents"));
    }

    // Every scenario holds an assessment (the parent of the other rows and of a closure's reference); the named case is what additionally exists, and any retained risk row blocks the rollback.
    [Theory]
    [InlineData("RiskAssessments")]
    [InlineData("RiskAssessmentFindings")]
    [InlineData("RiskMitigationMeasures")]
    [InlineData("ClosureReference")]
    public async Task Risk_down_is_refused_while_any_risk_row_or_closure_reference_exists_and_succeeds_when_empty(string populated)
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        var seed = await SeedEvidenceAtInitialSchema(db);
        await db.GetService<IMigrator>().MigrateAsync(RiskMigration);
        await InsertReviewRow(db, seed, "ReviewRecords");
        var org = seed.OrganizationId; var caseId = seed.CaseId; var assessment = Guid.NewGuid();
        var review = await db.Database.SqlQueryRaw<Guid>("""SELECT "Id" AS "Value" FROM "ReviewRecords" """).SingleAsync();
        await db.Database.ExecuteSqlRawAsync($"""
INSERT INTO "RiskAssessments" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","ReviewRecordId","CriteriaCatalogueId","CriteriaCatalogueVersion","Conclusion","Rationale","AssessedByUserId","AssessedAtUtc") VALUES ('{assessment}', now(), now(), '{org}', '{caseId}', 1, '{review}', 'provena-pilot-risk-criteria', '1', 0, 'Reasoned.', '{Guid.NewGuid()}', now())
""");
        switch (populated)
        {
            case "RiskAssessmentFindings":
                await db.Database.ExecuteSqlRawAsync($"""INSERT INTO "RiskAssessmentFindings" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","RiskAssessmentId","CriterionCode","Finding") VALUES ('{Guid.NewGuid()}', now(), now(), '{org}', '{caseId}', '{assessment}', 'EVIDENCE_RELIABILITY', 0)"""); break;
            case "RiskMitigationMeasures":
                await db.Database.ExecuteSqlRawAsync($"""INSERT INTO "RiskMitigationMeasures" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","RiskAssessmentId","Description","PerformedAtUtc","RecordedByUserId") VALUES ('{Guid.NewGuid()}', now(), now(), '{org}', '{caseId}', 1, '{assessment}', 'd', now(), '{Guid.NewGuid()}')"""); break;
            case "ClosureReference":
                await db.Database.ExecuteSqlRawAsync($"""INSERT INTO "CaseClosures" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","Outcome","ReviewRecordId","RiskAssessmentId","ClosedByUserId","ClosedAtUtc") VALUES ('{Guid.NewGuid()}', now(), now(), '{org}', '{caseId}', 1, 0, '{review}', '{assessment}', '{Guid.NewGuid()}', now())"""); break;
        }

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync(ConcernMigration));
        Assert.Contains("Risk records exist; rollback would destroy history", failure.ToString());
        Assert.Contains(RiskMigration, await db.Database.GetAppliedMigrationsAsync()); // the schema and the retained rows survive the refusal
        Assert.Equal(1, await Count(db, "RiskAssessments"));
        if (populated is "RiskAssessmentFindings" or "RiskMitigationMeasures") Assert.Equal(1, await Count(db, populated));
        if (populated == "ClosureReference") Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "CaseClosures" WHERE "RiskAssessmentId" IS NOT NULL""").SingleAsync());
        Assert.Equal("Reasoned.", await db.Database.SqlQueryRaw<string>("""SELECT "Rationale" AS "Value" FROM "RiskAssessments" """).SingleAsync());

        // With no risk record and no closure the same rollback succeeds.
        await db.Database.ExecuteSqlRawAsync("""TRUNCATE TABLE "CaseReopenings", "CaseClosures", "RiskMitigationMeasures", "RiskAssessmentFindings", "RiskAssessments" """);
        await db.GetService<IMigrator>().MigrateAsync(ConcernMigration);
        Assert.DoesNotContain(RiskMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name IN ('RiskAssessments','RiskAssessmentFindings','RiskMitigationMeasures')").ToListAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'CaseClosures' AND column_name = 'RiskAssessmentId'").SingleAsync());
        Assert.Equal(1, await Count(db, "ReviewRecords")); Assert.Equal(1, await Count(db, "Cases"));
    }

    // ---- Source Quantity Allocation v1 ------------------------------------------------------------------------------------------------------
    private const string AllocationMigration = "20261022221125_SourceQuantityAllocation";
    private const string AllocationColumn = """SELECT count(*)::int AS "Value" FROM information_schema.columns WHERE table_name = 'ContributionPlots' AND column_name = 'AllocatedQuantity'""";

    [Fact]
    public async Task Clean_database_has_the_nullable_numeric_allocation_column_and_a_positive_only_check()
    {
        await using var db = NewContext();
        Assert.False(db.Database.HasPendingModelChanges(), "The generated snapshot must match the DbContext.");
        await db.Database.MigrateAsync();
        Assert.Contains(AllocationMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var column = await db.Database.SqlQueryRaw<string>("""SELECT data_type || ' ' || numeric_precision || ',' || numeric_scale || ' nullable=' || is_nullable || ' default=' || coalesce(column_default, 'none') AS "Value" FROM information_schema.columns WHERE table_name = 'ContributionPlots' AND column_name = 'AllocatedQuantity'""").SingleAsync();
        Assert.Equal("numeric 20,6 nullable=YES default=none", column);
        var check = await db.Database.SqlQueryRaw<string>("""SELECT pg_get_constraintdef(oid) AS "Value" FROM pg_constraint WHERE conname = 'CK_ContributionPlots_AllocatedQuantityPositive' AND conrelid = '"ContributionPlots"'::regclass""").SingleAsync();
        Assert.Contains("AllocatedQuantity", check); Assert.Contains("IS NULL", check); Assert.Contains("> (0)::numeric", check);
        // No other change: the existing indexes and composite keys of ContributionPlots are untouched.
        Assert.Equal(["IX_ContributionPlots_LotContributionId_ProductionPlotId"], await db.Database.SqlQueryRaw<string>("""SELECT indexname AS "Value" FROM pg_indexes WHERE tablename = 'ContributionPlots' AND indexdef LIKE '%UNIQUE%' AND indexname NOT LIKE 'PK_%' AND indexname NOT LIKE 'AK_%'""").ToListAsync());
    }

    [Fact]
    public async Task The_allocation_check_rejects_zero_and_negative_values_and_accepts_null_and_positive_ones()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        var seed = await SeedEvidenceAtInitialSchema(db);
        await db.Database.MigrateAsync();
        await InsertDossierGraph(db, seed);
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "ContributionPlots" WHERE "AllocatedQuantity" IS NULL""").SingleAsync()); // a plot link without the column is null
        foreach (var bad in new[] { "0", "-1", "-0.000001" })
        {
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync($"""UPDATE "ContributionPlots" SET "AllocatedQuantity" = {bad}"""));
            Assert.Contains("CK_ContributionPlots_AllocatedQuantityPositive", failure.ToString());
        }
        await db.Database.ExecuteSqlRawAsync("""UPDATE "ContributionPlots" SET "AllocatedQuantity" = 0.000001""");
        Assert.Equal(0.000001m, await db.Database.SqlQueryRaw<decimal>("""SELECT "AllocatedQuantity" AS "Value" FROM "ContributionPlots" """).SingleAsync());
        await db.Database.ExecuteSqlRawAsync("""UPDATE "ContributionPlots" SET "AllocatedQuantity" = NULL""");
    }

    [Fact]
    public async Task The_allocation_migration_keeps_legacy_links_null_without_backfill_and_its_down_drops_only_the_column()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        var seed = await SeedEvidenceAtInitialSchema(db);
        await db.GetService<IMigrator>().MigrateAsync(RiskMigration);
        await InsertDossierGraph(db, seed);
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>(AllocationColumn).SingleAsync());

        await db.Database.MigrateAsync(); // no backfill: the single-plot link is NOT given the contribution quantity
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>(AllocationColumn).SingleAsync());
        Assert.Equal(1, await Count(db, "ContributionPlots"));
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "ContributionPlots" WHERE "AllocatedQuantity" IS NULL""").SingleAsync());

        await db.GetService<IMigrator>().MigrateAsync(RiskMigration); // legacy (null) links never block a rollback
        Assert.DoesNotContain(AllocationMigration, await db.Database.GetAppliedMigrationsAsync()); Assert.Contains(RiskMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>(AllocationColumn).SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM pg_constraint WHERE conname = 'CK_ContributionPlots_AllocatedQuantityPositive'""").SingleAsync());
        Assert.Equal(1, await Count(db, "ContributionPlots")); Assert.Equal(1, await Count(db, "CaseLots")); Assert.Equal(1, await Count(db, "Cases"));
    }

    [Fact]
    public async Task The_allocation_down_is_refused_while_any_link_carries_an_allocation_and_leaves_schema_and_rows_intact()
    {
        await using var db = NewContext();
        await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        var seed = await SeedEvidenceAtInitialSchema(db);
        await db.GetService<IMigrator>().MigrateAsync(AllocationMigration);
        await InsertDossierGraph(db, seed);
        await db.Database.ExecuteSqlRawAsync("""UPDATE "ContributionPlots" SET "AllocatedQuantity" = 5.5""");

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync(RiskMigration));
        Assert.Contains("Source allocations exist; rollback would destroy history", failure.ToString());
        Assert.Contains(AllocationMigration, await db.Database.GetAppliedMigrationsAsync()); // the schema and the retained value survive the refusal
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>(AllocationColumn).SingleAsync());
        Assert.Equal(5.5m, await db.Database.SqlQueryRaw<decimal>("""SELECT "AllocatedQuantity" AS "Value" FROM "ContributionPlots" """).SingleAsync());

        await db.Database.ExecuteSqlRawAsync("""UPDATE "ContributionPlots" SET "AllocatedQuantity" = NULL""");
        await db.GetService<IMigrator>().MigrateAsync(RiskMigration);
        Assert.DoesNotContain(AllocationMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>(AllocationColumn).SingleAsync());
    }
#pragma warning restore EF1002
}
