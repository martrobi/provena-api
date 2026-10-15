using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.IntegrationTests;

// Scaffolding for tests that need a Closed or archived case before the lifecycle routes exist (Case Lifecycle v2, Slice 1): raw SQL on purpose, because the
// service layer would (rightly) refuse to create a closure without an Owner review. It bypasses the tracked-write guards and is never product behaviour.
internal static class LifecycleTestSupport
{
    public static async Task SetStatus(IServiceProvider services, Guid caseId, EudrCaseStatus status)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync($"""UPDATE "Cases" SET "Status" = {(int)status} WHERE "Id" = {caseId}""");
    }

    // Starts a new archive period for the case (the workflow status is left untouched: archive is independent of it).
    public static async Task Archive(IServiceProvider services, Guid caseId, string reason = "Archived by a test.")
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "CaseArchivals" ("Id", "CreatedAtUtc", "UpdatedAtUtc", "OrganizationId", "EudrCaseId", "Number", "ArchivedAtUtc", "ArchivedByUserId", "Reason")
            SELECT gen_random_uuid(), now(), now(), c."OrganizationId", c."Id", COALESCE((SELECT MAX(a."Number") FROM "CaseArchivals" a WHERE a."EudrCaseId" = c."Id"), 0) + 1, now(), NULL, {reason}
            FROM "Cases" c WHERE c."Id" = {caseId}
            """);
    }

    public static async Task Unarchive(IServiceProvider services, Guid caseId)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync($"""UPDATE "CaseArchivals" SET "UnarchivedAtUtc" = now(), "UnarchiveReason" = 'Unarchived by a test.' WHERE "EudrCaseId" = {caseId} AND "UnarchivedAtUtc" IS NULL""");
    }
}
