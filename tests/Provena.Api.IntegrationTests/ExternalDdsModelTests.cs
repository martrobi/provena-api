using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Provena.Api.Auth;
using Provena.Api.Data;
using Provena.Api.Domain;
using Xunit;
namespace Provena.Api.IntegrationTests;

// External DDS Record v1: persistence model, guards and database constraints. The guards are application-level (SaveChanges); raw SQL deliberately bypasses them, which is why the
// constraints are tested separately with raw SQL against the real PostgreSQL schema.
[Collection("Api")]
public sealed class ExternalDdsModelTests(ApiFactory factory)
{
    private const string RetentionMessage = "Retained evidence is immutable except for one-way retirement.";
    private const string ReviewFk = "FK_ExternalDdsRecords_ReviewRecords_OrganizationId_EudrCaseId_~";
    private const string DocumentFk = "FK_ExternalDdsRecords_Documents_OrganizationId_EudrCaseId_Rece~";
    private const string SupersedesFk = "FK_ExternalDdsRecords_ExternalDdsRecords_OrganizationId_EudrCa~";
    private sealed record World(Guid Org, Guid CaseId, Guid ReviewId, Guid DocumentId, Guid OtherCaseId, Guid OtherReviewId, Guid OtherDocumentId);

    private async Task<Guid> NewOrganization()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new Organization { Name = "External DDS Org " + Guid.NewGuid().ToString("N") };
        db.Organizations.Add(org); await db.SaveChangesAsync();
        return org.Id;
    }
    private static AppDbContext TenantDb(IServiceScope scope, Guid org)
    {
        var tenant = new TenantContext(new HttpContextAccessor()); tenant.SetOrganization(org);
        return new AppDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>(), tenant);
    }
    private static async Task<(Guid CaseId, Guid ReviewId, Guid DocumentId)> SeedCase(AppDbContext db, Guid org)
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
        db.ReviewRecords.Add(review);
        var document = new CaseDocument { OrganizationId = org, EudrCaseId = c.Id, OriginalFileName = "receipt.txt", StoredFileName = Guid.NewGuid().ToString("N"), ContentType = "text/plain", SizeBytes = 1, StorageKey = "k/" + Guid.NewGuid().ToString("N") };
        db.Documents.Add(document); await db.SaveChangesAsync();
        return (c.Id, review.Id, document.Id);
    }
    private async Task<World> Seed()
    {
        var org = await NewOrganization();
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org);
        var (caseId, reviewId, documentId) = await SeedCase(db, org); var (otherCase, otherReview, otherDocument) = await SeedCase(db, org);
        return new(org, caseId, reviewId, documentId, otherCase, otherReview, otherDocument);
    }
    private static ExternalDdsRecord NewRecord(World w, int number = 1, Guid? supersedes = null) => new()
    {
        OrganizationId = w.Org, EudrCaseId = w.CaseId, Number = number, ReviewId = w.ReviewId, ReferenceNumber = "REF-" + number, VerificationNumber = "VERIF-FIXTURE", SubmittedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
        RecordedByUserId = Guid.NewGuid(), SupersedesExternalDdsRecordId = supersedes, ReplacementReason = supersedes == null ? null : "Correction.",
    };

    [Fact]
    public async Task Records_are_tenant_filtered_and_cannot_be_written_for_another_organization()
    {
        var a = await Seed(); var b = await NewOrganization();
        await using (var scope = factory.Services.CreateAsyncScope()) { await using var dbA = TenantDb(scope, a.Org); dbA.ExternalDdsRecords.Add(NewRecord(a)); await dbA.SaveChangesAsync(); }
        await using var other = factory.Services.CreateAsyncScope(); await using var dbB = TenantDb(other, b);
        Assert.Empty(await dbB.ExternalDdsRecords.ToListAsync());
        dbB.ExternalDdsRecords.Add(NewRecord(a, 2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => dbB.SaveChangesAsync());
    }

    [Fact]
    public async Task A_record_allows_only_the_one_way_supersession_and_is_never_edited_or_deleted()
    {
        var w = await Seed(); Guid id;
        await using (var scope = factory.Services.CreateAsyncScope()) { await using var db = TenantDb(scope, w.Org); var r = NewRecord(w); db.ExternalDdsRecords.Add(r); await db.SaveChangesAsync(); id = r.Id; }
        async Task Rejected(Func<ExternalDdsRecord, Task> change, bool delete = false)
        {
            await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, w.Org);
            var r = await db.ExternalDdsRecords.SingleAsync(x => x.Id == id);
            if (delete) db.ExternalDdsRecords.Remove(r); else await change(r);
            Assert.Equal(delete ? "Retained evidence is never deleted; withdraw or supersede it." : RetentionMessage, (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync())).Message);
        }
        await Rejected(r => { r.ReferenceNumber = "CHANGED"; return Task.CompletedTask; });
        await Rejected(r => { r.VerificationNumber = "CHANGED"; return Task.CompletedTask; });
        await Rejected(r => { r.ReviewId = w.OtherReviewId; return Task.CompletedTask; });
        await Rejected(r => { r.SubmittedAtUtc = DateTimeOffset.UtcNow; return Task.CompletedTask; });
        await Rejected(r => { r.ReceiptDocumentId = w.DocumentId; return Task.CompletedTask; });
        await Rejected(r => { r.ReplacementReason = "Rewritten."; return Task.CompletedTask; });
        await Rejected(r => { r.SupersededAtUtc = DateTimeOffset.UtcNow; r.ReferenceNumber = "CHANGED"; return Task.CompletedTask; }); // retirement together with an edit
        await Rejected(_ => Task.CompletedTask, delete: true);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await using var db = TenantDb(scope, w.Org);
            (await db.ExternalDdsRecords.SingleAsync(x => x.Id == id)).SupersededAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); // the one allowed transition
            Assert.Empty(await db.ExternalDdsRecords.Current().ToListAsync()); Assert.Single(await db.ExternalDdsRecords.ToListAsync()); // retained as history
        }
        await Rejected(r => { r.SupersededAtUtc = null; return Task.CompletedTask; }); // un-retiring is refused
        await Rejected(r => { r.SupersededAtUtc = DateTimeOffset.UtcNow.AddDays(1); return Task.CompletedTask; }); // and so is changing the marker again
        await using var verify = factory.Services.CreateAsyncScope(); await using var check = TenantDb(verify, w.Org);
        var kept = await check.ExternalDdsRecords.SingleAsync(x => x.Id == id);
        Assert.Equal("REF-1", kept.ReferenceNumber); Assert.Equal("VERIF-FIXTURE", kept.VerificationNumber);
    }

    [Fact]
    public async Task The_supersede_chain_is_retained_and_exactly_one_record_stays_current()
    {
        var w = await Seed(); Guid firstId, secondId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await using var db = TenantDb(scope, w.Org);
            var first = NewRecord(w); db.ExternalDdsRecords.Add(first); await db.SaveChangesAsync();
            first.SupersededAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
            var second = NewRecord(w, 2, first.Id); db.ExternalDdsRecords.Add(second); await db.SaveChangesAsync();
            firstId = first.Id; secondId = second.Id;
        }
        await using var check = factory.Services.CreateAsyncScope(); await using var read = TenantDb(check, w.Org);
        Assert.Equal([secondId], await read.ExternalDdsRecords.Current().Select(x => x.Id).ToListAsync());
        var chain = await read.ExternalDdsRecords.OrderBy(x => x.Number).ToListAsync();
        Assert.Equal([null, firstId], chain.Select(x => x.SupersedesExternalDdsRecordId).ToArray()); Assert.NotNull(chain[0].SupersededAtUtc); Assert.Null(chain[1].SupersededAtUtc);
        Assert.Equal(secondId, chain[1].Id);
    }

    // ---- database constraints (raw SQL bypasses the guards) -----------------------------------------------------------------------------------
    private async Task Violation(Guid org, Func<AppDbContext, Task> insert, string sqlState, string constraint)
    {
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org);
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => insert(db));
        var postgres = Assert.IsType<PostgresException>(failure.GetBaseException());
        Assert.Equal(sqlState, postgres.SqlState); Assert.Equal(constraint, postgres.ConstraintName);
    }
    private static Task Insert(AppDbContext db, Guid org, Guid caseId, Guid reviewId, int number, Guid? id = null, Guid? document = null, Guid? supersedes = null, string reference = "REF", string verification = "VERIF",
        string? reason = null, bool superseded = false) => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO "ExternalDdsRecords" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","ReviewId","ReferenceNumber","VerificationNumber","SubmittedAtUtc","RecordedByUserId",
            "ReceiptDocumentId","SupersedesExternalDdsRecordId","ReplacementReason","SupersededAtUtc")
        VALUES ({id ?? Guid.NewGuid()}, now(), now(), {org}, {caseId}, {number}, {reviewId}, {reference}, {verification}, now(), {Guid.NewGuid()}, {document}, {supersedes}, {reason}, CASE WHEN {superseded} THEN now() ELSE NULL END)
        """);

    [Fact]
    public async Task A_case_has_at_most_one_current_record_unique_numbers_and_a_record_is_superseded_at_most_once()
    {
        var w = await Seed(); var firstId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope()) { await using var db = TenantDb(scope, w.Org); await Insert(db, w.Org, w.CaseId, w.ReviewId, 1, firstId); }
        await Violation(w.Org, db => Insert(db, w.Org, w.CaseId, w.ReviewId, 2), PostgresErrorCodes.UniqueViolation, "IX_ExternalDdsRecords_EudrCaseId"); // a second current record
        await Violation(w.Org, db => Insert(db, w.Org, w.CaseId, w.ReviewId, 1, superseded: true), PostgresErrorCodes.UniqueViolation, "IX_ExternalDdsRecords_EudrCaseId_Number");
        await using var scope2 = factory.Services.CreateAsyncScope(); await using var db2 = TenantDb(scope2, w.Org);
        await db2.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "ExternalDdsRecords" SET "SupersededAtUtc" = now() WHERE "Id" = {firstId}""");
        var successor = Guid.NewGuid();
        await Insert(db2, w.Org, w.CaseId, w.ReviewId, 2, successor, supersedes: firstId, reason: "Correction."); // the supersession then the successor is the allowed order
        await Violation(w.Org, db => Insert(db, w.Org, w.CaseId, w.ReviewId, 3, supersedes: firstId, reason: "Competing correction.", superseded: true), PostgresErrorCodes.UniqueViolation, "IX_ExternalDdsRecords_SupersedesExternalDdsRecordId");
        await Insert(db2, w.Org, w.OtherCaseId, w.OtherReviewId, 1); // the same Number and a current record in another case are fine
    }

    [Fact]
    public async Task References_must_belong_to_the_records_own_organization_and_case()
    {
        var w = await Seed(); var foreign = await Seed(); var firstId = Guid.NewGuid(); var otherCaseRecord = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await using var db = TenantDb(scope, w.Org);
            await Insert(db, w.Org, w.CaseId, w.ReviewId, 1, firstId, superseded: true); await Insert(db, w.Org, w.OtherCaseId, w.OtherReviewId, 1, otherCaseRecord, superseded: true);
        }
        await Violation(w.Org, db => Insert(db, w.Org, w.CaseId, w.OtherReviewId, 2), PostgresErrorCodes.ForeignKeyViolation, ReviewFk); // a review of another case
        await Violation(w.Org, db => Insert(db, w.Org, w.CaseId, foreign.ReviewId, 2), PostgresErrorCodes.ForeignKeyViolation, ReviewFk); // a review of another organization
        await Violation(w.Org, db => Insert(db, w.Org, w.CaseId, Guid.NewGuid(), 2), PostgresErrorCodes.ForeignKeyViolation, ReviewFk);
        await Violation(w.Org, db => Insert(db, w.Org, foreign.CaseId, foreign.ReviewId, 2), PostgresErrorCodes.ForeignKeyViolation, "FK_ExternalDdsRecords_Cases_OrganizationId_EudrCaseId"); // a case of another organization
        await Violation(w.Org, db => Insert(db, w.Org, w.CaseId, w.ReviewId, 2, document: w.OtherDocumentId), PostgresErrorCodes.ForeignKeyViolation, DocumentFk); // a document of another case
        await Violation(w.Org, db => Insert(db, w.Org, w.CaseId, w.ReviewId, 2, document: foreign.DocumentId), PostgresErrorCodes.ForeignKeyViolation, DocumentFk); // a document of another organization
        await Violation(w.Org, db => Insert(db, w.Org, w.CaseId, w.ReviewId, 2, supersedes: otherCaseRecord, reason: "x"), PostgresErrorCodes.ForeignKeyViolation, SupersedesFk); // a record of another case
        await Violation(w.Org, db => Insert(db, w.Org, w.CaseId, w.ReviewId, 2, supersedes: Guid.NewGuid(), reason: "x"), PostgresErrorCodes.ForeignKeyViolation, SupersedesFk);
        await using var ok = factory.Services.CreateAsyncScope(); await using var db2 = TenantDb(ok, w.Org);
        await Insert(db2, w.Org, w.CaseId, w.ReviewId, 2, document: w.DocumentId, supersedes: firstId, reason: "Correction."); // the same-case references are accepted
        // The document and the review are Restrict-referenced: they cannot be deleted while a record points at them.
        await Violation(w.Org, db => db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "Documents" WHERE "Id" = {w.DocumentId}"""), PostgresErrorCodes.ForeignKeyViolation, DocumentFk);
        await Violation(w.Org, db => db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "ReviewRecords" WHERE "Id" = {w.ReviewId}"""), PostgresErrorCodes.ForeignKeyViolation, ReviewFk);
    }

    [Fact]
    public async Task The_numbers_must_be_nonblank_and_a_replacement_needs_its_reason()
    {
        var w = await Seed(); var firstId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope()) { await using var db = TenantDb(scope, w.Org); await Insert(db, w.Org, w.CaseId, w.ReviewId, 1, firstId, superseded: true); }
        const string Blank = "CK_ExternalDdsRecords_NonblankNumbers"; const string Pair = "CK_ExternalDdsRecords_ReplacementPair";
        await Violation(w.Org, db => Insert(db, w.Org, w.CaseId, w.ReviewId, 2, reference: "   "), PostgresErrorCodes.CheckViolation, Blank);
        await Violation(w.Org, db => Insert(db, w.Org, w.CaseId, w.ReviewId, 2, verification: ""), PostgresErrorCodes.CheckViolation, Blank);
        await Violation(w.Org, db => Insert(db, w.Org, w.CaseId, w.ReviewId, 2, supersedes: firstId), PostgresErrorCodes.CheckViolation, Pair); // a replacement without a reason
        await Violation(w.Org, db => Insert(db, w.Org, w.CaseId, w.ReviewId, 2, supersedes: firstId, reason: "  "), PostgresErrorCodes.CheckViolation, Pair);
        await Violation(w.Org, db => Insert(db, w.Org, w.CaseId, w.ReviewId, 2, reason: "Nothing to replace."), PostgresErrorCodes.CheckViolation, Pair); // a reason without a replacement
    }
}
