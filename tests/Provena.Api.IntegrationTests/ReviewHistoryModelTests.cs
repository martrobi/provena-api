using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Provena.Api.Auth;
using Provena.Api.Data;
using Provena.Api.Domain;
using Xunit;
namespace Provena.Api.IntegrationTests;

// Slice 5 / commit 3: persistence model, guards and database constraints of the immutable review history (CaseRevision, its pins and ReviewRecord).
// The guards are application-level (SaveChanges); raw SQL deliberately bypasses them, which is why the constraints are tested separately with raw SQL.
[Collection("Api")]
public sealed class ReviewHistoryModelTests(ApiFactory factory)
{
    private sealed record World(Guid Org, Guid CaseId, Guid DossierId, Guid LotId, Guid ApplicabilityId, Guid StatementId, Guid MasterDataId, Guid GeoId, Guid SubmissionId, Guid DocumentId, Guid LinkId, Guid RevisionId, Guid ReviewId);

    private async Task<Guid> NewOrganization()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new Organization { Name = "Review Org " + Guid.NewGuid().ToString("N") };
        db.Organizations.Add(org); await db.SaveChangesAsync();
        return org.Id;
    }
    private static AppDbContext TenantDb(IServiceScope scope, Guid org)
    {
        var tenant = new TenantContext(new HttpContextAccessor()); tenant.SetOrganization(org);
        return new AppDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>(), tenant);
    }

    // A second case in the same organization with all pin candidates, so wrong-case pins can be tried.
    private sealed record Pins(Guid CaseId, Guid DossierId, Guid LotId, Guid ApplicabilityId, Guid StatementId, Guid MasterDataId, Guid GeoId, Guid SubmissionId, Guid DocumentId, Guid LinkId);
    private static async Task<Pins> SeedCase(AppDbContext db, Guid org)
    {
        var supplier = new Supplier { OrganizationId = org, Name = "Supplier" }; var product = new Product { OrganizationId = org, Name = "Product", CnCode = "0901" };
        db.Suppliers.Add(supplier); db.Products.Add(product); await db.SaveChangesAsync();
        var shipment = new Shipment { OrganizationId = org, ReferenceNumber = "SHP-" + Guid.NewGuid().ToString("N"), SupplierId = supplier.Id, ProductId = product.Id, Quantity = 10, Unit = QuantityUnit.Kg };
        db.Shipments.Add(shipment); await db.SaveChangesAsync();
        var c = new EudrCase { OrganizationId = org, ShipmentId = shipment.Id }; db.Cases.Add(c); await db.SaveChangesAsync();
        var dossier = new CaseDossier { OrganizationId = org, EudrCaseId = c.Id }; db.CaseDossiers.Add(dossier); await db.SaveChangesAsync();
        var lot = new CaseLot { OrganizationId = org, CaseDossierId = dossier.Id, Number = 1, LotReference = "LOT-" + Guid.NewGuid().ToString("N"), Quantity = 10, Unit = QuantityUnit.Kg }; db.CaseLots.Add(lot);
        var applicability = new CaseApplicability { OrganizationId = org, EudrCaseId = c.Id, Number = 1, AssessedProductId = product.Id, AssessedCnCode = "0901" }; db.CaseApplicabilities.Add(applicability);
        var statement = new CaseRecipientStatement { OrganizationId = org, EudrCaseId = c.Id, Number = 1, Resolution = RecipientResolution.NotApplicable, NotApplicableBasis = "b" }; db.CaseRecipientStatements.Add(statement);
        var master = new CaseMasterDataVersion { OrganizationId = org, EudrCaseId = c.Id, Number = 1, SupplierId = supplier.Id, SupplierName = "S", ProductId = product.Id, ProductCnCode = "0901", ProductName = "P" }; db.CaseMasterDataVersions.Add(master);
        var geo = new GeoSubmission { OrganizationId = org, EudrCaseId = c.Id, OriginalFileName = "g.geojson", StorageKey = "k-" + Guid.NewGuid().ToString("N") }; db.Geolocations.Add(geo);
        var submission = new SupplierSubmission { OrganizationId = org, EudrCaseId = c.Id }; db.Submissions.Add(submission);
        var document = new CaseDocument { OrganizationId = org, EudrCaseId = c.Id, OriginalFileName = "a.pdf", StoredFileName = "a.pdf", ContentType = "application/pdf", SizeBytes = 1, StorageKey = "d-" + Guid.NewGuid().ToString("N") }; db.Documents.Add(document);
        await db.SaveChangesAsync();
        var link = new CaseEvidenceLink { OrganizationId = org, EudrCaseId = c.Id, CaseDocumentId = document.Id, Purpose = EvidencePurpose.Legality }; db.CaseEvidenceLinks.Add(link); await db.SaveChangesAsync();
        return new(c.Id, dossier.Id, lot.Id, applicability.Id, statement.Id, master.Id, geo.Id, submission.Id, document.Id, link.Id);
    }
    private async Task<World> Seed(Guid org)
    {
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org);
        var p = await SeedCase(db, org);
        var revision = NewRevision(org, p.CaseId, 1, p);
        db.CaseRevisions.Add(revision); await db.SaveChangesAsync();
        db.CaseRevisionDocuments.Add(new CaseRevisionDocument { OrganizationId = org, EudrCaseId = p.CaseId, CaseRevisionId = revision.Id, CaseDocumentId = p.DocumentId });
        db.CaseRevisionEvidenceLinks.Add(new CaseRevisionEvidenceLink { OrganizationId = org, EudrCaseId = p.CaseId, CaseRevisionId = revision.Id, CaseEvidenceLinkId = p.LinkId });
        var review = NewReview(org, p.CaseId, revision.Id, DateTimeOffset.UtcNow); db.ReviewRecords.Add(review);
        await db.SaveChangesAsync();
        return new(org, p.CaseId, p.DossierId, p.LotId, p.ApplicabilityId, p.StatementId, p.MasterDataId, p.GeoId, p.SubmissionId, p.DocumentId, p.LinkId, revision.Id, review.Id);
    }
    private static CaseRevision NewRevision(Guid org, Guid caseId, int number, Pins? p = null) => new()
    {
        OrganizationId = org, EudrCaseId = caseId, Number = number, CaseDossierId = p?.DossierId, CaseLotId = p?.LotId, CaseApplicabilityId = p?.ApplicabilityId, CaseRecipientStatementId = p?.StatementId,
        CaseMasterDataVersionId = p?.MasterDataId, GeoSubmissionId = p?.GeoId, SupplierSubmissionId = p?.SubmissionId, PolicyId = "provena-pilot-completeness", PolicyVersion = "1", SnapshotSchemaVersion = 1,
        FingerprintVersion = 1, Fingerprint = new string('a', 64), SectionHashes = "{}", SnapshotJson = "{}",
    };
    private static ReviewRecord NewReview(Guid org, Guid caseId, Guid revisionId, DateTimeOffset at) => new()
    { OrganizationId = org, EudrCaseId = caseId, CaseRevisionId = revisionId, Decision = ReviewDecision.ReturnedForCorrection, Rationale = "r", ReviewerUserId = Guid.NewGuid(), ReviewedAtUtc = at, PolicyVersion = "1" };

    private async Task Rejected(Guid org, Func<AppDbContext, Task> change)
    {
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org);
        await change(db);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Equal("Review history is immutable; record a new review.", failure.Message);
    }

    // ---- tenant filter and stamping ------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Review_history_is_tenant_filtered_and_cannot_be_written_for_another_organization()
    {
        var a = await Seed(await NewOrganization()); var b = await NewOrganization();
        await using var scope = factory.Services.CreateAsyncScope(); await using var dbB = TenantDb(scope, b);
        Assert.Empty(await dbB.CaseRevisions.ToListAsync()); Assert.Empty(await dbB.CaseRevisionDocuments.ToListAsync()); Assert.Empty(await dbB.CaseRevisionEvidenceLinks.ToListAsync()); Assert.Empty(await dbB.ReviewRecords.ToListAsync());
        dbB.CaseRevisions.Add(NewRevision(a.Org, a.CaseId, 2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => dbB.SaveChangesAsync());
        await using var dbA = TenantDb(scope, a.Org);
        Assert.Equal(1, await dbA.CaseRevisions.CountAsync()); Assert.Equal(1, await dbA.ReviewRecords.CountAsync());
    }

    // ---- immutability ---------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Revisions_pins_and_review_records_cannot_be_updated_or_deleted()
    {
        var w = await Seed(await NewOrganization());
        await Rejected(w.Org, async db => (await db.CaseRevisions.SingleAsync()).SnapshotJson = "{\"x\":1}");
        await Rejected(w.Org, async db => (await db.CaseRevisions.SingleAsync()).Fingerprint = new string('b', 64));
        await Rejected(w.Org, async db => (await db.CaseRevisions.SingleAsync()).Number = 9);
        await Rejected(w.Org, async db => (await db.CaseRevisions.SingleAsync()).CaseApplicabilityId = null);
        await Rejected(w.Org, async db => (await db.CaseRevisions.SingleAsync()).UpdatedAtUtc = DateTimeOffset.UtcNow);
        await Rejected(w.Org, async db => db.CaseRevisions.Remove(await db.CaseRevisions.SingleAsync()));
        await Rejected(w.Org, async db => (await db.CaseRevisionDocuments.SingleAsync()).CaseDocumentId = Guid.NewGuid());
        await Rejected(w.Org, async db => db.CaseRevisionDocuments.Remove(await db.CaseRevisionDocuments.SingleAsync()));
        await Rejected(w.Org, async db => (await db.CaseRevisionEvidenceLinks.SingleAsync()).CaseEvidenceLinkId = Guid.NewGuid());
        await Rejected(w.Org, async db => db.CaseRevisionEvidenceLinks.Remove(await db.CaseRevisionEvidenceLinks.SingleAsync()));
        await Rejected(w.Org, async db => (await db.ReviewRecords.SingleAsync()).Decision = ReviewDecision.AcceptedAsCompleteUnderPolicy);
        await Rejected(w.Org, async db => (await db.ReviewRecords.SingleAsync()).Rationale = "rewritten");
        await Rejected(w.Org, async db => (await db.ReviewRecords.SingleAsync()).ReviewedAtUtc = DateTimeOffset.UtcNow);
        await Rejected(w.Org, async db => db.ReviewRecords.Remove(await db.ReviewRecords.SingleAsync()));
        await using var scope = factory.Services.CreateAsyncScope(); await using var verify = TenantDb(scope, w.Org);
        Assert.Equal("{}", (await verify.CaseRevisions.SingleAsync()).SnapshotJson); Assert.Equal("r", (await verify.ReviewRecords.SingleAsync()).Rationale);
        Assert.Equal(1, await verify.CaseRevisionDocuments.CountAsync()); Assert.Equal(1, await verify.CaseRevisionEvidenceLinks.CountAsync());
    }

    [Fact]
    public async Task Existing_retirement_guards_still_allow_a_pinned_row_to_be_retired_without_touching_the_review()
    {
        var w = await Seed(await NewOrganization());
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, w.Org);
        (await db.Documents.SingleAsync(x => x.Id == w.DocumentId)).WithdrawnAtUtc = DateTimeOffset.UtcNow;
        (await db.CaseApplicabilities.SingleAsync(x => x.Id == w.ApplicabilityId)).SupersededAtUtc = DateTimeOffset.UtcNow;
        (await db.CaseLots.SingleAsync(x => x.Id == w.LotId)).SupersededAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        Assert.Equal(w.DocumentId, (await db.CaseRevisionDocuments.SingleAsync()).CaseDocumentId); // the pin of a later-withdrawn document stays
        Assert.Equal(w.LotId, (await db.CaseRevisions.SingleAsync()).CaseLotId);
    }

    // ---- foreign keys ----------------------------------------------------------------------------------------------------------------------
    private async Task AssertViolation(string sqlState, Func<AppDbContext, Task> insert)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => insert(admin));
        Assert.Equal(sqlState, Assert.IsType<PostgresException>(failure.GetBaseException()).SqlState);
    }
    private Task ForeignKey(Func<AppDbContext, Task> insert) => AssertViolation(PostgresErrorCodes.ForeignKeyViolation, insert);
    private static Task InsertRevision(AppDbContext db, Guid org, Guid caseId, int number, Guid? dossier = null, Guid? lot = null, Guid? applicability = null, Guid? statement = null, Guid? master = null, Guid? geo = null, Guid? submission = null) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "CaseRevisions" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","CaseDossierId","CaseLotId","CaseApplicabilityId","CaseRecipientStatementId","CaseMasterDataVersionId","GeoSubmissionId","SupplierSubmissionId","PolicyId","PolicyVersion","SnapshotSchemaVersion","FingerprintVersion","Fingerprint","SectionHashes","SnapshotJson")
            VALUES ({Guid.NewGuid()}, now(), now(), {org}, {caseId}, {number}, {dossier}, {lot}, {applicability}, {statement}, {master}, {geo}, {submission}, 'p', '1', 1, 1, 'f', '[]'::jsonb, '[]'::jsonb)
            """);

    [Fact]
    public async Task Every_pin_must_belong_to_the_revisions_own_case_and_organization()
    {
        var org = await NewOrganization(); var w = await Seed(org);
        Pins other;
        await using (var scope = factory.Services.CreateAsyncScope()) { await using var db = TenantDb(scope, org); other = await SeedCase(db, org); } // a second case of the same organization
        var foreign = await Seed(await NewOrganization());
        // The consistent pins of the revision's own case are accepted.
        await using (var scope = factory.Services.CreateAsyncScope())
            await InsertRevision(scope.ServiceProvider.GetRequiredService<AppDbContext>(), org, w.CaseId, 5, w.DossierId, w.LotId, w.ApplicabilityId, w.StatementId, w.MasterDataId, w.GeoId, w.SubmissionId);
        // Another case of the same organization.
        await ForeignKey(db => InsertRevision(db, org, w.CaseId, 6, applicability: other.ApplicabilityId));
        await ForeignKey(db => InsertRevision(db, org, w.CaseId, 6, statement: other.StatementId));
        await ForeignKey(db => InsertRevision(db, org, w.CaseId, 6, master: other.MasterDataId));
        await ForeignKey(db => InsertRevision(db, org, w.CaseId, 6, geo: other.GeoId));
        await ForeignKey(db => InsertRevision(db, org, w.CaseId, 6, submission: other.SubmissionId));
        await ForeignKey(db => InsertRevision(db, org, w.CaseId, 6, dossier: other.DossierId, lot: other.LotId)); // dossier and lot of another case
        // The dossier of the case but the lot of another dossier: the dossier-consistency FK of the lot.
        await ForeignKey(db => InsertRevision(db, org, w.CaseId, 6, dossier: w.DossierId, lot: other.LotId));
        // Another organization's rows.
        await ForeignKey(db => InsertRevision(db, org, w.CaseId, 6, applicability: foreign.ApplicabilityId));
        await ForeignKey(db => InsertRevision(db, foreign.Org, w.CaseId, 6)); // another organization's case
        await ForeignKey(db => InsertRevision(db, foreign.Org, foreign.CaseId, 6, dossier: w.DossierId, lot: w.LotId));
        await ForeignKey(db => InsertRevision(db, org, w.CaseId, 6, applicability: Guid.NewGuid()));
    }

    [Fact]
    public async Task Document_link_and_review_record_pins_are_case_and_organization_consistent()
    {
        var org = await NewOrganization(); var w = await Seed(org);
        Pins other;
        await using (var scope = factory.Services.CreateAsyncScope()) { await using var db = TenantDb(scope, org); other = await SeedCase(db, org); }
        var foreign = await Seed(await NewOrganization());
        Task Document(AppDbContext db, Guid o, Guid caseId, Guid revision, Guid document) => db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "CaseRevisionDocuments" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","CaseRevisionId","CaseDocumentId") VALUES ({Guid.NewGuid()}, now(), now(), {o}, {caseId}, {revision}, {document})""");
        Task Link(AppDbContext db, Guid o, Guid caseId, Guid revision, Guid link) => db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "CaseRevisionEvidenceLinks" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","CaseRevisionId","CaseEvidenceLinkId") VALUES ({Guid.NewGuid()}, now(), now(), {o}, {caseId}, {revision}, {link})""");
        Task Review(AppDbContext db, Guid o, Guid caseId, Guid revision) => db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "ReviewRecords" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","CaseRevisionId","Decision","Rationale","ReviewerUserId","ReviewedAtUtc","PolicyVersion") VALUES ({Guid.NewGuid()}, now(), now(), {o}, {caseId}, {revision}, 3, 'r', {Guid.NewGuid()}, now(), '1')""");
        await ForeignKey(db => Document(db, org, w.CaseId, w.RevisionId, other.DocumentId)); // a document of another case
        await ForeignKey(db => Document(db, org, w.CaseId, w.RevisionId, foreign.DocumentId)); // another organization's document
        await ForeignKey(db => Document(db, org, other.CaseId, w.RevisionId, other.DocumentId)); // a revision of another case
        await ForeignKey(db => Document(db, foreign.Org, foreign.CaseId, w.RevisionId, foreign.DocumentId));
        await ForeignKey(db => Link(db, org, w.CaseId, w.RevisionId, other.LinkId));
        await ForeignKey(db => Link(db, org, w.CaseId, w.RevisionId, foreign.LinkId));
        await ForeignKey(db => Link(db, org, other.CaseId, w.RevisionId, other.LinkId));
        // A revision without a review yet, so the unique one-review-per-revision index cannot mask the foreign keys.
        Guid fresh; await using (var scope = factory.Services.CreateAsyncScope()) { await using var db = TenantDb(scope, org); var revision = NewRevision(org, w.CaseId, 2); db.CaseRevisions.Add(revision); await db.SaveChangesAsync(); fresh = revision.Id; }
        await ForeignKey(db => Review(db, org, other.CaseId, fresh)); // a review of another case on this revision
        await ForeignKey(db => Review(db, foreign.Org, foreign.CaseId, fresh));
        await ForeignKey(db => Review(db, org, w.CaseId, Guid.NewGuid()));
        await Review(factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>(), org, w.CaseId, fresh); // the consistent review is accepted
    }

    // ---- CHECK constraint ------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Dossier_and_lot_pins_are_both_null_or_both_set()
    {
        var org = await NewOrganization(); var w = await Seed(org);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await InsertRevision(admin, org, w.CaseId, 10); // both null
            await InsertRevision(admin, org, w.CaseId, 11, w.DossierId, w.LotId); // both set, consistent
        }
        await AssertViolation(PostgresErrorCodes.CheckViolation, db => InsertRevision(db, org, w.CaseId, 12, dossier: w.DossierId, lot: null));
        await AssertViolation(PostgresErrorCodes.CheckViolation, db => InsertRevision(db, org, w.CaseId, 12, dossier: null, lot: w.LotId));
        await using var verify = factory.Services.CreateAsyncScope();
        var name = await verify.ServiceProvider.GetRequiredService<AppDbContext>().Database.SqlQueryRaw<string>("""SELECT conname AS "Value" FROM pg_constraint WHERE conname = 'CK_CaseRevisions_DossierLotPair'""").SingleAsync();
        Assert.Equal("CK_CaseRevisions_DossierLotPair", name);
    }

    // ---- indexes ---------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Revision_numbers_are_unique_per_case_and_each_revision_has_at_most_one_review_but_timestamps_may_collide()
    {
        var org = await NewOrganization(); var w = await Seed(org);
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org);

        db.CaseRevisions.Add(NewRevision(org, w.CaseId, 1));
        var duplicateNumber = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("IX_CaseRevisions_EudrCaseId_Number", duplicateNumber.ToString()); Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(duplicateNumber.GetBaseException()).SqlState);
        db.ChangeTracker.Clear();

        db.ReviewRecords.Add(NewReview(org, w.CaseId, w.RevisionId, DateTimeOffset.UtcNow));
        var duplicateReview = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("IX_ReviewRecords_CaseRevisionId", duplicateReview.ToString());
        db.ChangeTracker.Clear();

        db.CaseRevisionDocuments.Add(new CaseRevisionDocument { OrganizationId = org, EudrCaseId = w.CaseId, CaseRevisionId = w.RevisionId, CaseDocumentId = w.DocumentId });
        Assert.Contains("IX_CaseRevisionDocuments_CaseRevisionId_CaseDocumentId", (await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).ToString());
        db.ChangeTracker.Clear();
        db.CaseRevisionEvidenceLinks.Add(new CaseRevisionEvidenceLink { OrganizationId = org, EudrCaseId = w.CaseId, CaseRevisionId = w.RevisionId, CaseEvidenceLinkId = w.LinkId });
        Assert.Contains("IX_CaseRevisionEvidenceLinks_CaseRevisionId_CaseEvidenceLinkId", (await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).ToString());
        db.ChangeTracker.Clear();

        // Two reviews of one case with the very same ReviewedAtUtc are accepted: "latest" is the revision Number, never a timestamp.
        var at = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var second = NewRevision(org, w.CaseId, 2); var third = NewRevision(org, w.CaseId, 3);
        db.CaseRevisions.AddRange(second, third); await db.SaveChangesAsync();
        db.ReviewRecords.AddRange(NewReview(org, w.CaseId, second.Id, at), NewReview(org, w.CaseId, third.Id, at)); await db.SaveChangesAsync();
        Assert.Equal(2, await db.ReviewRecords.CountAsync(x => x.ReviewedAtUtc == at));
        Assert.Equal(3, await db.CaseRevisions.MaxAsync(x => x.Number));
    }
}
