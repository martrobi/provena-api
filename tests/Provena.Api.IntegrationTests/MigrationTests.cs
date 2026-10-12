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
}
