using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Provena.Api.Auth;
using Provena.Api.Data;
using Provena.Api.Domain;
using Xunit;
namespace Provena.Api.IntegrationTests;

// Post-completion Concern & Corrective Action v1: persistence guards and database constraints. The guards are application-level (SaveChanges); raw SQL deliberately bypasses them, which is
// why the constraints are tested separately with raw SQL against the real PostgreSQL schema.
[Collection("Api")]
public sealed class ConcernModelTests(ApiFactory factory)
{
    private const string ImmutableMessage = "Concern history is immutable; record a new concern fact.";
    private sealed record World(Guid Org, Guid CaseId, Guid OtherCaseId, Guid DocumentId, Guid OtherCaseDocumentId, Guid RecipientId, Guid ConcernId);

    private static AppDbContext TenantDb(IServiceScope scope, Guid org)
    {
        var tenant = new TenantContext(new HttpContextAccessor()); tenant.SetOrganization(org);
        return new AppDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>(), tenant);
    }
    private static async Task<(Guid CaseId, Guid DocumentId)> SeedCase(AppDbContext db, Guid org)
    {
        var supplier = new Supplier { OrganizationId = org, Name = "Supplier" }; var product = new Product { OrganizationId = org, Name = "Product", CnCode = "0901" };
        db.Suppliers.Add(supplier); db.Products.Add(product); await db.SaveChangesAsync();
        var shipment = new Shipment { OrganizationId = org, ReferenceNumber = "SHP-" + Guid.NewGuid().ToString("N"), SupplierId = supplier.Id, ProductId = product.Id, Quantity = 10, Unit = QuantityUnit.Kg };
        db.Shipments.Add(shipment); await db.SaveChangesAsync();
        var c = new EudrCase { OrganizationId = org, ShipmentId = shipment.Id, Status = EudrCaseStatus.Closed }; db.Cases.Add(c); await db.SaveChangesAsync();
        var document = new CaseDocument { OrganizationId = org, EudrCaseId = c.Id, OriginalFileName = "a.txt", StoredFileName = Guid.NewGuid().ToString("N"), ContentType = "text/plain", SizeBytes = 1, StorageKey = "k/" + Guid.NewGuid().ToString("N") };
        db.Documents.Add(document); await db.SaveChangesAsync();
        return (c.Id, document.Id);
    }
    private async Task<World> Seed()
    {
        Guid org;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var o = new Organization { Name = "Concern Org " + Guid.NewGuid().ToString("N") }; admin.Organizations.Add(o); await admin.SaveChangesAsync(); org = o.Id;
        }
        await using var s = factory.Services.CreateAsyncScope(); await using var db = TenantDb(s, org);
        var (caseId, documentId) = await SeedCase(db, org); var (otherCase, otherDocument) = await SeedCase(db, org);
        var statement = new CaseRecipientStatement { OrganizationId = org, EudrCaseId = caseId, Number = 1, Resolution = RecipientResolution.RecipientsIdentified };
        db.CaseRecipientStatements.Add(statement); await db.SaveChangesAsync();
        var recipient = new CaseRecipient { OrganizationId = org, CaseRecipientStatementId = statement.Id, Name = "R", PostalAddress = "A", Email = "r@example.test" };
        db.CaseRecipients.Add(recipient);
        var concern = NewConcern(org, caseId, 1); db.CaseConcerns.Add(concern); await db.SaveChangesAsync();
        return new(org, caseId, otherCase, documentId, otherDocument, recipient.Id, concern.Id);
    }
    private static CaseConcern NewConcern(Guid org, Guid caseId, int number) => new()
    { OrganizationId = org, EudrCaseId = caseId, Number = number, Category = ConcernCategory.Other, Summary = "S", Details = "D", RaisedAtUtc = DateTimeOffset.UtcNow.AddHours(-1), RecordedByUserId = Guid.NewGuid() };
    private static ConcernAction NewAction(World w) => new() { OrganizationId = w.Org, EudrCaseId = w.CaseId, ConcernId = w.ConcernId, Description = "d", PerformedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1), RecordedByUserId = Guid.NewGuid() };
    private static ConcernNotification NewNotification(World w) => new()
    { OrganizationId = w.Org, EudrCaseId = w.CaseId, ConcernId = w.ConcernId, CaseRecipientId = w.RecipientId, Channel = ConcernNotificationChannel.Email, NotifiedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1), RecordedByUserId = Guid.NewGuid() };
    private static ConcernResolution NewResolution(World w) => new()
    { OrganizationId = w.Org, EudrCaseId = w.CaseId, ConcernId = w.ConcernId, Outcome = ConcernResolutionOutcome.NoFurtherActionRequired, Rationale = "r", ResolvedByUserId = Guid.NewGuid(), ResolvedAtUtc = DateTimeOffset.UtcNow };

    [Fact]
    public async Task Concern_rows_are_tenant_filtered_and_cannot_be_written_for_another_organization()
    {
        var a = await Seed(); var b = await Seed();
        await using var scope = factory.Services.CreateAsyncScope(); await using var dbB = TenantDb(scope, b.Org);
        Assert.DoesNotContain(a.ConcernId, await dbB.CaseConcerns.Select(x => x.Id).ToListAsync());
        dbB.CaseConcerns.Add(NewConcern(a.Org, a.CaseId, 2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => dbB.SaveChangesAsync());
    }

    [Fact]
    public async Task All_four_concern_types_are_fully_immutable_and_never_deleted()
    {
        var w = await Seed();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await using var db = TenantDb(scope, w.Org);
            db.ConcernActions.Add(NewAction(w)); db.ConcernNotifications.Add(NewNotification(w)); db.ConcernResolutions.Add(NewResolution(w)); await db.SaveChangesAsync();
        }
        async Task Rejected<T>(Func<AppDbContext, DbSet<T>> set, Action<T>? change) where T : TenantEntity
        {
            await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, w.Org);
            var row = await set(db).FirstAsync(x => x.OrganizationId == w.Org);
            if (change == null) set(db).Remove(row); else change(row);
            Assert.Equal(ImmutableMessage, (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync())).Message);
        }
        await Rejected(d => d.CaseConcerns, c => c.Summary = "Rewritten"); await Rejected(d => d.CaseConcerns, c => c.RaisedAtUtc = DateTimeOffset.UtcNow); await Rejected<CaseConcern>(d => d.CaseConcerns, null);
        await Rejected(d => d.ConcernActions, a => a.Description = "Rewritten"); await Rejected<ConcernAction>(d => d.ConcernActions, null);
        await Rejected(d => d.ConcernNotifications, n => n.Note = "Rewritten"); await Rejected<ConcernNotification>(d => d.ConcernNotifications, null);
        await Rejected(d => d.ConcernResolutions, r => r.Rationale = "Rewritten"); await Rejected(d => d.ConcernResolutions, r => r.Outcome = ConcernResolutionOutcome.CorrectiveActionCompleted);
        await Rejected(d => d.ConcernResolutions, r => r.ResolvedAtUtc = DateTimeOffset.UtcNow.AddDays(1)); await Rejected<ConcernResolution>(d => d.ConcernResolutions, null);
        await using var verify = factory.Services.CreateAsyncScope(); await using var check = TenantDb(verify, w.Org);
        Assert.Equal("S", (await check.CaseConcerns.SingleAsync(x => x.Id == w.ConcernId)).Summary);
        Assert.Equal("r", (await check.ConcernResolutions.SingleAsync()).Rationale);
    }

    // ---- database constraints (raw SQL bypasses the guards) -----------------------------------------------------------------------------------
    private async Task Violation(Guid org, Func<AppDbContext, Task> insert, string sqlState, string constraint)
    {
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org);
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => insert(db));
        var postgres = Assert.IsType<PostgresException>(failure.GetBaseException());
        Assert.Equal(sqlState, postgres.SqlState); Assert.Equal(constraint, postgres.ConstraintName);
    }
    private static Task InsertConcern(AppDbContext db, Guid org, Guid caseId, int number, string summary = "S", string details = "D") => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO "CaseConcerns" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","Category","Summary","Details","RaisedAtUtc","RecordedByUserId")
        VALUES ({Guid.NewGuid()}, now(), now(), {org}, {caseId}, {number}, 4, {summary}, {details}, now(), {Guid.NewGuid()})
        """);
    private static Task InsertAction(AppDbContext db, Guid org, Guid caseId, Guid concernId, Guid? document = null, string description = "d") => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO "ConcernActions" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","ConcernId","Description","PerformedAtUtc","RecordedByUserId","EvidenceDocumentId")
        VALUES ({Guid.NewGuid()}, now(), now(), {org}, {caseId}, {concernId}, {description}, now(), {Guid.NewGuid()}, {document})
        """);
    private static Task InsertResolution(AppDbContext db, Guid org, Guid caseId, Guid concernId, string rationale = "r") => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO "ConcernResolutions" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","ConcernId","Outcome","Rationale","ResolvedByUserId","ResolvedAtUtc")
        VALUES ({Guid.NewGuid()}, now(), now(), {org}, {caseId}, {concernId}, 1, {rationale}, {Guid.NewGuid()}, now())
        """);
    private static Task InsertNotification(AppDbContext db, Guid org, Guid caseId, Guid concernId, Guid recipient, string? note = null) => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO "ConcernNotifications" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","ConcernId","CaseRecipientId","Channel","NotifiedAtUtc","Note","RecordedByUserId")
        VALUES ({Guid.NewGuid()}, now(), now(), {org}, {caseId}, {concernId}, {recipient}, 0, now(), {note}, {Guid.NewGuid()})
        """);

    [Fact]
    public async Task The_database_enforces_unique_concern_numbers_one_resolution_and_nonblank_text()
    {
        var w = await Seed();
        await Violation(w.Org, db => InsertConcern(db, w.Org, w.CaseId, 1), "23505", "IX_CaseConcerns_EudrCaseId_Number");
        await Violation(w.Org, db => InsertConcern(db, w.Org, w.CaseId, 5, summary: "   "), "23514", "CK_CaseConcerns_NonblankText");
        await Violation(w.Org, db => InsertConcern(db, w.Org, w.CaseId, 6, details: ""), "23514", "CK_CaseConcerns_NonblankText");
        await Violation(w.Org, db => InsertAction(db, w.Org, w.CaseId, w.ConcernId, description: " "), "23514", "CK_ConcernActions_NonblankDescription");
        await Violation(w.Org, db => InsertNotification(db, w.Org, w.CaseId, w.ConcernId, w.RecipientId, note: "  "), "23514", "CK_ConcernNotifications_NonblankNote");
        await Violation(w.Org, db => InsertResolution(db, w.Org, w.CaseId, w.ConcernId, rationale: " "), "23514", "CK_ConcernResolutions_NonblankRationale");
        await using (var scope = factory.Services.CreateAsyncScope()) { await using var db = TenantDb(scope, w.Org); await InsertResolution(db, w.Org, w.CaseId, w.ConcernId); }
        await Violation(w.Org, db => InsertResolution(db, w.Org, w.CaseId, w.ConcernId), "23505", "IX_ConcernResolutions_ConcernId"); // one resolution per concern
    }

    [Fact]
    public async Task The_database_proves_the_concern_and_the_evidence_belong_to_the_rows_own_case_and_forbids_deletes()
    {
        var w = await Seed();
        await Violation(w.Org, db => InsertAction(db, w.Org, w.OtherCaseId, w.ConcernId), "23503", "FK_ConcernActions_CaseConcerns_OrganizationId_EudrCaseId_Conce~"); // concern of another case
        await Violation(w.Org, db => InsertAction(db, w.Org, w.CaseId, w.ConcernId, document: w.OtherCaseDocumentId), "23503", "FK_ConcernActions_Documents_OrganizationId_EudrCaseId_Evidence~");
        await Violation(w.Org, db => InsertNotification(db, w.Org, w.OtherCaseId, w.ConcernId, w.RecipientId), "23503", "FK_ConcernNotifications_CaseConcerns_OrganizationId_EudrCaseId~");
        await Violation(w.Org, db => InsertNotification(db, w.Org, w.CaseId, w.ConcernId, Guid.NewGuid()), "23503", "FK_ConcernNotifications_CaseRecipients_OrganizationId_CaseReci~");
        await Violation(w.Org, db => InsertResolution(db, w.Org, w.OtherCaseId, w.ConcernId), "23503", "FK_ConcernResolutions_CaseConcerns_OrganizationId_EudrCaseId_C~");
        await using (var scope = factory.Services.CreateAsyncScope()) { await using var db = TenantDb(scope, w.Org); await InsertAction(db, w.Org, w.CaseId, w.ConcernId, document: w.DocumentId); }
        // Restrict: the concern, its evidence document and its case cannot be deleted while referenced.
        await Violation(w.Org, db => db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "CaseConcerns" WHERE "Id" = {w.ConcernId}"""), "23503", "FK_ConcernActions_CaseConcerns_OrganizationId_EudrCaseId_Conce~");
        await Violation(w.Org, db => db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "Documents" WHERE "Id" = {w.DocumentId}"""), "23503", "FK_ConcernActions_Documents_OrganizationId_EudrCaseId_Evidence~");
    }
}
