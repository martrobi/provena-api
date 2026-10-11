using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Provena.Api.Auth;
namespace Provena.Api.Data;
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().Build();
        var connection = config.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("Set ConnectionStrings__Default or configure user-secrets for EF commands.");
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options, new TenantContext(new HttpContextAccessor()));
    }
}
