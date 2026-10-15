using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Provena.Api.Auth;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
using Xunit;
using static Provena.Api.IntegrationTests.CompletenessReadModelTests;
namespace Provena.Api.IntegrationTests;

// Quantity Reconciliation v1 (completeness policy v2) over real HTTP and PostgreSQL: the structured result on GET completeness and the working export, the supplier flow that
// records a mismatching quantity instead of rejecting it, the positive Owner review gate, and the ExternalDdsCompleted close-time recheck (including a Current review made under policy v1).
// Each scenario provisions its own organization through AuthService; the registering user is the organization's Owner.
[Collection("Api")]
public sealed class QuantityReconciliationTests(ApiFactory factory)
{
    private const string Password = "Test-Only-Password-928!";
    private sealed record Owner(HttpClient Client, Guid Org, Guid UserId);

    private async Task<Owner> NewOwner()
    {
        var name = "Qty-" + Guid.NewGuid().ToString("N")[..8];
        await using var scope = factory.Services.CreateAsyncScope();
        var token = await scope.ServiceProvider.GetRequiredService<AuthService>().Register(new RegisterRequest($"{name}@example.test", Password, "Test", "User", "Org " + name, null), CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Organizations.Where(x => x.Name == "Org " + name).Select(x => x.Id).SingleAsync();
        var user = await db.OrganizationUsers.Where(x => x.OrganizationId == org).Select(x => x.UserId).SingleAsync();
        var client = factory.CreateClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return new(client, org, user);
    }
    private static AppDbContext TenantDb(IServiceScope scope, Guid org)
    {
        var tenant = new TenantContext(new HttpContextAccessor()); tenant.SetOrganization(org);
        return new AppDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>(), tenant);
    }
    private async Task<T> Query<T>(Guid org, Func<AppDbContext, Task<T>> query) { await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org); return await query(db); }
    private Task<int> Closures(Guid org, Guid caseId) => Query(org, db => db.CaseClosures.CountAsync(x => x.EudrCaseId == caseId));
    private Task<int> Revisions(Guid org, Guid caseId) => Query(org, db => db.CaseRevisions.CountAsync(x => x.EudrCaseId == caseId));
    private Task<int> Audits(Guid org, Guid caseId, string type) => Query(org, db => db.AuditEvents.CountAsync(x => x.EudrCaseId == caseId && x.EventType == type));
    private Task<int> RevokedRequests(Guid org, Guid caseId) => Query(org, db => db.SupplierRequests.CountAsync(x => x.EudrCaseId == caseId && x.RevokedAtUtc != null));

    // Saves the case's one lot with a single producer contribution of the given quantity; the shipment stays 10 Kg, so anything but 10 Kg is a Shipment <-> CaseLot mismatch.
    private static Task<HttpResponseMessage> SaveLot(World w, decimal lot, decimal contribution, string unit = "Kg") => w.C.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/dossier/lot", new
    {
        lotReference = "LOT-" + Guid.NewGuid().ToString("N")[..10], quantity = lot, unit, note = (string?)null,
        contributions = new[] { new { producerId = w.ProducerId, quantity = contribution, unit, plots = new[] { new { productionPlotId = w.PlotId, plotRevisionId = w.RevisionId, productionStartDate = "2024-01-01", productionEndDate = "2024-12-31" } } } },
    });
    private static Task<HttpResponseMessage> PostReview(HttpClient c, Guid caseId, string decision, JsonElement state) => c.PostAsJsonAsync($"/api/eudr-cases/{caseId}/reviews", new
    {
        decision, rationale = "Reviewed against the recorded facts.", expectedFingerprint = Fingerprint(state),
        expectedLatestReviewId = state.GetProperty("latestReview").ValueKind == JsonValueKind.Null ? (Guid?)null : state.GetProperty("latestReview").GetProperty("reviewId").GetGuid(),
    });
    private static async Task<HttpResponseMessage> ReviewNow(HttpClient c, Guid caseId, string decision) => await PostReview(c, caseId, decision, await Completeness(c, caseId));
    private static Task<HttpResponseMessage> Close(HttpClient c, Guid caseId, Guid reviewId, string? reference = null) => c.PostAsJsonAsync($"/api/eudr-cases/{caseId}/close", new { expectedReviewId = reviewId, externalDdsReference = reference });
    private static async Task<JsonElement> Problem(HttpResponseMessage response, HttpStatusCode status)
    { var json = await Json(response, status); Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType); return json; }
    private static JsonElement Reconciliation(JsonElement completeness) => completeness.GetProperty("quantityReconciliation");
    private static JsonElement CheckOf(JsonElement completeness, string code) => Reconciliation(completeness).GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("code").GetString() == code);
    private static string Outcome(JsonElement completeness) => Reconciliation(completeness).GetProperty("outcome").GetString()!;
    private static async Task<string> CaseStatus(HttpClient c, Guid caseId) => (await Json(await c.GetAsync($"/api/eudr-cases/{caseId}"))).GetProperty("status").GetString()!;

    // Simulates an Owner review made BEFORE Quantity Reconciliation existed (policy v1, which never compared the shipment or the confirmed quantity). The HTTP review route can no longer
    // produce one for a mismatching dossier, so it is inserted directly with the current fingerprint to make it Current.
    private async Task<Guid> InsertPolicyV1AcceptedReview(Owner owner, Guid caseId, string currentFingerprint)
    {
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, owner.Org);
        var number = (await db.CaseRevisions.Where(x => x.EudrCaseId == caseId).MaxAsync(x => (int?)x.Number) ?? 0) + 1;
        var revision = new CaseRevision
        {
            OrganizationId = owner.Org, EudrCaseId = caseId, Number = number, PolicyId = "provena-pilot-completeness", PolicyVersion = "1", SnapshotSchemaVersion = 1,
            FingerprintVersion = 1, Fingerprint = currentFingerprint, SectionHashes = "{}", SnapshotJson = "{}",
        };
        db.CaseRevisions.Add(revision); await db.SaveChangesAsync();
        var review = new ReviewRecord { OrganizationId = owner.Org, EudrCaseId = caseId, CaseRevisionId = revision.Id, Decision = ReviewDecision.AcceptedAsCompleteUnderPolicy, Rationale = "Accepted under policy v1.", ReviewerUserId = owner.UserId, ReviewedAtUtc = DateTimeOffset.UtcNow, PolicyVersion = "1" };
        db.ReviewRecords.Add(review); await db.SaveChangesAsync();
        return review.Id;
    }

    // ---- the structured result ---------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Completeness_and_the_working_export_expose_the_structured_reconciliation_with_field_level_details()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org);
        var matched = await Completeness(o.Client, w.CaseId);
        Assert.Equal("Matched", Outcome(matched)); AssertNoOrganizationId(Reconciliation(matched));
        Assert.Equal(["SHIPMENT_LOT", "SUPPLIER_CONFIRMED_SHIPMENT", "CONTRIBUTIONS_LOT"], Reconciliation(matched).GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("code").GetString()));
        var shipmentLot = CheckOf(matched, "SHIPMENT_LOT");
        Assert.Equal(["code", "status", "expected", "actual", "difference", "missingFields", "reason"], shipmentLot.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["source", "quantity", "unit", "unitImplied"], shipmentLot.GetProperty("expected").EnumerateObject().Select(p => p.Name));
        Assert.Equal("Shipment", shipmentLot.GetProperty("expected").GetProperty("source").GetString()); Assert.Equal(10m, shipmentLot.GetProperty("expected").GetProperty("quantity").GetDecimal()); Assert.Equal("Kg", shipmentLot.GetProperty("expected").GetProperty("unit").GetString());
        Assert.Equal("CaseLot", shipmentLot.GetProperty("actual").GetProperty("source").GetString()); Assert.Equal("Matched", shipmentLot.GetProperty("status").GetString());
        // The default Build marks the quantity confirmation NotRequired and the supplier submitted nothing, so that check does not apply.
        var confirmed = CheckOf(matched, "SUPPLIER_CONFIRMED_SHIPMENT");
        Assert.Equal("NotApplicable", confirmed.GetProperty("status").GetString()); Assert.True(confirmed.GetProperty("actual").GetProperty("unitImplied").GetBoolean());
        foreach (var code in new[] { "LOT_PRESENT", "SHIPMENT_LOT_QUANTITY_MATCH", "LOT_RECONCILED" }) Assert.Equal("Satisfied", StatusOf(matched, code));
        Assert.Equal("NotApplicable", StatusOf(matched, "SUPPLIER_CONFIRMED_QUANTITY_MATCH"));
        Assert.Equal(["LOT_PRESENT", "SHIPMENT_LOT_QUANTITY_MATCH", "SUPPLIER_CONFIRMED_QUANTITY_MATCH", "LOT_RECONCILED"], matched.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("group").GetString() == "Quantity").Select(i => i.GetProperty("code").GetString()));

        // A lot that differs from the shipment: exact, no tolerance, and the lot still reconciles with its own contributions.
        await Ok(SaveLot(w, 25m, 25m));
        var quantity = await Completeness(o.Client, w.CaseId);
        Assert.Equal("QuantityMismatch", Outcome(quantity)); Assert.Equal("Incomplete", quantity.GetProperty("dataCoverage").GetString());
        var mismatch = CheckOf(quantity, "SHIPMENT_LOT");
        Assert.Equal("QuantityMismatch", mismatch.GetProperty("status").GetString()); Assert.Equal(15m, mismatch.GetProperty("difference").GetDecimal()); Assert.Equal(25m, mismatch.GetProperty("actual").GetProperty("quantity").GetDecimal());
        Assert.Equal("Invalid", StatusOf(quantity, "SHIPMENT_LOT_QUANTITY_MATCH")); Assert.Equal("Satisfied", StatusOf(quantity, "LOT_RECONCILED"));

        // Different units are never converted: 10 Ton is not 10000 Kg and not 10 Kg.
        await Ok(SaveLot(w, 10m, 10m, "Ton"));
        var unit = await Completeness(o.Client, w.CaseId);
        Assert.Equal("UnitMismatch", Outcome(unit));
        var unitCheck = CheckOf(unit, "SHIPMENT_LOT");
        Assert.Equal("UnitMismatch", unitCheck.GetProperty("status").GetString()); Assert.Equal(JsonValueKind.Null, unitCheck.GetProperty("difference").ValueKind);
        Assert.Equal("Kg", unitCheck.GetProperty("expected").GetProperty("unit").GetString()); Assert.Equal("Ton", unitCheck.GetProperty("actual").GetProperty("unit").GetString());
        Assert.Equal("Invalid", StatusOf(unit, "SHIPMENT_LOT_QUANTITY_MATCH"));

        // The working export carries the same structured result under completeness.
        var response = await o.Client.GetAsync($"/api/eudr-cases/{w.CaseId}/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        using var manifest = JsonDocument.Parse(await new StreamReader(zip.Entries.Single(e => e.FullName.EndsWith("/manifest.json")).Open()).ReadToEndAsync());
        var exported = manifest.RootElement.GetProperty("completeness").GetProperty("quantityReconciliation");
        Assert.Equal("UnitMismatch", exported.GetProperty("outcome").GetString()); Assert.Equal(3, exported.GetProperty("checks").GetArrayLength());
        Assert.Equal("UnitMismatch", exported.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "SHIPMENT_LOT").GetProperty("status").GetString());
    }

    // ---- the supplier flow --------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_supplier_confirmed_quantity_that_differs_is_accepted_and_judged_by_the_reconciliation_and_more_than_six_decimals_is_a_400()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org, new Opt(Documents: false)); // the quantity confirmation stays required and Missing
        var required = await Completeness(o.Client, w.CaseId);
        Assert.Equal("MissingData", Outcome(required)); Assert.Equal("Missing", StatusOf(required, "SUPPLIER_CONFIRMED_QUANTITY_MATCH"));
        Assert.Equal(["supplierConfirmedQuantity"], CheckOf(required, "SUPPLIER_CONFIRMED_SHIPMENT").GetProperty("missingFields").EnumerateArray().Select(x => x.GetString()));

        var link = await Json(await o.Client.PostAsync($"/api/eudr-cases/{w.CaseId}/supplier-request", null)); var token = link.GetProperty("publicUrl").GetString()!.Split('/').Last();
        var path = $"/public/supplier-requests/{token}/details"; using var publicClient = factory.CreateClient();
        Task<HttpResponseMessage> Submit(decimal quantity) => publicClient.PutAsJsonAsync(path, new { countryOfProductionCode = "BR", productionPeriodFrom = "2026-01-01", productionPeriodTo = "2026-06-30", confirmedQuantity = quantity });

        // A quantity that differs from the shipment is recorded, not rejected: submission is not acceptance and reconciliation is derived afterwards.
        Assert.Equal(HttpStatusCode.NoContent, (await Submit(9m)).StatusCode);
        var differs = await Completeness(o.Client, w.CaseId);
        var check = CheckOf(differs, "SUPPLIER_CONFIRMED_SHIPMENT");
        Assert.Equal("QuantityMismatch", check.GetProperty("status").GetString()); Assert.Equal(-1m, check.GetProperty("difference").GetDecimal());
        Assert.Equal(9m, check.GetProperty("actual").GetProperty("quantity").GetDecimal()); Assert.Equal("Kg", check.GetProperty("actual").GetProperty("unit").GetString()); Assert.True(check.GetProperty("actual").GetProperty("unitImplied").GetBoolean());
        Assert.Equal("QuantityMismatch", Outcome(differs)); Assert.Equal("Invalid", StatusOf(differs, "SUPPLIER_CONFIRMED_QUANTITY_MATCH"));

        // More than six decimals cannot be stored exactly, so it is refused instead of rounded; the previous submission stays current.
        var tooPrecise = await Problem(await Submit(1.1234567m), HttpStatusCode.BadRequest);
        Assert.True(tooPrecise.GetProperty("errors").TryGetProperty("ConfirmedQuantity", out _));
        Assert.Equal("QuantityMismatch", Outcome(await Completeness(o.Client, w.CaseId)));

        // An equal value at a different scale is the same quantity: no tolerance is needed.
        Assert.Equal(HttpStatusCode.NoContent, (await Submit(10.000000m)).StatusCode);
        var equal = await Completeness(o.Client, w.CaseId);
        Assert.Equal("Matched", CheckOf(equal, "SUPPLIER_CONFIRMED_SHIPMENT").GetProperty("status").GetString()); Assert.Equal("Satisfied", StatusOf(equal, "SUPPLIER_CONFIRMED_QUANTITY_MATCH"));
    }

    // ---- the positive Owner review gate ----------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_non_matched_reconciliation_blocks_acceptance_until_the_quantities_agree_and_leaves_the_other_decisions_alone()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org);
        await Ok(SaveLot(w, 25m, 25m));
        var state = await Completeness(o.Client, w.CaseId);
        var blocked = await Problem(await PostReview(o.Client, w.CaseId, "AcceptedAsCompleteUnderPolicy", state), HttpStatusCode.Conflict);
        var failing = blocked.GetProperty("errors").GetProperty("failingItems").EnumerateArray().Select(i => i.GetProperty("code").GetString()).ToArray();
        Assert.Contains("SHIPMENT_LOT_QUANTITY_MATCH", failing); Assert.DoesNotContain("LOT_RECONCILED", failing);
        Assert.Equal(0, await Revisions(o.Org, w.CaseId));

        await Ok(SaveLot(w, 10m, 10m, "Ton")); // a unit mismatch blocks too
        Assert.Contains("SHIPMENT_LOT_QUANTITY_MATCH", (await Problem(await ReviewNow(o.Client, w.CaseId, "AcceptedAsCompleteUnderPolicy"), HttpStatusCode.Conflict)).GetProperty("errors").GetProperty("failingItems").EnumerateArray().Select(i => i.GetProperty("code").GetString()));
        // Returning the dossier for correction is always allowed.
        Assert.Equal(HttpStatusCode.Created, (await ReviewNow(o.Client, w.CaseId, "ReturnedForCorrection")).StatusCode);

        await Ok(SaveLot(w, 10m, 10m)); // corrected
        Assert.Equal("Matched", Outcome(await Completeness(o.Client, w.CaseId)));
        Assert.Equal(HttpStatusCode.Created, (await ReviewNow(o.Client, w.CaseId, "AcceptedAsCompleteUnderPolicy")).StatusCode);
    }

    [Theory]
    [InlineData("ConfirmedOutsideRegulatoryScope", "OutsideRegulatoryScope")]
    [InlineData("ConfirmedUnsupportedFlow", "UnsupportedFlow")]
    public async Task Confirmed_outside_scope_and_unsupported_flow_review_and_close_with_mismatching_quantities(string decision, string outcome)
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org);
        await Ok(o.Client.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/applicability", decision == "ConfirmedOutsideRegulatoryScope" ? Application("OutsideRegulatoryScope", "Pending") : Application(activity: "Export")));
        await Ok(SaveLot(w, 25m, 25m)); // Shipment <-> CaseLot mismatch
        Assert.Equal("QuantityMismatch", Outcome(await Completeness(o.Client, w.CaseId)));
        var review = await Json(await ReviewNow(o.Client, w.CaseId, decision), HttpStatusCode.Created);
        var closure = await Json(await Close(o.Client, w.CaseId, review.GetProperty("reviewId").GetGuid()), HttpStatusCode.Created);
        Assert.Equal(outcome, closure.GetProperty("outcome").GetString()); Assert.Equal(1, await Closures(o.Org, w.CaseId)); Assert.Equal("Closed", await CaseStatus(o.Client, w.CaseId));
    }

    // ---- the close-time recheck ----------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Closing_as_external_dds_completed_is_refused_for_a_current_v1_review_when_the_current_reconciliation_is_not_matched_and_writes_nothing()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org); await Ok(SaveLot(w, 25m, 25m)); // quantities that policy v1 never compared
        var link = await Json(await o.Client.PostAsync($"/api/eudr-cases/{w.CaseId}/supplier-request", null)); var token = link.GetProperty("publicUrl").GetString()!.Split('/').Last();
        var statusBefore = await CaseStatus(o.Client, w.CaseId);
        var state = await Completeness(o.Client, w.CaseId);
        var reviewId = await InsertPolicyV1AcceptedReview(o, w.CaseId, Fingerprint(state));
        var current = await Completeness(o.Client, w.CaseId);
        Assert.Equal("Current", current.GetProperty("reviewFreshness").GetString()); Assert.True(current.GetProperty("latestReview").GetProperty("policyChanged").GetBoolean()); Assert.Equal("1", current.GetProperty("latestReview").GetProperty("policyVersion").GetString());

        // No External DDS record exists yet: the quantity gate still comes before the DDS gate.
        var response = await Close(o.Client, w.CaseId, reviewId, "DDS-ABC-1");
        var problem = await Problem(response, HttpStatusCode.Conflict);
        Assert.Equal(409, problem.GetProperty("status").GetInt32()); Assert.True(problem.TryGetProperty("traceId", out _)); Assert.Contains("ExternalDdsCompleted", problem.GetProperty("detail").GetString());
        var errors = problem.GetProperty("errors");
        Assert.Equal("QuantityNotReconciled", errors.GetProperty("reason").GetString());
        var details = errors.GetProperty("quantityReconciliation");
        Assert.Equal("QuantityMismatch", details.GetProperty("outcome").GetString()); Assert.Equal(3, details.GetProperty("checks").GetArrayLength());
        Assert.Equal("QuantityMismatch", details.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "SHIPMENT_LOT").GetProperty("status").GetString());
        AssertNoOrganizationId(problem);

        // The failed close wrote nothing: no closure, no audit event, no status change, and the supplier link was not revoked.
        Assert.Equal(0, await Closures(o.Org, w.CaseId)); Assert.Equal(0, await Audits(o.Org, w.CaseId, "CaseClosed"));
        Assert.Equal(statusBefore, await CaseStatus(o.Client, w.CaseId)); Assert.Equal(0, await RevokedRequests(o.Org, w.CaseId));
        using var publicClient = factory.CreateClient(); Assert.Equal(HttpStatusCode.OK, (await publicClient.GetAsync($"/public/supplier-requests/{token}")).StatusCode);

        // Recording the external fact does not need Matched quantities (it happened outside Provena); closing as ExternalDdsCompleted still does.
        await ExternalDdsTestSupport.RecordOk(o.Client, w.CaseId, reviewId, "DDS-ABC-1");
        Assert.Equal("QuantityNotReconciled", (await Problem(await Close(o.Client, w.CaseId, reviewId, "DDS-ABC-1"), HttpStatusCode.Conflict)).GetProperty("errors").GetProperty("reason").GetString());
        Assert.Equal(0, await Closures(o.Org, w.CaseId));

        // Correcting the quantities changes the facts, so the old review is Stale and a new review of the corrected dossier is needed.
        await Ok(SaveLot(w, 10m, 10m));
        var stale = await Problem(await Close(o.Client, w.CaseId, reviewId, "DDS-ABC-1"), HttpStatusCode.Conflict);
        Assert.Equal("StaleReview", stale.GetProperty("errors").GetProperty("reason").GetString());
        var fresh = await Json(await ReviewNow(o.Client, w.CaseId, "AcceptedAsCompleteUnderPolicy"), HttpStatusCode.Created);
        await ExternalDdsTestSupport.RecordOk(o.Client, w.CaseId, fresh.GetProperty("reviewId").GetGuid(), "DDS-ABC-1");
        var closed = await Close(o.Client, w.CaseId, fresh.GetProperty("reviewId").GetGuid(), "DDS-ABC-1");
        Assert.Equal(HttpStatusCode.Created, closed.StatusCode); Assert.Equal("ExternalDdsCompleted", (await Json(closed)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_not_required_quantity_confirmation_without_a_basis_is_missing_data_and_a_current_v1_review_cannot_close_on_it()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org); // Build waives the confirmation with a recorded note
        Assert.Equal("Matched", Outcome(await Completeness(o.Client, w.CaseId)));
        var requirement = (await Json(await o.Client.GetAsync($"/api/eudr-cases/{w.CaseId}"))).GetProperty("requirements").EnumerateArray().Single(r => r.GetProperty("type").GetString() == "QuantityConfirmation").GetProperty("id").GetGuid();
        // The reviewer API allows NotRequired without a note; the completeness requirement item then stays Missing, and so must the reconciliation.
        Assert.Equal(HttpStatusCode.NoContent, (await Patch(o.Client, $"/api/eudr-cases/{w.CaseId}/requirements/{requirement}", new { status = "NotRequired" })).StatusCode);
        var state = await Completeness(o.Client, w.CaseId);
        Assert.Equal("Missing", StatusOf(state, "REQUIREMENT_QuantityConfirmation")); Assert.Equal("Missing", StatusOf(state, "SUPPLIER_CONFIRMED_QUANTITY_MATCH"));
        Assert.Equal("MissingData", Outcome(state));
        Assert.Equal(["quantityConfirmationWaiverBasis"], CheckOf(state, "SUPPLIER_CONFIRMED_SHIPMENT").GetProperty("missingFields").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("REQUIREMENT_QuantityConfirmation", (await Problem(await PostReview(o.Client, w.CaseId, "AcceptedAsCompleteUnderPolicy", state), HttpStatusCode.Conflict)).GetProperty("errors").GetProperty("failingItems").EnumerateArray().Select(i => i.GetProperty("code").GetString()));

        var reviewId = await InsertPolicyV1AcceptedReview(o, w.CaseId, Fingerprint(state));
        Assert.Equal("Current", (await Completeness(o.Client, w.CaseId)).GetProperty("reviewFreshness").GetString());
        var problem = await Problem(await Close(o.Client, w.CaseId, reviewId, "DDS-ABC-3"), HttpStatusCode.Conflict);
        Assert.Equal("QuantityNotReconciled", problem.GetProperty("errors").GetProperty("reason").GetString()); Assert.Equal("MissingData", problem.GetProperty("errors").GetProperty("quantityReconciliation").GetProperty("outcome").GetString());
        Assert.Equal(0, await Closures(o.Org, w.CaseId)); Assert.Equal(0, await Audits(o.Org, w.CaseId, "CaseClosed"));

        // Recording a basis restores both the requirement item and the reconciliation (and, being a requirement fact, stales the old review).
        Assert.Equal(HttpStatusCode.NoContent, (await Patch(o.Client, $"/api/eudr-cases/{w.CaseId}/requirements/{requirement}", new { status = "NotRequired", reviewerNote = "Not needed in this pilot case" })).StatusCode);
        var restored = await Completeness(o.Client, w.CaseId);
        Assert.Equal("NotApplicable", StatusOf(restored, "REQUIREMENT_QuantityConfirmation")); Assert.Equal("NotApplicable", StatusOf(restored, "SUPPLIER_CONFIRMED_QUANTITY_MATCH")); Assert.Equal("Matched", Outcome(restored));
    }

    [Fact]
    public async Task A_current_v1_accepted_review_still_closes_as_external_dds_completed_with_201_when_the_reconciliation_matches()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org);
        var state = await Completeness(o.Client, w.CaseId); Assert.Equal("Matched", Outcome(state));
        var reviewId = await InsertPolicyV1AcceptedReview(o, w.CaseId, Fingerprint(state));
        await ExternalDdsTestSupport.RecordOk(o.Client, w.CaseId, reviewId, "DDS-ABC-2");
        var response = await Close(o.Client, w.CaseId, reviewId, "DDS-ABC-2");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); Assert.EndsWith($"/api/eudr-cases/{w.CaseId}/lifecycle", response.Headers.Location!.AbsolutePath);
        var closure = await Json(response);
        Assert.Equal("ExternalDdsCompleted", closure.GetProperty("outcome").GetString()); Assert.Equal("Closed", await CaseStatus(o.Client, w.CaseId)); Assert.Equal(1, await Audits(o.Org, w.CaseId, "CaseClosed"));
    }
}
