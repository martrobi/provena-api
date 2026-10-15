using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Provena.Api.Auth;
using Provena.Api.Data;
using Provena.Api.Domain;
using Xunit;
namespace Provena.Api.IntegrationTests;

// Case Lifecycle v2, Slice 1: persistence model, guards and database constraints of the closure / reopening / archive history. The guards are application-level
// (SaveChanges); raw SQL deliberately bypasses them, which is why the constraints are tested separately with raw SQL. No lifecycle service or route exists yet.
[Collection("Api")]
public sealed class CaseLifecycleModelTests(ApiFactory factory)
{
    private const string ImmutableMessage = "Case lifecycle history is immutable; record a new closure or reopening.";
    private const string RetentionMessage = "Retained evidence is immutable except for one-way retirement.";
    private const string ReviewFk = "FK_CaseClosures_ReviewRecords_OrganizationId_EudrCaseId_Review~";
    private const string ClosureFk = "FK_CaseReopenings_CaseClosures_OrganizationId_EudrCaseId_CaseC~";
    private sealed record World(Guid Org, Guid CaseId, Guid ReviewId, Guid OtherCaseId, Guid OtherReviewId);

    private async Task<Guid> NewOrganization()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new Organization { Name = "Lifecycle Org " + Guid.NewGuid().ToString("N") };
        db.Organizations.Add(org); await db.SaveChangesAsync();
        return org.Id;
    }
    private static AppDbContext TenantDb(IServiceScope scope, Guid org)
    {
        var tenant = new TenantContext(new HttpContextAccessor()); tenant.SetOrganization(org);
        return new AppDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>(), tenant);
    }
    private static async Task<(Guid CaseId, Guid ReviewId)> SeedCaseWithReview(AppDbContext db, Guid org)
    {
        var supplier = new Supplier { OrganizationId = org, Name = "Supplier" }; var product = new Product { OrganizationId = org, Name = "Product", CnCode = "0901" };
        db.Suppliers.Add(supplier); db.Products.Add(product); await db.SaveChangesAsync();
        var shipment = new Shipment { OrganizationId = org, ReferenceNumber = "SHP-" + Guid.NewGuid().ToString("N"), SupplierId = supplier.Id, ProductId = product.Id, Quantity = 10, Unit = QuantityUnit.Kg };
        db.Shipments.Add(shipment); await db.SaveChangesAsync();
        var c = new EudrCase { OrganizationId = org, ShipmentId = shipment.Id }; db.Cases.Add(c); await db.SaveChangesAsync();
        var revision = new CaseRevision
        {
            OrganizationId = org, EudrCaseId = c.Id, Number = 1, PolicyId = "provena-pilot-completeness", PolicyVersion = "1", SnapshotSchemaVersion = 1,
            FingerprintVersion = 1, Fingerprint = new string('a', 64), SectionHashes = "{}", SnapshotJson = "{}",
        };
        db.CaseRevisions.Add(revision); await db.SaveChangesAsync();
        var review = new ReviewRecord { OrganizationId = org, EudrCaseId = c.Id, CaseRevisionId = revision.Id, Decision = ReviewDecision.AcceptedAsCompleteUnderPolicy, Rationale = "r", ReviewerUserId = Guid.NewGuid(), ReviewedAtUtc = DateTimeOffset.UtcNow, PolicyVersion = "1" };
        db.ReviewRecords.Add(review); await db.SaveChangesAsync();
        return (c.Id, review.Id);
    }
    private async Task<World> Seed()
    {
        var org = await NewOrganization();
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org);
        var (caseId, reviewId) = await SeedCaseWithReview(db, org); var (otherCase, otherReview) = await SeedCaseWithReview(db, org);
        return new(org, caseId, reviewId, otherCase, otherReview);
    }
    private static CaseClosure NewClosure(World w, int number = 1) => new()
    { OrganizationId = w.Org, EudrCaseId = w.CaseId, Number = number, Outcome = CaseClosureOutcome.ExternalDdsCompleted, ReviewRecordId = w.ReviewId, ExternalDdsReference = "DDS-1", ClosedByUserId = Guid.NewGuid(), ClosedAtUtc = DateTimeOffset.UtcNow };
    private static CaseReopening NewReopening(World w, Guid closureId) => new()
    { OrganizationId = w.Org, EudrCaseId = w.CaseId, CaseClosureId = closureId, Reason = "Corrected data.", ReopenedByUserId = Guid.NewGuid(), ReopenedAtUtc = DateTimeOffset.UtcNow };
    private static CaseArchival NewArchival(World w, int number = 1) => new()
    { OrganizationId = w.Org, EudrCaseId = w.CaseId, Number = number, ArchivedAtUtc = DateTimeOffset.UtcNow, ArchivedByUserId = Guid.NewGuid(), Reason = "Archived for retention." };

    // ---- enum and entity shape -----------------------------------------------------------------------------------------------------------
    [Fact]
    public void Workflow_status_has_no_completed_or_archived_value_and_keeps_the_stored_integers_of_the_open_statuses()
    {
        Assert.Equal(["Draft", "WaitingForSupplier", "ReviewNeeded", "ReadyForExport", "Closed"], Enum.GetNames<EudrCaseStatus>());
        Assert.Equal([0, 1, 2, 3, 4], Enum.GetValues<EudrCaseStatus>().Select(x => (int)x).ToArray());
        Assert.Equal(["ExternalDdsCompleted", "OutsideRegulatoryScope", "UnsupportedFlow"], Enum.GetNames<CaseClosureOutcome>());
        Assert.DoesNotContain(typeof(EudrCase).GetProperties(), p => p.Name is "CompletedAtUtc" or "ExternalDdsReference");
    }

    // ---- tenant filter and stamping -------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Lifecycle_history_is_tenant_filtered_and_cannot_be_written_for_another_organization()
    {
        var a = await Seed(); var b = await NewOrganization();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await using var dbA = TenantDb(scope, a.Org);
            var closure = NewClosure(a); dbA.CaseClosures.Add(closure); dbA.CaseArchivals.Add(NewArchival(a)); await dbA.SaveChangesAsync();
            dbA.CaseReopenings.Add(NewReopening(a, closure.Id)); await dbA.SaveChangesAsync();
        }
        await using var other = factory.Services.CreateAsyncScope(); await using var dbB = TenantDb(other, b);
        Assert.Empty(await dbB.CaseClosures.ToListAsync()); Assert.Empty(await dbB.CaseReopenings.ToListAsync()); Assert.Empty(await dbB.CaseArchivals.ToListAsync());
        dbB.CaseClosures.Add(NewClosure(a, 2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => dbB.SaveChangesAsync());
        dbB.ChangeTracker.Clear();
        dbB.CaseArchivals.Add(NewArchival(a, 2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => dbB.SaveChangesAsync());
    }

    // ---- closure and reopening immutability --------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Closures_and_reopenings_reject_every_update_and_delete()
    {
        var w = await Seed(); Guid closureId, reopeningId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await using var db = TenantDb(scope, w.Org);
            var closure = NewClosure(w); db.CaseClosures.Add(closure); await db.SaveChangesAsync();
            var reopening = NewReopening(w, closure.Id); db.CaseReopenings.Add(reopening); await db.SaveChangesAsync();
            closureId = closure.Id; reopeningId = reopening.Id;
        }
        async Task Rejected(Func<AppDbContext, Task> change)
        {
            await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, w.Org);
            await change(db);
            Assert.Equal(ImmutableMessage, (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync())).Message);
        }
        await Rejected(async db => (await db.CaseClosures.SingleAsync(x => x.Id == closureId)).ExternalDdsReference = "CHANGED");
        await Rejected(async db => (await db.CaseClosures.SingleAsync(x => x.Id == closureId)).Outcome = CaseClosureOutcome.UnsupportedFlow);
        await Rejected(async db => (await db.CaseClosures.SingleAsync(x => x.Id == closureId)).ReviewRecordId = w.OtherReviewId);
        await Rejected(async db => (await db.CaseClosures.SingleAsync(x => x.Id == closureId)).UpdatedAtUtc = DateTimeOffset.UtcNow.AddDays(1)); // every column, timestamps included
        await Rejected(async db => db.CaseClosures.Remove(await db.CaseClosures.SingleAsync(x => x.Id == closureId)));
        await Rejected(async db => (await db.CaseReopenings.SingleAsync(x => x.Id == reopeningId)).Reason = "Rewritten.");
        await Rejected(async db => (await db.CaseReopenings.SingleAsync(x => x.Id == reopeningId)).ReopenedByUserId = Guid.NewGuid());
        await Rejected(async db => db.CaseReopenings.Remove(await db.CaseReopenings.SingleAsync(x => x.Id == reopeningId)));
        await using var verify = factory.Services.CreateAsyncScope(); await using var check = TenantDb(verify, w.Org);
        Assert.Equal("DDS-1", (await check.CaseClosures.SingleAsync(x => x.Id == closureId)).ExternalDdsReference);
        Assert.Equal("Corrected data.", (await check.CaseReopenings.SingleAsync(x => x.Id == reopeningId)).Reason);
    }

    // ---- archive: one-way unarchive only ----------------------------------------------------------------------------------------------------
    [Fact]
    public async Task An_archive_period_allows_only_the_one_way_unarchive_and_is_never_deleted()
    {
        var w = await Seed(); Guid id;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await using var db = TenantDb(scope, w.Org);
            var archival = NewArchival(w); db.CaseArchivals.Add(archival); await db.SaveChangesAsync(); id = archival.Id;
            Assert.Equal(id, (await db.CaseArchivals.Current().SingleAsync()).Id);
        }
        async Task Rejected(Func<AppDbContext, Task> change, string message)
        {
            await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, w.Org);
            await change(db);
            Assert.Equal(message, (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync())).Message);
        }
        await Rejected(async db => (await db.CaseArchivals.SingleAsync(x => x.Id == id)).Reason = "Rewritten.", RetentionMessage); // facts never change
        await Rejected(async db => (await db.CaseArchivals.SingleAsync(x => x.Id == id)).ArchivedAtUtc = DateTimeOffset.UtcNow.AddDays(-1), RetentionMessage);
        await Rejected(async db => (await db.CaseArchivals.SingleAsync(x => x.Id == id)).UnarchiveReason = "reason without the marker", RetentionMessage);
        await Rejected(async db => { var a = await db.CaseArchivals.SingleAsync(x => x.Id == id); a.UnarchivedAtUtc = DateTimeOffset.UtcNow; a.Reason = "Rewritten."; }, RetentionMessage); // marker plus a fact change
        await Rejected(async db => db.CaseArchivals.Remove(await db.CaseArchivals.SingleAsync(x => x.Id == id)), "Retained evidence is never deleted; withdraw or supersede it.");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await using var db = TenantDb(scope, w.Org);
            var a = await db.CaseArchivals.SingleAsync(x => x.Id == id); a.UnarchivedAtUtc = DateTimeOffset.UtcNow; a.UnarchivedByUserId = Guid.NewGuid(); a.UnarchiveReason = "Needed again.";
            await db.SaveChangesAsync();
            Assert.Empty(await db.CaseArchivals.Current().ToListAsync()); // the period stays as history
            Assert.Single(await db.CaseArchivals.ToListAsync());
        }
        await Rejected(async db => (await db.CaseArchivals.SingleAsync(x => x.Id == id)).UnarchivedAtUtc = null, RetentionMessage); // un-retiring is refused
        await Rejected(async db => (await db.CaseArchivals.SingleAsync(x => x.Id == id)).UnarchiveReason = "Rewritten.", RetentionMessage);
    }

    // ---- database constraints (raw SQL bypasses the guards) -----------------------------------------------------------------------------------
    private async Task Violation(Guid org, Func<AppDbContext, Task> insert, string sqlState, string constraint)
    {
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org);
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => insert(db));
        var postgres = Assert.IsType<PostgresException>(failure.GetBaseException());
        Assert.Equal(sqlState, postgres.SqlState); Assert.Equal(constraint, postgres.ConstraintName);
    }
    private static Task InsertClosure(AppDbContext db, Guid org, Guid caseId, Guid reviewId, int number, Guid? id = null) => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO "CaseClosures" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","Outcome","ReviewRecordId","ExternalDdsReference","ClosedByUserId","ClosedAtUtc")
        VALUES ({id ?? Guid.NewGuid()}, now(), now(), {org}, {caseId}, {number}, 0, {reviewId}, NULL, {Guid.NewGuid()}, now())
        """);
    private static Task InsertReopening(AppDbContext db, Guid org, Guid caseId, Guid closureId) => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO "CaseReopenings" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","CaseClosureId","Reason","ReopenedByUserId","ReopenedAtUtc")
        VALUES ({Guid.NewGuid()}, now(), now(), {org}, {caseId}, {closureId}, 'r', {Guid.NewGuid()}, now())
        """);
    private static Task InsertArchival(AppDbContext db, Guid org, Guid caseId, int number, bool unarchived) => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO "CaseArchivals" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","ArchivedAtUtc","Reason","UnarchivedAtUtc")
        VALUES ({Guid.NewGuid()}, now(), now(), {org}, {caseId}, {number}, now(), 'r', CASE WHEN {unarchived} THEN now() ELSE NULL END)
        """);

    [Fact]
    public async Task Closure_numbers_are_unique_per_case_and_a_review_backs_at_most_one_closure()
    {
        var w = await Seed();
        await using (var scope = factory.Services.CreateAsyncScope()) { await using var db = TenantDb(scope, w.Org); await InsertClosure(db, w.Org, w.CaseId, w.ReviewId, 1); }
        await Violation(w.Org, db => InsertClosure(db, w.Org, w.CaseId, w.ReviewId, 2), PostgresErrorCodes.UniqueViolation, "IX_CaseClosures_ReviewRecordId"); // the same review again, even after a reopen
        await Violation(w.Org, db => InsertClosure(db, w.Org, w.CaseId, w.OtherReviewId, 1), PostgresErrorCodes.UniqueViolation, "IX_CaseClosures_EudrCaseId_Number");
        await using var ok = factory.Services.CreateAsyncScope(); await using var other = TenantDb(ok, w.Org);
        await InsertClosure(other, w.Org, w.OtherCaseId, w.OtherReviewId, 1); // the same Number in another case is fine
    }

    [Fact]
    public async Task A_closure_can_only_reference_a_review_of_its_own_case_and_organization()
    {
        var w = await Seed(); var foreign = await Seed();
        await Violation(w.Org, db => InsertClosure(db, w.Org, w.CaseId, w.OtherReviewId, 1), PostgresErrorCodes.ForeignKeyViolation, ReviewFk); // a review of another case
        await Violation(w.Org, db => InsertClosure(db, w.Org, w.CaseId, foreign.ReviewId, 1), PostgresErrorCodes.ForeignKeyViolation, ReviewFk); // a review of another organization
        await Violation(w.Org, db => InsertClosure(db, w.Org, foreign.CaseId, foreign.ReviewId, 1), PostgresErrorCodes.ForeignKeyViolation, "FK_CaseClosures_Cases_OrganizationId_EudrCaseId"); // a case of another organization
        await Violation(w.Org, db => InsertClosure(db, w.Org, w.CaseId, Guid.NewGuid(), 1), PostgresErrorCodes.ForeignKeyViolation, ReviewFk);
    }

    [Fact]
    public async Task A_closure_is_reopened_at_most_once_and_only_within_its_own_case()
    {
        var w = await Seed(); var closureId = Guid.NewGuid(); var otherClosureId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await using var db = TenantDb(scope, w.Org);
            await InsertClosure(db, w.Org, w.CaseId, w.ReviewId, 1, closureId); await InsertClosure(db, w.Org, w.OtherCaseId, w.OtherReviewId, 1, otherClosureId);
            await InsertReopening(db, w.Org, w.CaseId, closureId);
        }
        await Violation(w.Org, db => InsertReopening(db, w.Org, w.CaseId, closureId), PostgresErrorCodes.UniqueViolation, "IX_CaseReopenings_CaseClosureId");
        await Violation(w.Org, db => InsertReopening(db, w.Org, w.CaseId, otherClosureId), PostgresErrorCodes.ForeignKeyViolation, ClosureFk); // a closure of another case
        await Violation(w.Org, db => InsertReopening(db, w.Org, w.CaseId, Guid.NewGuid()), PostgresErrorCodes.ForeignKeyViolation, ClosureFk);
    }

    [Fact]
    public async Task A_case_has_at_most_one_current_archive_period_and_unique_period_numbers()
    {
        var w = await Seed();
        await using (var scope = factory.Services.CreateAsyncScope()) { await using var db = TenantDb(scope, w.Org); await InsertArchival(db, w.Org, w.CaseId, 1, unarchived: false); }
        await Violation(w.Org, db => InsertArchival(db, w.Org, w.CaseId, 2, unarchived: false), PostgresErrorCodes.UniqueViolation, "IX_CaseArchivals_EudrCaseId"); // a second current period
        await Violation(w.Org, db => InsertArchival(db, w.Org, w.CaseId, 1, unarchived: true), PostgresErrorCodes.UniqueViolation, "IX_CaseArchivals_EudrCaseId_Number");
        await using var ok = factory.Services.CreateAsyncScope(); await using var other = TenantDb(ok, w.Org);
        await InsertArchival(other, w.Org, w.CaseId, 2, unarchived: true); // an ended period never conflicts with the current one
        await InsertArchival(other, w.Org, w.OtherCaseId, 1, unarchived: false); // another case has its own current period
        Assert.Equal(2, await other.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "CaseArchivals" WHERE "UnarchivedAtUtc" IS NULL AND "OrganizationId" = {0}""", w.Org).SingleAsync());
    }

    [Fact]
    public async Task Archive_is_independent_of_the_workflow_status()
    {
        var w = await Seed();
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, w.Org);
        db.CaseArchivals.Add(NewArchival(w)); await db.SaveChangesAsync();
        var c = await db.Cases.AsNoTracking().SingleAsync(x => x.Id == w.CaseId);
        Assert.Equal(EudrCaseStatus.Draft, c.Status); // archiving rewrites no status
        Assert.Equal(1, await db.CaseArchivals.Current().CountAsync(x => x.EudrCaseId == w.CaseId));
        Assert.Equal(0, await db.CaseArchivals.Current().CountAsync(x => x.EudrCaseId == w.OtherCaseId));
    }
}
