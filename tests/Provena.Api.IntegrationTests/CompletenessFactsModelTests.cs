using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Provena.Api.Auth;
using Provena.Api.Data;
using Provena.Api.Domain;
using Xunit;
namespace Provena.Api.IntegrationTests;

// Slice 5 / commit 1: persistence model and guards for the operator profile, case master-data copy, applicability, recipients and evidence links.
// The guards are application-level (SaveChanges); raw SQL and bulk writes bypass them, which is why the database constraints are tested separately.
[Collection("Api")]
public sealed class CompletenessFactsModelTests(ApiFactory factory)
{
    private sealed record World(Guid OrgId, Guid CaseId, Guid SupplierId, Guid ProductId, Guid DocumentId, Guid MasterDataId, Guid ApplicabilityId, Guid StatementId, Guid RecipientId, Guid LinkId, Guid ProfileId);

    private async Task<Guid> NewOrganization()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new Organization { Name = "Facts Org " + Guid.NewGuid().ToString("N") };
        db.Organizations.Add(org); await db.SaveChangesAsync();
        return org.Id;
    }
    private static AppDbContext TenantDb(IServiceScope scope, Guid organizationId)
    {
        var tenant = new TenantContext(new HttpContextAccessor()); tenant.SetOrganization(organizationId);
        return new AppDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>(), tenant);
    }
    private static async Task<(Guid Case, Guid Supplier, Guid Product)> SeedCase(AppDbContext db, Guid org)
    {
        var supplier = new Supplier { OrganizationId = org, Name = "Supplier" };
        var product = new Product { OrganizationId = org, Name = "Product", CnCode = "0901" };
        db.Suppliers.Add(supplier); db.Products.Add(product); await db.SaveChangesAsync();
        var shipment = new Shipment { OrganizationId = org, ReferenceNumber = "SHP-" + Guid.NewGuid().ToString("N"), SupplierId = supplier.Id, ProductId = product.Id, Quantity = 10, Unit = QuantityUnit.Kg };
        db.Shipments.Add(shipment); await db.SaveChangesAsync();
        var eudrCase = new EudrCase { OrganizationId = org, ShipmentId = shipment.Id };
        db.Cases.Add(eudrCase); await db.SaveChangesAsync();
        return (eudrCase.Id, supplier.Id, product.Id);
    }
    private static CaseDocument NewDocument(Guid org, Guid caseId) => new()
    {
        OrganizationId = org, EudrCaseId = caseId, OriginalFileName = "a.pdf", StoredFileName = "a.pdf", ContentType = "application/pdf", SizeBytes = 1, Category = DocumentCategory.Other,
        StorageKey = "key-" + Guid.NewGuid().ToString("N"), UploadedByType = UploadedByType.InternalUser,
    };
    private static CaseMasterDataVersion NewMasterData(Guid org, Guid caseId, Guid supplier, Guid product, int number, DateTimeOffset? superseded = null) => new()
    {
        OrganizationId = org, EudrCaseId = caseId, Number = number, SupplierId = supplier, SupplierName = "Supplier", ProductId = product, ProductCnCode = "0901", ProductCommodity = CommodityType.Coffee, ProductName = "Product", SupersededAtUtc = superseded,
    };
    private static CaseApplicability NewApplicability(Guid org, Guid caseId, Guid product, int number, DateTimeOffset? superseded = null) => new()
    {
        OrganizationId = org, EudrCaseId = caseId, Number = number, AssessedProductId = product, AssessedCnCode = "0901", AssessedCommodity = CommodityType.Coffee, SupersededAtUtc = superseded,
    };
    private async Task<World> Seed(Guid org)
    {
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org);
        var (caseId, supplier, product) = await SeedCase(db, org);
        var document = NewDocument(org, caseId); db.Documents.Add(document);
        var profile = new OperatorProfile { OrganizationId = org, LegalName = "Op", PostalAddress = "Addr", Email = "op@example.test", EstablishmentCountryCode = "HR" };
        var master = NewMasterData(org, caseId, supplier, product, 1); var applicability = NewApplicability(org, caseId, product, 1);
        var statement = new CaseRecipientStatement { OrganizationId = org, EudrCaseId = caseId, Number = 1, Resolution = RecipientResolution.RecipientsIdentified };
        db.OperatorProfiles.Add(profile); db.CaseMasterDataVersions.Add(master); db.CaseApplicabilities.Add(applicability); db.CaseRecipientStatements.Add(statement);
        await db.SaveChangesAsync();
        var recipient = new CaseRecipient { OrganizationId = org, CaseRecipientStatementId = statement.Id, Name = "R", PostalAddress = "A", Email = "r@example.test" };
        var link = new CaseEvidenceLink { OrganizationId = org, EudrCaseId = caseId, CaseDocumentId = document.Id, Purpose = EvidencePurpose.Legality };
        db.CaseRecipients.Add(recipient); db.CaseEvidenceLinks.Add(link); await db.SaveChangesAsync();
        return new(org, caseId, supplier, product, document.Id, master.Id, applicability.Id, statement.Id, recipient.Id, link.Id, profile.Id);
    }
    private async Task Rejected(Guid org, Func<AppDbContext, Task> change)
    {
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org);
        await change(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
    private async Task Retire<T>(Guid org, Guid id, Action<T> retire) where T : TenantEntity
    {
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org);
        retire(await db.Set<T>().SingleAsync(x => x.Id == id)); await db.SaveChangesAsync();
    }

    // ---- guards -------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Master_data_versions_are_immutable_but_allow_one_way_supersede()
    {
        var w = await Seed(await NewOrganization());
        Task Edit(Action<CaseMasterDataVersion> change) => Rejected(w.OrgId, async db => change(await db.CaseMasterDataVersions.SingleAsync(x => x.Id == w.MasterDataId)));
        await Edit(x => x.SupplierName = "x"); await Edit(x => x.OperatorLegalName = "x"); await Edit(x => x.ProductCnCode = "1801"); await Edit(x => x.ProductCommodity = CommodityType.Soy);
        await Edit(x => x.ProductName = "x"); await Edit(x => x.ProductCatalogueDescription = "x"); await Edit(x => x.SupplierId = Guid.NewGuid()); await Edit(x => x.ProductId = Guid.NewGuid());
        await Edit(x => x.Number = 9); await Edit(x => x.CreatedByUserId = Guid.NewGuid()); await Edit(x => x.EudrCaseId = Guid.NewGuid());
        await Rejected(w.OrgId, async db => db.CaseMasterDataVersions.Remove(await db.CaseMasterDataVersions.SingleAsync(x => x.Id == w.MasterDataId)));
        await Edit(x => { x.SupersededAtUtc = DateTimeOffset.UtcNow; x.SupplierName = "x"; }); // retiring while editing a fact is not a clean retirement
        await Retire<CaseMasterDataVersion>(w.OrgId, w.MasterDataId, x => x.SupersededAtUtc = DateTimeOffset.UtcNow);
        await Edit(x => x.SupersededAtUtc = null); await Edit(x => x.SupersededAtUtc = DateTimeOffset.UtcNow.AddDays(1));
        await using var scope = factory.Services.CreateAsyncScope(); await using var verify = TenantDb(scope, w.OrgId);
        Assert.Equal(1, await verify.CaseMasterDataVersions.CountAsync()); Assert.Empty(await verify.CaseMasterDataVersions.Current().ToListAsync());
    }

    [Fact]
    public async Task Applicability_versions_are_immutable_but_allow_one_way_supersede()
    {
        var w = await Seed(await NewOrganization());
        Task Edit(Action<CaseApplicability> change) => Rejected(w.OrgId, async db => change(await db.CaseApplicabilities.SingleAsync(x => x.Id == w.ApplicabilityId)));
        await Edit(x => x.Reasoning = "x"); await Edit(x => x.RegulatoryScope = RegulatoryScope.InScope); await Edit(x => x.FlowSupport = FlowSupport.Supported); await Edit(x => x.AssessedProductId = Guid.NewGuid());
        await Edit(x => x.AssessedCnCode = "1801"); await Edit(x => x.AssessedCommodity = CommodityType.Soy); await Edit(x => x.OperatorRole = OperatorRole.OtherUnsupported); await Edit(x => x.OperatorRoleDescription = "x");
        await Edit(x => x.DecidedByUserId = Guid.NewGuid()); await Edit(x => x.DecidedAtUtc = DateTimeOffset.UtcNow); await Edit(x => x.Number = 9);
        await Rejected(w.OrgId, async db => db.CaseApplicabilities.Remove(await db.CaseApplicabilities.SingleAsync(x => x.Id == w.ApplicabilityId)));
        await Edit(x => { x.SupersededAtUtc = DateTimeOffset.UtcNow; x.Reasoning = "x"; });
        await Retire<CaseApplicability>(w.OrgId, w.ApplicabilityId, x => x.SupersededAtUtc = DateTimeOffset.UtcNow);
        await Edit(x => x.SupersededAtUtc = null);
        await using var scope = factory.Services.CreateAsyncScope(); await using var verify = TenantDb(scope, w.OrgId);
        Assert.Empty(await verify.CaseApplicabilities.Current().ToListAsync());
    }

    [Fact]
    public async Task Recipient_statements_are_immutable_but_allow_one_way_supersede_and_recipients_are_fully_immutable()
    {
        var w = await Seed(await NewOrganization());
        Task EditStatement(Action<CaseRecipientStatement> change) => Rejected(w.OrgId, async db => change(await db.CaseRecipientStatements.SingleAsync(x => x.Id == w.StatementId)));
        await EditStatement(x => x.Resolution = RecipientResolution.NotApplicable); await EditStatement(x => x.NotApplicableBasis = "x"); await EditStatement(x => x.Number = 9);
        await Rejected(w.OrgId, async db => db.CaseRecipientStatements.Remove(await db.CaseRecipientStatements.SingleAsync(x => x.Id == w.StatementId)));
        Task EditRecipient(Action<CaseRecipient> change) => Rejected(w.OrgId, async db => change(await db.CaseRecipients.SingleAsync(x => x.Id == w.RecipientId)));
        await EditRecipient(x => x.Name = "x"); await EditRecipient(x => x.PostalAddress = "x"); await EditRecipient(x => x.Email = "x@example.test"); await EditRecipient(x => x.CaseRecipientStatementId = Guid.NewGuid());
        await EditRecipient(x => x.UpdatedAtUtc = DateTimeOffset.UtcNow);
        await Rejected(w.OrgId, async db => db.CaseRecipients.Remove(await db.CaseRecipients.SingleAsync(x => x.Id == w.RecipientId)));
        await Retire<CaseRecipientStatement>(w.OrgId, w.StatementId, x => x.SupersededAtUtc = DateTimeOffset.UtcNow);
        await EditStatement(x => x.SupersededAtUtc = null);
        await using var scope = factory.Services.CreateAsyncScope(); await using var verify = TenantDb(scope, w.OrgId);
        Assert.Equal(1, await verify.CaseRecipients.CountAsync()); Assert.Empty(await verify.CaseRecipientStatements.Current().ToListAsync());
    }

    [Fact]
    public async Task Evidence_links_are_immutable_but_allow_one_way_withdrawal_with_its_companion()
    {
        var w = await Seed(await NewOrganization());
        Task Edit(Action<CaseEvidenceLink> change) => Rejected(w.OrgId, async db => change(await db.CaseEvidenceLinks.SingleAsync(x => x.Id == w.LinkId)));
        await Edit(x => x.Purpose = EvidencePurpose.DeforestationFree); await Edit(x => x.Note = "x"); await Edit(x => x.CaseDocumentId = Guid.NewGuid()); await Edit(x => x.CreatedByUserId = Guid.NewGuid());
        await Edit(x => x.WithdrawnByUserId = Guid.NewGuid()); // the companion alone is not a withdrawal
        await Edit(x => { x.WithdrawnAtUtc = DateTimeOffset.UtcNow; x.Note = "x"; });
        await Rejected(w.OrgId, async db => db.CaseEvidenceLinks.Remove(await db.CaseEvidenceLinks.SingleAsync(x => x.Id == w.LinkId)));
        var user = Guid.NewGuid();
        await Retire<CaseEvidenceLink>(w.OrgId, w.LinkId, x => { x.WithdrawnAtUtc = DateTimeOffset.UtcNow; x.WithdrawnByUserId = user; });
        await Edit(x => x.WithdrawnAtUtc = null); await Edit(x => x.WithdrawnByUserId = null); await Edit(x => x.WithdrawnAtUtc = DateTimeOffset.UtcNow.AddDays(1));
        await using var scope = factory.Services.CreateAsyncScope(); await using var verify = TenantDb(scope, w.OrgId);
        var link = await verify.CaseEvidenceLinks.SingleAsync(); Assert.Equal(user, link.WithdrawnByUserId); Assert.Empty(await verify.CaseEvidenceLinks.Current().ToListAsync());
    }

    [Fact]
    public async Task Operator_profile_is_mutable_but_never_deleted_and_unique_per_organization()
    {
        var w = await Seed(await NewOrganization());
        await using var scope = factory.Services.CreateAsyncScope();
        await using (var db = TenantDb(scope, w.OrgId))
        { var profile = await db.OperatorProfiles.SingleAsync(); profile.LegalName = "Renamed"; profile.EoriNumber = "HR1"; await db.SaveChangesAsync(); }
        await Rejected(w.OrgId, async db => db.OperatorProfiles.Remove(await db.OperatorProfiles.SingleAsync()));
        await using (var db = TenantDb(scope, w.OrgId))
        {
            db.OperatorProfiles.Add(new OperatorProfile { OrganizationId = w.OrgId, LegalName = "Second", PostalAddress = "A", Email = "a@example.test", EstablishmentCountryCode = "HR" });
            var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("IX_OperatorProfiles_OrganizationId", duplicate.ToString());
        }
        await using var verify = TenantDb(scope, w.OrgId);
        Assert.Equal("Renamed", (await verify.OperatorProfiles.SingleAsync()).LegalName);
    }

    // ---- partial unique current indexes ---------------------------------------------------------------------------------------------------
    [Fact]
    public async Task One_current_version_per_case_and_unique_numbers_for_each_versioned_fact()
    {
        var w = await Seed(await NewOrganization());
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, w.OrgId);
        async Task Duplicate(string index, Action add)
        {
            add(); var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains(index, failure.ToString()); Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(failure.GetBaseException()).SqlState); db.ChangeTracker.Clear();
        }
        await Duplicate("IX_CaseMasterDataVersions_EudrCaseId", () => db.CaseMasterDataVersions.Add(NewMasterData(w.OrgId, w.CaseId, w.SupplierId, w.ProductId, 2)));
        await Duplicate("IX_CaseMasterDataVersions_EudrCaseId_Number", () => db.CaseMasterDataVersions.Add(NewMasterData(w.OrgId, w.CaseId, w.SupplierId, w.ProductId, 1, DateTimeOffset.UtcNow)));
        await Duplicate("IX_CaseApplicabilities_EudrCaseId", () => db.CaseApplicabilities.Add(NewApplicability(w.OrgId, w.CaseId, w.ProductId, 2)));
        await Duplicate("IX_CaseApplicabilities_EudrCaseId_Number", () => db.CaseApplicabilities.Add(NewApplicability(w.OrgId, w.CaseId, w.ProductId, 1, DateTimeOffset.UtcNow)));
        await Duplicate("IX_CaseRecipientStatements_EudrCaseId", () => db.CaseRecipientStatements.Add(new CaseRecipientStatement { OrganizationId = w.OrgId, EudrCaseId = w.CaseId, Number = 2, Resolution = RecipientResolution.NotApplicable, NotApplicableBasis = "b" }));
        await Duplicate("IX_CaseRecipientStatements_EudrCaseId_Number", () => db.CaseRecipientStatements.Add(new CaseRecipientStatement { OrganizationId = w.OrgId, EudrCaseId = w.CaseId, Number = 1, Resolution = RecipientResolution.NotApplicable, NotApplicableBasis = "b", SupersededAtUtc = DateTimeOffset.UtcNow }));

        // Superseded versions coexist with the current one under their own numbers.
        db.CaseMasterDataVersions.Add(NewMasterData(w.OrgId, w.CaseId, w.SupplierId, w.ProductId, 2, DateTimeOffset.UtcNow));
        db.CaseApplicabilities.Add(NewApplicability(w.OrgId, w.CaseId, w.ProductId, 2, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        Assert.Single(await db.CaseMasterDataVersions.Current().ToListAsync()); Assert.Equal(2, await db.CaseMasterDataVersions.CountAsync());
    }

    [Fact]
    public async Task One_current_evidence_link_per_document_and_purpose_and_withdrawal_frees_the_slot()
    {
        var w = await Seed(await NewOrganization());
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, w.OrgId);
        db.CaseEvidenceLinks.Add(new CaseEvidenceLink { OrganizationId = w.OrgId, EudrCaseId = w.CaseId, CaseDocumentId = w.DocumentId, Purpose = EvidencePurpose.Legality });
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("IX_CaseEvidenceLinks_EudrCaseId_CaseDocumentId_Purpose", duplicate.ToString()); db.ChangeTracker.Clear();

        // Another purpose for the same document is a different slot.
        db.CaseEvidenceLinks.Add(new CaseEvidenceLink { OrganizationId = w.OrgId, EudrCaseId = w.CaseId, CaseDocumentId = w.DocumentId, Purpose = EvidencePurpose.DeforestationFree });
        await db.SaveChangesAsync();
        // A withdrawn link coexists with a current one, and a second withdrawn duplicate is fine too.
        var first = await db.CaseEvidenceLinks.SingleAsync(x => x.Id == w.LinkId);
        first.WithdrawnAtUtc = DateTimeOffset.UtcNow; first.WithdrawnByUserId = Guid.NewGuid(); await db.SaveChangesAsync();
        db.CaseEvidenceLinks.Add(new CaseEvidenceLink { OrganizationId = w.OrgId, EudrCaseId = w.CaseId, CaseDocumentId = w.DocumentId, Purpose = EvidencePurpose.Legality });
        await db.SaveChangesAsync();
        Assert.Equal(3, await db.CaseEvidenceLinks.CountAsync()); Assert.Equal(2, await db.CaseEvidenceLinks.Current().CountAsync());
    }

    // ---- foreign keys (raw SQL bypasses the application guards on purpose) -----------------------------------------------------------------
    private async Task AssertForeignKeyViolation(Func<AppDbContext, Task> insert)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => insert(admin));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(failure.GetBaseException()).SqlState);
    }
    private static Task InsertMasterData(AppDbContext db, Guid org, Guid caseId, Guid supplier, Guid product, int number) => db.Database.ExecuteSqlInterpolatedAsync(
        $"""INSERT INTO "CaseMasterDataVersions" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","SupplierId","SupplierName","ProductId","ProductCnCode","ProductCommodity","ProductName","SupersededAtUtc") VALUES ({Guid.NewGuid()}, now(), now(), {org}, {caseId}, {number}, {supplier}, 'S', {product}, '0901', 0, 'P', now())""");
    private static Task InsertApplicability(AppDbContext db, Guid org, Guid caseId, Guid assessedProduct, int number) => db.Database.ExecuteSqlInterpolatedAsync(
        $"""INSERT INTO "CaseApplicabilities" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","AssessedProductId","AssessedCnCode","AssessedCommodity","SingleRelevantCommodity","ActivityType","OperatorRole","ActivityDateKind","RegulatoryScope","FlowSupport","SupersededAtUtc") VALUES ({Guid.NewGuid()}, now(), now(), {org}, {caseId}, {number}, {assessedProduct}, '0901', 0, true, 0, 0, 0, 0, 0, now())""");
    private static Task InsertLink(AppDbContext db, Guid org, Guid caseId, Guid document, int purpose) => db.Database.ExecuteSqlInterpolatedAsync(
        $"""INSERT INTO "CaseEvidenceLinks" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","CaseDocumentId","Purpose") VALUES ({Guid.NewGuid()}, now(), now(), {org}, {caseId}, {document}, {purpose})""");

    [Fact]
    public async Task Master_data_and_applicability_foreign_keys_reject_other_organization_suppliers_and_products()
    {
        var a = await Seed(await NewOrganization()); var b = await Seed(await NewOrganization());
        // The consistent same-organization references are accepted.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await InsertMasterData(admin, a.OrgId, a.CaseId, a.SupplierId, a.ProductId, 5); await InsertApplicability(admin, a.OrgId, a.CaseId, a.ProductId, 5);
        }
        await AssertForeignKeyViolation(db => InsertMasterData(db, a.OrgId, a.CaseId, b.SupplierId, a.ProductId, 6));
        await AssertForeignKeyViolation(db => InsertMasterData(db, a.OrgId, a.CaseId, a.SupplierId, b.ProductId, 6));
        await AssertForeignKeyViolation(db => InsertMasterData(db, a.OrgId, a.CaseId, Guid.NewGuid(), a.ProductId, 6));
        await AssertForeignKeyViolation(db => InsertMasterData(db, b.OrgId, a.CaseId, b.SupplierId, b.ProductId, 6)); // another organization's case
        await AssertForeignKeyViolation(db => InsertApplicability(db, a.OrgId, a.CaseId, b.ProductId, 6)); // AssessedProductId of another organization
        await AssertForeignKeyViolation(db => InsertApplicability(db, a.OrgId, a.CaseId, Guid.NewGuid(), 6));
        await AssertForeignKeyViolation(db => InsertApplicability(db, b.OrgId, a.CaseId, b.ProductId, 6));
    }

    [Fact]
    public async Task Evidence_link_foreign_keys_require_a_document_of_the_same_case_and_organization()
    {
        var a = await Seed(await NewOrganization()); var b = await Seed(await NewOrganization());
        Guid otherCaseDocument, otherCase;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await using var db = TenantDb(scope, a.OrgId);
            (otherCase, _, _) = await SeedCase(db, a.OrgId);
            var document = NewDocument(a.OrgId, otherCase); db.Documents.Add(document); await db.SaveChangesAsync(); otherCaseDocument = document.Id;
        }
        // The document's own case is accepted (a distinct purpose, so the unique current-link index is not hit).
        await using (var scope = factory.Services.CreateAsyncScope()) await InsertLink(scope.ServiceProvider.GetRequiredService<AppDbContext>(), a.OrgId, a.CaseId, a.DocumentId, 3);
        await AssertForeignKeyViolation(db => InsertLink(db, a.OrgId, a.CaseId, otherCaseDocument, 0)); // a document of another case of the same organization
        await AssertForeignKeyViolation(db => InsertLink(db, a.OrgId, otherCase, a.DocumentId, 0));
        await AssertForeignKeyViolation(db => InsertLink(db, a.OrgId, a.CaseId, b.DocumentId, 0)); // another organization's document
        await AssertForeignKeyViolation(db => InsertLink(db, b.OrgId, b.CaseId, a.DocumentId, 0));
        await AssertForeignKeyViolation(db => InsertLink(db, a.OrgId, a.CaseId, Guid.NewGuid(), 0));
        await AssertForeignKeyViolation(db => InsertLink(db, a.OrgId, b.CaseId, a.DocumentId, 0)); // another organization's case
    }

    [Fact]
    public async Task Recipient_and_statement_foreign_keys_are_tenant_and_case_safe()
    {
        var a = await Seed(await NewOrganization()); var b = await Seed(await NewOrganization());
        await AssertForeignKeyViolation(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "CaseRecipients" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","CaseRecipientStatementId","Name","PostalAddress","Email") VALUES ({Guid.NewGuid()}, now(), now(), {b.OrgId}, {a.StatementId}, 'N', 'A', 'e@example.test')"""));
        await AssertForeignKeyViolation(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "CaseRecipients" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","CaseRecipientStatementId","Name","PostalAddress","Email") VALUES ({Guid.NewGuid()}, now(), now(), {a.OrgId}, {Guid.NewGuid()}, 'N', 'A', 'e@example.test')"""));
        await AssertForeignKeyViolation(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "CaseRecipientStatements" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","Resolution","SupersededAtUtc") VALUES ({Guid.NewGuid()}, now(), now(), {b.OrgId}, {a.CaseId}, 7, 1, now())"""));
    }

    // ---- tenant filter and organization stamp guard -----------------------------------------------------------------------------------------
    [Fact]
    public async Task Tenant_filter_and_organization_stamp_guard_apply_to_the_completeness_fact_types()
    {
        var a = await Seed(await NewOrganization()); var b = await NewOrganization();
        await using var scope = factory.Services.CreateAsyncScope(); await using var dbB = TenantDb(scope, b);
        Assert.Empty(await dbB.OperatorProfiles.ToListAsync()); Assert.Empty(await dbB.CaseMasterDataVersions.ToListAsync()); Assert.Empty(await dbB.CaseApplicabilities.ToListAsync());
        Assert.Empty(await dbB.CaseRecipientStatements.ToListAsync()); Assert.Empty(await dbB.CaseRecipients.ToListAsync()); Assert.Empty(await dbB.CaseEvidenceLinks.ToListAsync());
        dbB.CaseMasterDataVersions.Add(NewMasterData(a.OrgId, a.CaseId, a.SupplierId, a.ProductId, 9));
        await Assert.ThrowsAsync<InvalidOperationException>(() => dbB.SaveChangesAsync());
        dbB.ChangeTracker.Clear();
        dbB.OperatorProfiles.Add(new OperatorProfile { OrganizationId = a.OrgId, LegalName = "x", PostalAddress = "x", Email = "x@example.test", EstablishmentCountryCode = "HR" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => dbB.SaveChangesAsync());
    }

    [Fact]
    public async Task Supplier_postal_address_is_nullable_and_bounded_in_the_model()
    {
        var org = await NewOrganization();
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org);
        var supplier = new Supplier { OrganizationId = org, Name = "S" };
        db.Suppliers.Add(supplier); await db.SaveChangesAsync();
        Assert.Null((await db.Suppliers.AsNoTracking().SingleAsync()).PostalAddress);
        supplier.PostalAddress = new string('x', 1000); await db.SaveChangesAsync();
        supplier.PostalAddress = new string('x', 1001);
        var tooLong = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, Assert.IsType<PostgresException>(tooLong.GetBaseException()).SqlState);
    }
}
