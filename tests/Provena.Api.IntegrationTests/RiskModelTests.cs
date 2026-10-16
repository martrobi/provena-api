using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Provena.Api.Auth;
using Provena.Api.Data;
using Provena.Api.Domain;
using Xunit;
namespace Provena.Api.IntegrationTests;

// Risk Assessment & Mitigation v1: persistence guards and database constraints. The guards are application-level (SaveChanges); raw SQL deliberately bypasses them, which is why the
// constraints are tested separately with raw SQL against the real PostgreSQL schema - above all that a closure on review R2 cannot reference an assessment made on review R1.
[Collection("Api")]
public sealed class RiskModelTests(ApiFactory factory)
{
    private const string ImmutableMessage = "Risk history is immutable; record a new risk assessment or mitigation measure.";
    private const string ClosureAssessmentFk = "FK_CaseClosures_RiskAssessments_OrganizationId_EudrCaseId_Revi~";
    private const string AssessmentReviewFk = "FK_RiskAssessments_ReviewRecords_OrganizationId_EudrCaseId_Rev~";
    private sealed record World(Guid Org, Guid CaseId, Guid Review1, Guid Review2, Guid OtherCaseId, Guid OtherReview, Guid DocumentId, Guid OtherCaseDocumentId, Guid Assessment1, Guid Assessment2, Guid OtherAssessment, Guid MeasureId);

    private async Task<Guid> NewOrganization()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new Organization { Name = "Risk Org " + Guid.NewGuid().ToString("N") };
        db.Organizations.Add(org); await db.SaveChangesAsync();
        return org.Id;
    }
    private static AppDbContext TenantDb(IServiceScope scope, Guid org)
    {
        var tenant = new TenantContext(new HttpContextAccessor()); tenant.SetOrganization(org);
        return new AppDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>(), tenant);
    }
    private static async Task<(Guid CaseId, Guid[] Reviews, Guid DocumentId)> SeedCase(AppDbContext db, Guid org, int reviews)
    {
        var supplier = new Supplier { OrganizationId = org, Name = "Supplier" }; var product = new Product { OrganizationId = org, Name = "Product", CnCode = "0901" };
        db.Suppliers.Add(supplier); db.Products.Add(product); await db.SaveChangesAsync();
        var shipment = new Shipment { OrganizationId = org, ReferenceNumber = "SHP-" + Guid.NewGuid().ToString("N"), SupplierId = supplier.Id, ProductId = product.Id, Quantity = 10, Unit = QuantityUnit.Kg };
        db.Shipments.Add(shipment); await db.SaveChangesAsync();
        var c = new EudrCase { OrganizationId = org, ShipmentId = shipment.Id }; db.Cases.Add(c); await db.SaveChangesAsync();
        var document = new CaseDocument { OrganizationId = org, EudrCaseId = c.Id, OriginalFileName = "a.txt", StoredFileName = Guid.NewGuid().ToString("N"), ContentType = "text/plain", SizeBytes = 1, StorageKey = "k/" + Guid.NewGuid().ToString("N") };
        db.Documents.Add(document); await db.SaveChangesAsync();
        var ids = new List<Guid>();
        for (var n = 1; n <= reviews; n++)
        {
            var revision = new CaseRevision
            {
                OrganizationId = org, EudrCaseId = c.Id, Number = n, PolicyId = "provena-pilot-completeness", PolicyVersion = "1", SnapshotSchemaVersion = 1,
                FingerprintVersion = 1, Fingerprint = new string('a', 64), SectionHashes = "{}", SnapshotJson = "{}",
            };
            db.CaseRevisions.Add(revision); await db.SaveChangesAsync();
            var review = new ReviewRecord { OrganizationId = org, EudrCaseId = c.Id, CaseRevisionId = revision.Id, Decision = ReviewDecision.AcceptedAsCompleteUnderPolicy, Rationale = "r", ReviewerUserId = Guid.NewGuid(), ReviewedAtUtc = DateTimeOffset.UtcNow, PolicyVersion = "1" };
            db.ReviewRecords.Add(review); await db.SaveChangesAsync(); ids.Add(review.Id);
        }
        return (c.Id, ids.ToArray(), document.Id);
    }
    private static RiskAssessment NewAssessment(Guid org, Guid caseId, Guid review, int number, RiskConclusion conclusion = RiskConclusion.Negligible) => new()
    {
        OrganizationId = org, EudrCaseId = caseId, Number = number, ReviewRecordId = review, CriteriaCatalogueId = "provena-pilot-risk-criteria", CriteriaCatalogueVersion = "1",
        Conclusion = conclusion, Rationale = "Reasoned.", AssessedByUserId = Guid.NewGuid(), AssessedAtUtc = DateTimeOffset.UtcNow,
    };
    private async Task<World> Seed()
    {
        var org = await NewOrganization();
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org);
        var (caseId, reviews, documentId) = await SeedCase(db, org, 2); var (otherCase, otherReviews, otherDocument) = await SeedCase(db, org, 1);
        var a1 = NewAssessment(org, caseId, reviews[0], 1, RiskConclusion.NotNegligible); var a2 = NewAssessment(org, caseId, reviews[1], 2); var other = NewAssessment(org, otherCase, otherReviews[0], 1);
        db.RiskAssessments.AddRange(a1, a2, other); await db.SaveChangesAsync();
        db.RiskAssessmentFindings.Add(new RiskAssessmentFinding { OrganizationId = org, EudrCaseId = caseId, RiskAssessmentId = a1.Id, CriterionCode = "EVIDENCE_RELIABILITY", Finding = RiskCriterionFinding.Concern, Note = "n" });
        var measure = new RiskMitigationMeasure { OrganizationId = org, EudrCaseId = caseId, Number = 1, RiskAssessmentId = a1.Id, Description = "d", PerformedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1), RecordedByUserId = Guid.NewGuid() };
        db.RiskMitigationMeasures.Add(measure); await db.SaveChangesAsync();
        return new(org, caseId, reviews[0], reviews[1], otherCase, otherReviews[0], documentId, otherDocument, a1.Id, a2.Id, other.Id, measure.Id);
    }

    // ---- enum and entity shape ----------------------------------------------------------------------------------------------------------
    [Fact]
    public void The_enums_are_small_and_the_entities_carry_no_workflow_status_or_task_fields()
    {
        Assert.Equal(["Negligible", "NotNegligible"], Enum.GetNames<RiskConclusion>()); Assert.Equal([0, 1], Enum.GetValues<RiskConclusion>().Select(x => (int)x).ToArray());
        Assert.Equal(["NoConcern", "Concern", "InsufficientInformation"], Enum.GetNames<RiskCriterionFinding>()); Assert.Equal([0, 1, 2], Enum.GetValues<RiskCriterionFinding>().Select(x => (int)x).ToArray());
        foreach (var type in new[] { typeof(RiskAssessment), typeof(RiskAssessmentFinding), typeof(RiskMitigationMeasure) })
            Assert.DoesNotContain(type.GetProperties(), p => p.Name is "Status" or "Assignee" or "AssigneeUserId" or "DueDate" or "DueAtUtc" or "Priority" or "SupersededAtUtc" or "WithdrawnAtUtc");
    }

    // ---- tenant filter and stamping -----------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Risk_rows_are_tenant_filtered_and_cannot_be_written_for_another_organization()
    {
        var a = await Seed(); var b = await Seed();
        await using var scope = factory.Services.CreateAsyncScope(); await using var dbB = TenantDb(scope, b.Org);
        Assert.DoesNotContain(a.Assessment1, await dbB.RiskAssessments.Select(x => x.Id).ToListAsync());
        Assert.DoesNotContain(a.MeasureId, await dbB.RiskMitigationMeasures.Select(x => x.Id).ToListAsync());
        Assert.Empty(await dbB.RiskAssessmentFindings.Where(x => x.OrganizationId == a.Org).ToListAsync());
        dbB.RiskAssessments.Add(NewAssessment(a.Org, a.CaseId, a.Review1, 9));
        await Assert.ThrowsAsync<InvalidOperationException>(() => dbB.SaveChangesAsync());
    }

    // ---- immutability guard -------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task All_three_risk_types_are_fully_immutable_and_never_deleted()
    {
        var w = await Seed();
        async Task Rejected<T>(Func<AppDbContext, DbSet<T>> set, Action<T>? change) where T : TenantEntity
        {
            await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, w.Org);
            var row = await set(db).FirstAsync(x => x.OrganizationId == w.Org);
            if (change == null) set(db).Remove(row); else change(row);
            Assert.Equal(ImmutableMessage, (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync())).Message);
        }
        await Rejected(d => d.RiskAssessments, a => a.Conclusion = a.Conclusion == RiskConclusion.Negligible ? RiskConclusion.NotNegligible : RiskConclusion.Negligible); await Rejected(d => d.RiskAssessments, a => a.Rationale = "Rewritten");
        await Rejected(d => d.RiskAssessments, a => a.ConsideredMitigationThroughNumber = 7); await Rejected<RiskAssessment>(d => d.RiskAssessments, null);
        await Rejected(d => d.RiskAssessmentFindings, f => f.Note = "Rewritten"); await Rejected(d => d.RiskAssessmentFindings, f => f.Finding = RiskCriterionFinding.NoConcern); await Rejected<RiskAssessmentFinding>(d => d.RiskAssessmentFindings, null);
        await Rejected(d => d.RiskMitigationMeasures, m => m.Description = "Rewritten"); await Rejected(d => d.RiskMitigationMeasures, m => m.EvidenceDocumentId = Guid.NewGuid()); await Rejected<RiskMitigationMeasure>(d => d.RiskMitigationMeasures, null);
        await using var verify = factory.Services.CreateAsyncScope(); await using var check = TenantDb(verify, w.Org);
        Assert.Equal("Reasoned.", (await check.RiskAssessments.SingleAsync(x => x.Id == w.Assessment1)).Rationale);
        Assert.Equal("d", (await check.RiskMitigationMeasures.SingleAsync()).Description);
    }

    // ---- database constraints (raw SQL bypasses the guards) -----------------------------------------------------------------------------
    private async Task Violation(Guid org, Func<AppDbContext, Task> insert, string sqlState, string? constraint)
    {
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org);
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => insert(db));
        var postgres = Assert.IsType<PostgresException>(failure.GetBaseException());
        Assert.Equal(sqlState, postgres.SqlState); if (constraint != null) Assert.Equal(constraint, postgres.ConstraintName);
    }
    private static Task InsertAssessment(AppDbContext db, Guid org, Guid caseId, Guid review, int number, string rationale = "Reasoned.", int? considered = null) => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO "RiskAssessments" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","ReviewRecordId","CriteriaCatalogueId","CriteriaCatalogueVersion","Conclusion","Rationale","ConsideredMitigationThroughNumber","AssessedByUserId","AssessedAtUtc")
        VALUES ({Guid.NewGuid()}, now(), now(), {org}, {caseId}, {number}, {review}, 'provena-pilot-risk-criteria', '1', 0, {rationale}, {considered}, {Guid.NewGuid()}, now())
        """);
    private static Task InsertFinding(AppDbContext db, Guid org, Guid caseId, Guid assessment, string code, int finding, string? note) => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO "RiskAssessmentFindings" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","RiskAssessmentId","CriterionCode","Finding","Note")
        VALUES ({Guid.NewGuid()}, now(), now(), {org}, {caseId}, {assessment}, {code}, {finding}, {note})
        """);
    private static Task InsertMeasure(AppDbContext db, Guid org, Guid caseId, Guid assessment, int number, string description = "d", Guid? document = null) => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO "RiskMitigationMeasures" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","RiskAssessmentId","Description","PerformedAtUtc","EvidenceDocumentId","RecordedByUserId")
        VALUES ({Guid.NewGuid()}, now(), now(), {org}, {caseId}, {number}, {assessment}, {description}, now(), {document}, {Guid.NewGuid()})
        """);
    private static Task InsertClosure(AppDbContext db, Guid org, Guid caseId, Guid review, Guid? assessment, int number, int outcome = 0) => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO "CaseClosures" ("Id","CreatedAtUtc","UpdatedAtUtc","OrganizationId","EudrCaseId","Number","Outcome","ReviewRecordId","ExternalDdsReference","RiskAssessmentId","ClosedByUserId","ClosedAtUtc")
        VALUES ({Guid.NewGuid()}, now(), now(), {org}, {caseId}, {number}, {outcome}, {review}, NULL, {assessment}, {Guid.NewGuid()}, now())
        """);

    [Fact]
    public async Task The_database_enforces_unique_numbers_one_finding_per_criterion_and_nonblank_text()
    {
        var w = await Seed();
        await Violation(w.Org, db => InsertAssessment(db, w.Org, w.CaseId, w.Review1, 1), PostgresErrorCodes.UniqueViolation, "IX_RiskAssessments_EudrCaseId_Number");
        await Violation(w.Org, db => InsertAssessment(db, w.Org, w.CaseId, w.Review1, 5, rationale: "   "), PostgresErrorCodes.CheckViolation, "CK_RiskAssessments_NonblankRationale");
        await Violation(w.Org, db => InsertAssessment(db, w.Org, w.CaseId, w.Review1, 6, considered: 0), PostgresErrorCodes.CheckViolation, "CK_RiskAssessments_MitigationNumber");
        await Violation(w.Org, db => InsertFinding(db, w.Org, w.CaseId, w.Assessment1, "EVIDENCE_RELIABILITY", 0, null), PostgresErrorCodes.UniqueViolation, "IX_RiskAssessmentFindings_RiskAssessmentId_CriterionCode");
        await Violation(w.Org, db => InsertFinding(db, w.Org, w.CaseId, w.Assessment1, "OTHER_RISK_INDICATORS", 0, "  "), PostgresErrorCodes.CheckViolation, "CK_RiskAssessmentFindings_NonblankNote");
        await Violation(w.Org, db => InsertFinding(db, w.Org, w.CaseId, w.Assessment1, "OTHER_RISK_INDICATORS", 1, null), PostgresErrorCodes.CheckViolation, "CK_RiskAssessmentFindings_NoteRequired"); // Concern needs a note
        await Violation(w.Org, db => InsertFinding(db, w.Org, w.CaseId, w.Assessment1, "OTHER_RISK_INDICATORS", 2, null), PostgresErrorCodes.CheckViolation, "CK_RiskAssessmentFindings_NoteRequired"); // so does InsufficientInformation
        await Violation(w.Org, db => InsertMeasure(db, w.Org, w.CaseId, w.Assessment1, 1), PostgresErrorCodes.UniqueViolation, "IX_RiskMitigationMeasures_EudrCaseId_Number");
        await Violation(w.Org, db => InsertMeasure(db, w.Org, w.CaseId, w.Assessment1, 2, description: " "), PostgresErrorCodes.CheckViolation, "CK_RiskMitigationMeasures_NonblankDescription");
        await using (var scope = factory.Services.CreateAsyncScope()) { await using var db = TenantDb(scope, w.Org); await InsertFinding(db, w.Org, w.CaseId, w.Assessment1, "OTHER_RISK_INDICATORS", 0, null); } // NoConcern needs no note
    }

    [Fact]
    public async Task The_database_proves_the_review_the_assessment_the_evidence_and_the_measure_belong_to_the_rows_own_case_and_forbids_deletes()
    {
        var w = await Seed();
        await Violation(w.Org, db => InsertAssessment(db, w.Org, w.CaseId, w.OtherReview, 7), PostgresErrorCodes.ForeignKeyViolation, AssessmentReviewFk); // a review of another case
        await Violation(w.Org, db => InsertFinding(db, w.Org, w.OtherCaseId, w.Assessment1, "OTHER_RISK_INDICATORS", 0, null), PostgresErrorCodes.ForeignKeyViolation, null); // assessment of another case
        await Violation(w.Org, db => InsertMeasure(db, w.Org, w.OtherCaseId, w.Assessment1, 1), PostgresErrorCodes.ForeignKeyViolation, null);
        await Violation(w.Org, db => InsertMeasure(db, w.Org, w.CaseId, w.Assessment1, 2, document: w.OtherCaseDocumentId), PostgresErrorCodes.ForeignKeyViolation, null); // evidence of another case
        await using (var scope = factory.Services.CreateAsyncScope()) { await using var db = TenantDb(scope, w.Org); await InsertMeasure(db, w.Org, w.CaseId, w.Assessment1, 2, document: w.DocumentId); }
        // Restrict: neither an assessment, nor its review, nor an evidence document can be deleted while referenced.
        await Violation(w.Org, db => db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "RiskAssessments" WHERE "Id" = {w.Assessment1}"""), PostgresErrorCodes.ForeignKeyViolation, null);
        await Violation(w.Org, db => db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "ReviewRecords" WHERE "Id" = {w.Review1}"""), PostgresErrorCodes.ForeignKeyViolation, null);
        await Violation(w.Org, db => db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "Documents" WHERE "Id" = {w.DocumentId}"""), PostgresErrorCodes.ForeignKeyViolation, null);
    }

    // ---- the closure -> assessment -> review consistency --------------------------------------------------------------------------------
    [Fact]
    public async Task The_database_rejects_a_closure_on_review_R2_that_references_an_assessment_made_on_review_R1()
    {
        var w = await Seed(); // Assessment1 was made on Review1, Assessment2 on Review2
        await Violation(w.Org, db => InsertClosure(db, w.Org, w.CaseId, w.Review2, w.Assessment1, 1), PostgresErrorCodes.ForeignKeyViolation, ClosureAssessmentFk);
        await Violation(w.Org, db => InsertClosure(db, w.Org, w.CaseId, w.Review1, w.Assessment2, 1), PostgresErrorCodes.ForeignKeyViolation, ClosureAssessmentFk);
        // An assessment of another case, even on a review id that matches that case, cannot back this case's closure.
        await Violation(w.Org, db => InsertClosure(db, w.Org, w.CaseId, w.OtherReview, w.OtherAssessment, 1), PostgresErrorCodes.ForeignKeyViolation, null);
        await Violation(w.Org, db => InsertClosure(db, w.Org, w.CaseId, w.Review1, w.OtherAssessment, 1), PostgresErrorCodes.ForeignKeyViolation, ClosureAssessmentFk);
        await using var scope = factory.Services.CreateAsyncScope(); await using var check = TenantDb(scope, w.Org);
        Assert.Equal(0, await check.CaseClosures.CountAsync());
    }

    [Fact]
    public async Task A_consistent_closure_is_accepted_and_a_null_assessment_is_allowed_for_legacy_and_other_outcomes()
    {
        var w = await Seed();
        await using (var scope = factory.Services.CreateAsyncScope()) { await using var db = TenantDb(scope, w.Org); await InsertClosure(db, w.Org, w.CaseId, w.Review2, w.Assessment2, 1); } // R2 + the assessment made on R2
        await using (var scope = factory.Services.CreateAsyncScope()) { await using var db = TenantDb(scope, w.Org); await InsertClosure(db, w.Org, w.CaseId, w.Review1, null, 2, outcome: 1); } // OutsideRegulatoryScope, no assessment
        await using (var scope = factory.Services.CreateAsyncScope()) { await using var db = TenantDb(scope, w.Org); await InsertClosure(db, w.Org, w.OtherCaseId, w.OtherReview, null, 1); } // legacy-shaped ExternalDdsCompleted, no assessment
        await using var verify = factory.Services.CreateAsyncScope(); await using var check = TenantDb(verify, w.Org);
        Assert.Equal(w.Assessment2, (await check.CaseClosures.SingleAsync(x => x.Number == 1 && x.EudrCaseId == w.CaseId)).RiskAssessmentId);
        Assert.Equal(2, await check.CaseClosures.CountAsync(x => x.RiskAssessmentId == null));
    }

    [Fact]
    public async Task Only_an_external_dds_completed_closure_may_carry_an_assessment_and_an_assessment_backs_at_most_one_closure()
    {
        var w = await Seed();
        await Violation(w.Org, db => InsertClosure(db, w.Org, w.CaseId, w.Review2, w.Assessment2, 1, outcome: 1), PostgresErrorCodes.CheckViolation, "CK_CaseClosures_RiskAssessmentOutcome");
        await Violation(w.Org, db => InsertClosure(db, w.Org, w.CaseId, w.Review2, w.Assessment2, 1, outcome: 2), PostgresErrorCodes.CheckViolation, "CK_CaseClosures_RiskAssessmentOutcome");
        await using (var scope = factory.Services.CreateAsyncScope()) { await using var db = TenantDb(scope, w.Org); await InsertClosure(db, w.Org, w.CaseId, w.Review2, w.Assessment2, 1); }
        // The same assessment (hence the same review) again is refused; the unique review index and the filtered unique assessment index both stand behind it.
        await Violation(w.Org, db => InsertClosure(db, w.Org, w.CaseId, w.Review2, w.Assessment2, 2), PostgresErrorCodes.UniqueViolation, null);
        // Restrict: a closure that references the assessment keeps it (and the assessment keeps its review).
        await Violation(w.Org, db => db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "RiskAssessments" WHERE "Id" = {w.Assessment2}"""), PostgresErrorCodes.ForeignKeyViolation, ClosureAssessmentFk);
    }
}
