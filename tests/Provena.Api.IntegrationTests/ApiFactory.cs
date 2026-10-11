using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Provena.Api.Data;
using Testcontainers.PostgreSql;
using Xunit;
namespace Provena.Api.IntegrationTests;
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder().WithImage("postgres:17").Build();
    private readonly string storage = Path.Combine(Path.GetTempPath(), "provena-tests-" + Guid.NewGuid().ToString("N"));
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = postgres.GetConnectionString(),
            ["Jwt:Secret"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            ["Jwt:Issuer"] = "Provena.Api", ["Jwt:Audience"] = "Provena.Frontend",
            ["Frontend:BaseUrl"] = "https://frontend.example.test", ["FileStorage:RootPath"] = storage,
            ["SeedDemoData"] = "false"
        }));
    }
    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(db.Database.HasPendingModelChanges(), "The initial model snapshot must match the DbContext.");
        await db.Database.MigrateAsync();
    }
    async Task IAsyncLifetime.DisposeAsync()
    { await base.DisposeAsync(); await postgres.DisposeAsync(); if (Directory.Exists(storage)) Directory.Delete(storage, true); }
}
[CollectionDefinition("Api", DisableParallelization = true)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory> { }
