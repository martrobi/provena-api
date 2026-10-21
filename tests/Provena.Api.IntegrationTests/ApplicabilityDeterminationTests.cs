using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Provena.Api.Auth;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
using Xunit;
using static Provena.Api.IntegrationTests.CompletenessReadModelTests;
namespace Provena.Api.IntegrationTests;

// Applicability Determination v1 hardening over real HTTP and PostgreSQL: the PUT is Owner-only (live check before the case lookup and the body), carries an optimistic-concurrency token,
// reports typed conflicts and supports the explicit RequiresLegalReview scope. CaseApplicability stays the only applicability model; nothing here changes the fingerprint or the lifecycle.
[Collection("Api")]
public sealed class ApplicabilityDeterminationTests(ApiFactory factory)
{
    private const string Password = "Test-Only-Password-928!";
    private static readonly Opt Light = new(Recipients: false, Lot: false, Documents: false);
    private sealed record Owner(HttpClient Client, Guid Org, Guid UserId);

    // ---- fixtures -------------------------------------------------------------------------------------------------------------------------
    private async Task<Owner> NewOwner()
    {
        var name = "Appl-" + Guid.NewGuid().ToString("N")[..8];
        await using var scope = factory.Services.CreateAsyncScope();
        var token = await scope.ServiceProvider.GetRequiredService<AuthService>().Register(new RegisterRequest($"{name}@example.test", Password, "Test", "User", "Org " + name, null), CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Organizations.Where(x => x.Name == "Org " + name).Select(x => x.Id).SingleAsync();
        var user = await db.OrganizationUsers.Where(x => x.OrganizationId == org).Select(x => x.UserId).SingleAsync();
        return new(Client(token.AccessToken), org, user);
    }
    private HttpClient Client(string token) { var c = factory.CreateClient(); c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); return c; }
    private async Task<(HttpClient Client, Guid UserId)> NewMember(Guid org)
    {
        var email = $"member-{Guid.NewGuid():N}@example.test";
        await using var scope = factory.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        await auth.Register(new RegisterRequest(email, Password, "Mem", "Ber", "Member home " + Guid.NewGuid().ToString("N")[..8], null), CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await db.Users.Where(x => x.Email == email).Select(x => x.Id).SingleAsync();
        db.OrganizationUsers.RemoveRange(await db.OrganizationUsers.Where(x => x.UserId == userId).ToListAsync());
        db.OrganizationUsers.Add(new OrganizationUser { OrganizationId = org, UserId = userId, Role = OrganizationRole.Member });
        await db.SaveChangesAsync();
        return (Client((await auth.Login(new LoginRequest(email, Password), CancellationToken.None)).AccessToken), userId);
    }
    private static AppDbContext TenantDb(IServiceScope scope, Guid org)
    {
        var tenant = new TenantContext(new HttpContextAccessor()); tenant.SetOrganization(org);
        return new AppDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>(), tenant);
    }
    private async Task<T> Query<T>(Guid org, Func<AppDbContext, Task<T>> query) { await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org); return await query(db); }
    private Task<int> Versions(Guid org, Guid caseId) => Query(org, db => db.CaseApplicabilities.CountAsync(x => x.EudrCaseId == caseId));
    private Task<List<AuditEvent>> Saved(Guid org, Guid caseId) => Query(org, db => db.AuditEvents.AsNoTracking().Where(x => x.EudrCaseId == caseId && x.EventType == "CaseApplicabilitySaved").ToListAsync());

    private static object Body(string scope = "RequiresLegalReview", string flow = "Pending", string? reasoning = "Counsel must confirm the CN heading.", string? description = null, string? citation = null,
        string? basis = null, string? qualifications = null, string? activityDate = null, string? effective = null) => new
    {
        productDescription = description, singleRelevantCommodity = true, activityType = "PlacingOnMarketByImport", operatorRole = "Operator", activityDate, activityDateKind = "Planned",
        scopeCitation = citation, qualifications, legalBasisVersion = basis, effectiveDate = effective, reasoning, regulatoryScope = scope, flowSupport = flow,
    };
    private static string Url(Guid caseId, string tail = "applicability") => $"/api/eudr-cases/{caseId}/{tail}";
    private static async Task<JsonElement> Problem(HttpResponseMessage response, HttpStatusCode status)
    { var json = await Json(response, status); Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType); return json; }
    private static string Reason(JsonElement problem) => problem.GetProperty("errors").GetProperty("reason").GetString()!;
    private static async Task<JsonElement> Current(HttpClient c, Guid caseId) => await Json(await c.GetAsync(Url(caseId)), HttpStatusCode.OK);
    private static async Task<string> StatusOf(HttpClient c, Guid caseId) => (await Current(c, caseId)).GetProperty("status").GetString()!;

    private static async Task<HttpResponseMessage> TryReview(HttpClient c, Guid caseId, string decision)
    {
        var state = await Completeness(c, caseId);
        var latest = state.GetProperty("latestReview").ValueKind == JsonValueKind.Null ? (Guid?)null : state.GetProperty("latestReview").GetProperty("reviewId").GetGuid();
        return await c.PostAsJsonAsync(Url(caseId, "reviews"), new { decision, rationale = "Reviewed against the recorded facts.", expectedFingerprint = Fingerprint(state), expectedLatestReviewId = latest });
    }
    private static async Task<Guid> Review(HttpClient c, Guid caseId, string decision) => (await Json(await TryReview(c, caseId, decision), HttpStatusCode.Created)).GetProperty("reviewId").GetGuid();
    private static Task<HttpResponseMessage> Close(HttpClient c, Guid caseId, Guid reviewId) => c.PostAsJsonAsync(Url(caseId, "close"), new { expectedReviewId = reviewId });
    private static Task<HttpResponseMessage> PutBody(HttpClient c, Guid caseId, object body, Guid? expected) => ApplicabilitySupport.PutExpecting(c, caseId, body, expected);

    // ---- authorization: Owner before case and body ------------------------------------------------------------------------------------------
    [Fact]
    public async Task Only_an_owner_may_put_the_applicability_and_a_member_learns_nothing_about_the_case_or_the_body_but_can_read_it()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org, Light); var other = await NewOwner(); var foreign = await Build(other.Client, other.Org, Light);
        var (member, _) = await NewMember(o.Org); using var anonymous = factory.CreateClient();
        var before = await Versions(o.Org, w.CaseId); var auditBefore = (await Saved(o.Org, w.CaseId)).Count; var expected = await ApplicabilitySupport.CurrentId(o.Client, w.CaseId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(Url(w.CaseId), ApplicabilitySupport.With(Body(), expected))).StatusCode);
        // A genuine Member: 403 for an existing case, a nonexistent case, another organization's case and an invalid or malformed body - never 404, 400 or 415.
        foreach (var caseId in new[] { w.CaseId, Guid.NewGuid(), foreign.CaseId })
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync(Url(caseId), ApplicabilitySupport.With(Body(), expected))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync(Url(w.CaseId), new { regulatoryScope = "Nonsense", reasoning = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsync(Url(w.CaseId), Raw("{ not json"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsync(Url(w.CaseId), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsync(Url(foreign.CaseId), Raw("{ not json"))).StatusCode);
        Assert.Equal(before, await Versions(o.Org, w.CaseId)); Assert.Equal(auditBefore, (await Saved(o.Org, w.CaseId)).Count);

        // The Owner is told about the case first (404), then the body (400).
        await Problem(await o.Client.PutAsJsonAsync(Url(Guid.NewGuid()), ApplicabilitySupport.With(Body(), null)), HttpStatusCode.NotFound);
        await Problem(await o.Client.PutAsJsonAsync(Url(foreign.CaseId), ApplicabilitySupport.With(Body(), null)), HttpStatusCode.NotFound);
        await Problem(await o.Client.PutAsync(Url(w.CaseId), Raw("{ not json")), HttpStatusCode.BadRequest);

        // Members keep reading the current version and the history.
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync(Url(w.CaseId))).StatusCode);
        Assert.Equal(before, (await Json(await member.GetAsync(Url(w.CaseId, "applicability-history")), HttpStatusCode.OK)).GetArrayLength());
        await Problem(await member.GetAsync(Url(foreign.CaseId)), HttpStatusCode.NotFound);

        // The Owner succeeds.
        var saved = await Json(await PutBody(o.Client, w.CaseId, Body(), expected), HttpStatusCode.OK);
        Assert.Equal("RequiresLegalReview", saved.GetProperty("status").GetString());
        Assert.Equal(o.UserId, saved.GetProperty("current").GetProperty("decidedByUserId").GetGuid());
    }

    [Fact]
    public async Task The_owner_check_is_live_a_demoted_owner_with_a_valid_token_cannot_put_the_applicability()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org, Light); var expected = await ApplicabilitySupport.CurrentId(o.Client, w.CaseId);
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync($"""UPDATE "OrganizationUsers" SET "Role" = {(int)OrganizationRole.Member} WHERE "UserId" = {o.UserId}""");
        Assert.Equal(HttpStatusCode.Forbidden, (await PutBody(o.Client, w.CaseId, Body(), expected)).StatusCode); // the JWT still says Owner
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync($"""UPDATE "OrganizationUsers" SET "Role" = {(int)OrganizationRole.Owner} WHERE "UserId" = {o.UserId}""");
        Assert.Equal(HttpStatusCode.OK, (await PutBody(o.Client, w.CaseId, Body(), expected)).StatusCode);
    }

    [Fact]
    public async Task A_historical_member_decided_version_stays_readable_valid_and_is_superseded_by_an_owner_without_being_rewritten()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org, Light); var (member, memberId) = await NewMember(o.Org);
        var first = await ApplicabilitySupport.CurrentId(o.Client, w.CaseId);
        // Before this change a Member could record the assessment: simulate that stored state (the guard only covers tracked EF writes).
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync($"""UPDATE "CaseApplicabilities" SET "DecidedByUserId" = {memberId}, "CreatedByUserId" = {memberId} WHERE "Id" = {first}""");
        var read = await Current(member, w.CaseId);
        Assert.Equal("WithinSupportedScope", read.GetProperty("status").GetString()); Assert.Equal(memberId, read.GetProperty("current").GetProperty("decidedByUserId").GetGuid());

        var second = await Json(await PutBody(o.Client, w.CaseId, Body("InScope", "Supported", "Owner reassessment", "Green coffee beans", "Annex I", "2023/1115", null, "2026-06-01", "2026-01-01"), first), HttpStatusCode.OK);
        Assert.Equal(2, second.GetProperty("current").GetProperty("number").GetInt32());
        var history = await Json(await member.GetAsync(Url(w.CaseId, "applicability-history")), HttpStatusCode.OK);
        var v1 = history.EnumerateArray().Single(x => x.GetProperty("number").GetInt32() == 1);
        Assert.Equal(memberId, v1.GetProperty("decidedByUserId").GetGuid()); Assert.Equal(JsonValueKind.String, v1.GetProperty("supersededAtUtc").ValueKind);
        Assert.Equal("Assessed", v1.GetProperty("reasoning").GetString());
    }

    // ---- optimistic concurrency -------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_expected_current_id_is_checked_against_the_active_version_before_the_identical_no_op_and_mismatches_are_typed_conflicts()
    {
        var o = await NewOwner(); var c = o.Client; var w = await Build(c, o.Org, Light with { Applicability = false });
        Assert.Equal(HttpStatusCode.NoContent, (await c.GetAsync(Url(w.CaseId))).StatusCode);

        // First write: a non-null expected id is a mismatch (nothing existed); null succeeds.
        var phantom = Guid.NewGuid();
        var none = await Problem(await PutBody(c, w.CaseId, Body(), phantom), HttpStatusCode.Conflict);
        Assert.Equal("ApplicabilityHistoryChanged", Reason(none)); Assert.Equal(JsonValueKind.Null, none.GetProperty("errors").GetProperty("currentApplicabilityId").ValueKind);
        Assert.Equal(0, await Versions(o.Org, w.CaseId));
        var v1 = await Json(await PutBody(c, w.CaseId, Body(), null), HttpStatusCode.OK); var v1Id = v1.GetProperty("current").GetProperty("id").GetGuid();
        Assert.Equal(1, v1.GetProperty("current").GetProperty("number").GetInt32());

        // Null while a current version exists, even for identical content: a conflict, not a silent no-op.
        var stale = await Problem(await PutBody(c, w.CaseId, Body(), null), HttpStatusCode.Conflict);
        Assert.Equal("ApplicabilityHistoryChanged", Reason(stale));
        Assert.Equal(v1Id, stale.GetProperty("errors").GetProperty("currentApplicabilityId").GetGuid()); Assert.Equal(1, stale.GetProperty("errors").GetProperty("currentApplicabilityNumber").GetInt32());
        // A wrong id is the same conflict, and says nothing but the current identity.
        Assert.Equal("ApplicabilityHistoryChanged", Reason(await Problem(await PutBody(c, w.CaseId, Body(), phantom), HttpStatusCode.Conflict)));
        Assert.Equal(1, await Versions(o.Org, w.CaseId));

        // Identical content with the correct id stays a no-op: same version, no new row, no new audit event.
        var auditBefore = (await Saved(o.Org, w.CaseId)).Count;
        var same = await Json(await PutBody(c, w.CaseId, Body(), v1Id), HttpStatusCode.OK);
        Assert.Equal(v1Id, same.GetProperty("current").GetProperty("id").GetGuid()); Assert.Equal(1, await Versions(o.Org, w.CaseId)); Assert.Equal(auditBefore, (await Saved(o.Org, w.CaseId)).Count);

        // The exact id supersedes; the previous id is then stale.
        var v2 = await Json(await PutBody(c, w.CaseId, Body(reasoning: "Counsel asked for the customs heading."), v1Id), HttpStatusCode.OK); var v2Id = v2.GetProperty("current").GetProperty("id").GetGuid();
        Assert.NotEqual(v1Id, v2Id); Assert.Equal(2, v2.GetProperty("current").GetProperty("number").GetInt32());
        var again = await Problem(await PutBody(c, w.CaseId, Body(reasoning: "Another change."), v1Id), HttpStatusCode.Conflict);
        Assert.Equal(v2Id, again.GetProperty("errors").GetProperty("currentApplicabilityId").GetGuid()); Assert.Equal(2, again.GetProperty("errors").GetProperty("currentApplicabilityNumber").GetInt32());
        Assert.Equal(2, await Versions(o.Org, w.CaseId));
    }

    [Fact]
    public async Task Concurrent_puts_with_the_same_expected_id_serialize_so_exactly_one_wins_and_the_stale_writer_gets_the_typed_conflict()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org, Light); var expected = await ApplicabilitySupport.CurrentId(o.Client, w.CaseId);
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => PutBody(o.Client, w.CaseId, Body(reasoning: "Concurrent writer " + i), expected)));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        foreach (var loser in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict)) Assert.Equal("ApplicabilityHistoryChanged", Reason(await Problem(loser, HttpStatusCode.Conflict)));
        Assert.Equal(2, await Versions(o.Org, w.CaseId));
        Assert.Equal(1, await Query(o.Org, db => db.CaseApplicabilities.CountAsync(x => x.EudrCaseId == w.CaseId && x.SupersededAtUtc == null)));
    }

    [Fact]
    public async Task A_missing_master_data_copy_or_a_changed_shipment_is_the_typed_refresh_conflict_with_the_unchanged_detail()
    {
        var o = await NewOwner(); var c = o.Client;
        var supplier = (await Json(await c.PostAsJsonAsync("/api/suppliers", new { name = "Supplier " + Guid.NewGuid().ToString("N")[..8], countryCode = "BR" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var product = (await Json(await c.PostAsJsonAsync("/api/products", new { name = "Product " + Guid.NewGuid().ToString("N")[..8], cnCode = "0901", commodityType = "Coffee" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var shipment = await Json(await c.PostAsJsonAsync("/api/shipments", new { referenceNumber = "APPL-" + Guid.NewGuid().ToString("N")[..8], supplierId = supplier, productId = product, quantity = 10, unit = "Kg" }), HttpStatusCode.Created);
        var caseId = shipment.GetProperty("eudrCaseId").GetGuid();

        var none = await Problem(await PutBody(c, caseId, Body(), null), HttpStatusCode.Conflict);
        Assert.Equal("MasterDataRefreshRequired", Reason(none)); Assert.Equal("Refresh the case master data first.", none.GetProperty("detail").GetString());
        Assert.Equal(0, await Versions(o.Org, caseId));

        await Ok(c.PostAsync(Url(caseId, "master-data/refresh"), null));
        await Ok(PutBody(c, caseId, Body(), null));
        var otherProduct = (await Json(await c.PostAsJsonAsync("/api/products", new { name = "Other " + Guid.NewGuid().ToString("N")[..8], cnCode = "1801", commodityType = "Cocoa" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var shipmentId = shipment.GetProperty("shipment").GetProperty("id").GetGuid();
        await Ok(c.PutAsJsonAsync($"/api/shipments/{shipmentId}", new { referenceNumber = shipment.GetProperty("shipment").GetProperty("referenceNumber").GetString(), supplierId = supplier, productId = otherProduct, quantity = 10, unit = "Kg" }));
        var current = await ApplicabilitySupport.CurrentId(c, caseId);
        var mismatch = await Problem(await PutBody(c, caseId, Body(reasoning: "After the shipment changed."), current), HttpStatusCode.Conflict);
        Assert.Equal("MasterDataRefreshRequired", Reason(mismatch)); Assert.Equal("Refresh the case master data first.", mismatch.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Another_organizations_owner_cannot_reach_or_probe_the_applicability_of_a_case()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org, Light); var other = await NewOwner(); var expected = await ApplicabilitySupport.CurrentId(o.Client, w.CaseId);
        await Problem(await PutBody(other.Client, w.CaseId, Body(), expected), HttpStatusCode.NotFound);
        await Problem(await PutBody(other.Client, w.CaseId, Body(), null), HttpStatusCode.NotFound);
        await Problem(await other.Client.GetAsync(Url(w.CaseId)), HttpStatusCode.NotFound);
        await Problem(await other.Client.GetAsync(Url(w.CaseId, "applicability-history")), HttpStatusCode.NotFound);
        Assert.Equal(1, await Versions(o.Org, w.CaseId));
        // The foreign Owner's expected id for their own case is not accepted on this case either.
        var foreign = await Build(other.Client, other.Org, Light); var foreignId = await ApplicabilitySupport.CurrentId(other.Client, foreign.CaseId);
        Assert.Equal("ApplicabilityHistoryChanged", Reason(await Problem(await PutBody(o.Client, w.CaseId, Body(reasoning: "x"), foreignId), HttpStatusCode.Conflict)));
    }

    // ---- RequiresLegalReview ---------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Requires_legal_review_needs_reasoning_derives_its_own_status_and_blocks_every_positive_decision_but_not_a_return_for_correction()
    {
        var o = await NewOwner(); var c = o.Client; var w = await Build(c, o.Org, Light);
        var expected = await ApplicabilitySupport.CurrentId(c, w.CaseId);
        foreach (var reasoning in new[] { null, "", "   " })
            await Problem(await PutBody(c, w.CaseId, Body(reasoning: reasoning), expected), HttpStatusCode.BadRequest);
        Assert.Equal("WithinSupportedScope", await StatusOf(c, w.CaseId));

        var saved = await Json(await PutBody(c, w.CaseId, Body(flow: "Supported"), expected), HttpStatusCode.OK); // flow support is ignored while legal review is required
        Assert.Equal("RequiresLegalReview", saved.GetProperty("status").GetString());
        Assert.Equal("RequiresLegalReview", saved.GetProperty("current").GetProperty("regulatoryScope").GetString());
        var state = await Completeness(c, w.CaseId);
        Assert.Equal("RequiresLegalReview", state.GetProperty("applicability").GetProperty("status").GetString()); Assert.Equal("Incomplete", state.GetProperty("dataCoverage").GetString());
        string ItemStatus(string code) => state.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("code").GetString() == code).GetProperty("status").GetString()!;
        Assert.Equal("Missing", ItemStatus("APPLICABILITY_DECIDED")); Assert.Equal("Missing", ItemStatus("APPLICABILITY_IN_SCOPE")); Assert.Equal("Missing", ItemStatus("FLOW_SUPPORTED"));

        foreach (var decision in new[] { "AcceptedAsCompleteUnderPolicy", "ConfirmedOutsideRegulatoryScope", "ConfirmedUnsupportedFlow" })
        {
            var refused = await Problem(await TryReview(c, w.CaseId, decision), HttpStatusCode.Conflict);
            Assert.Contains("RequiresLegalReview", refused.GetProperty("detail").GetString()); Assert.Contains("RequiresLegalReview", Reason(refused));
        }
        var returned = await Review(c, w.CaseId, "ReturnedForCorrection");

        // A returned review never closes, and legal review cannot reach any closure path.
        var cannotClose = await Problem(await Close(c, w.CaseId, returned), HttpStatusCode.Conflict);
        Assert.Contains("returned the dossier for correction", cannotClose.GetProperty("detail").GetString());
        Assert.NotEqual("Closed", (await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}"))).GetProperty("status").GetString());

        // Resolving it by a later version makes the old review stale, and the resolved outcome closes normally.
        var resolved = await Json(await PutBody(c, w.CaseId, Body("OutsideRegulatoryScope", "Pending", "Counsel confirmed the heading is outside Annex I.", null, "Annex I", "2023/1115", null, "2026-06-01", "2026-01-01"), await ApplicabilitySupport.CurrentId(c, w.CaseId)), HttpStatusCode.OK);
        Assert.Equal("OutsideRegulatoryScope", resolved.GetProperty("status").GetString());
        Assert.Equal("Stale", (await Completeness(c, w.CaseId)).GetProperty("reviewFreshness").GetString());
        var review = await Review(c, w.CaseId, "ConfirmedOutsideRegulatoryScope");
        Assert.Equal("OutsideRegulatoryScope", (await Json(await Close(c, w.CaseId, review), HttpStatusCode.Created)).GetProperty("outcome").GetString());
    }

    // ---- review / risk / DDS chain ----------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_new_applicability_version_stales_the_review_and_the_earlier_risk_assessment_and_external_dds_record_no_longer_match_the_new_review()
    {
        var o = await NewOwner(); var c = o.Client; var w = await Build(c, o.Org);
        var review1 = await Review(c, w.CaseId, "AcceptedAsCompleteUnderPolicy");
        await RiskTestSupport.EnsureNegligible(c, w.CaseId, review1); await ExternalDdsTestSupport.RecordOk(c, w.CaseId, review1, "DDS-APPL-1");
        Assert.Equal("Current", (await Completeness(c, w.CaseId)).GetProperty("reviewFreshness").GetString());

        await Ok(PutBody(c, w.CaseId, Body("InScope", "Supported", "A revised assessment.", "Green coffee beans", "Annex I", "2023/1115", null, "2026-06-01", "2026-01-01"), await ApplicabilitySupport.CurrentId(c, w.CaseId)));
        var stale = await Completeness(c, w.CaseId);
        Assert.Equal("Stale", stale.GetProperty("reviewFreshness").GetString());
        Assert.Equal(["Applicability"], stale.GetProperty("latestReview").GetProperty("changedAreas").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal("StaleReview", Reason(await Problem(await Close(c, w.CaseId, review1), HttpStatusCode.Conflict)));

        var review2 = await Review(c, w.CaseId, "AcceptedAsCompleteUnderPolicy");
        Assert.Equal("RiskAssessmentReviewMismatch", Reason(await Problem(await Close(c, w.CaseId, review2), HttpStatusCode.Conflict)));
        await RiskTestSupport.EnsureNegligible(c, w.CaseId, review2);
        Assert.Equal("ExternalDdsReviewMismatch", Reason(await Problem(await Close(c, w.CaseId, review2), HttpStatusCode.Conflict)));
        await ExternalDdsTestSupport.RecordOk(c, w.CaseId, review2, "DDS-APPL-1");
        Assert.Equal("ExternalDdsCompleted", (await Json(await Close(c, w.CaseId, review2), HttpStatusCode.Created)).GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("ConfirmedOutsideRegulatoryScope", "OutsideRegulatoryScope")]
    [InlineData("ConfirmedUnsupportedFlow", "UnsupportedFlow")]
    public async Task The_outside_scope_and_unsupported_flow_closures_still_follow_the_recorded_applicability(string decision, string outcome)
    {
        var o = await NewOwner(); var c = o.Client; var w = await Build(c, o.Org, Light);
        var body = decision == "ConfirmedOutsideRegulatoryScope"
            ? Body("OutsideRegulatoryScope", "Pending", "Outside Annex I.", null, "Annex I", "2023/1115", null, "2026-06-01", "2026-01-01")
            : new { productDescription = "Green coffee beans", singleRelevantCommodity = true, activityType = "Export", operatorRole = "Operator", activityDate = "2026-06-01", activityDateKind = "Planned",
                scopeCitation = "Annex I", legalBasisVersion = "2023/1115", effectiveDate = "2026-01-01", reasoning = "Export is outside the pilot.", regulatoryScope = "InScope", flowSupport = "Supported" };
        await Ok(PutBody(c, w.CaseId, body, await ApplicabilitySupport.CurrentId(c, w.CaseId)));
        var review = await Review(c, w.CaseId, decision);
        Assert.Equal(outcome, (await Json(await Close(c, w.CaseId, review), HttpStatusCode.Created)).GetProperty("outcome").GetString());
    }

    // ---- lifecycle -------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_closed_or_archived_case_rejects_the_put_and_a_reopen_keeps_the_history_so_a_correction_is_a_new_version_needing_a_new_review()
    {
        var o = await NewOwner(); var c = o.Client; var w = await Build(c, o.Org, Light);
        await Ok(PutBody(c, w.CaseId, Body("OutsideRegulatoryScope", "Pending", "Outside Annex I.", null, "Annex I", "2023/1115", null, "2026-06-01", "2026-01-01"), await ApplicabilitySupport.CurrentId(c, w.CaseId)));
        var review = await Review(c, w.CaseId, "ConfirmedOutsideRegulatoryScope");
        var closure = (await Json(await Close(c, w.CaseId, review), HttpStatusCode.Created)).GetProperty("closureId").GetGuid();
        var current = await ApplicabilitySupport.CurrentId(c, w.CaseId); var versions = await Versions(o.Org, w.CaseId);

        await Problem(await PutBody(c, w.CaseId, Body(), current), HttpStatusCode.Conflict); // closed
        Assert.Equal(versions, await Versions(o.Org, w.CaseId));

        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync(Url(w.CaseId, "reopen"), new { expectedClosureId = closure, reason = "Counsel asked for another look." })).StatusCode);
        var corrected = await Json(await PutBody(c, w.CaseId, Body(), current), HttpStatusCode.OK);
        Assert.Equal(versions + 1, await Versions(o.Org, w.CaseId)); Assert.Equal("RequiresLegalReview", corrected.GetProperty("status").GetString());
        var history = await Json(await c.GetAsync(Url(w.CaseId, "applicability-history")), HttpStatusCode.OK);
        Assert.Equal(versions + 1, history.GetArrayLength()); Assert.Contains(history.EnumerateArray(), v => v.GetProperty("id").GetGuid() == current);
        Assert.Equal(1, (await Json(await c.GetAsync(Url(w.CaseId, "lifecycle")), HttpStatusCode.OK)).GetProperty("closures").GetArrayLength());

        // The old review is stale and already backs a closure; a new review is required before anything can close again.
        Assert.Equal("Stale", (await Completeness(c, w.CaseId)).GetProperty("reviewFreshness").GetString());
        Assert.Equal("ReviewAlreadyBacksClosure", Reason(await Problem(await Close(c, w.CaseId, review), HttpStatusCode.Conflict)));

        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync(Url(w.CaseId, "archive"), new { reason = "Retention hold." })).StatusCode);
        await Problem(await PutBody(c, w.CaseId, Body(reasoning: "While archived."), await ApplicabilitySupport.CurrentId(c, w.CaseId)), HttpStatusCode.Conflict);
    }

    // ---- audit, OpenAPI, schema --------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_audit_event_carries_identifiers_only_and_never_the_free_text()
    {
        var o = await NewOwner(); var c = o.Client; var w = await Build(c, o.Org, Light);
        const string marker = "SECRET-MARKER-7f3a";
        var saved = await Json(await PutBody(c, w.CaseId, Body(reasoning: marker + " reasoning", citation: marker + " citation", basis: marker + " basis", qualifications: marker + " qualifications", description: marker + " description"),
            await ApplicabilitySupport.CurrentId(c, w.CaseId)), HttpStatusCode.OK);
        var events = await Saved(o.Org, w.CaseId);
        Assert.True(events.Count >= 2);
        foreach (var e in events) { Assert.DoesNotContain(marker, e.Description); Assert.DoesNotContain(marker, e.MetadataJson ?? ""); }
        var latest = events.OrderByDescending(e => e.CreatedAtUtc).First();
        Assert.Contains(saved.GetProperty("current").GetProperty("id").GetGuid().ToString(), latest.Description); Assert.Contains("number 2", latest.Description);
    }

    [Fact]
    public async Task The_openapi_document_describes_the_manually_read_applicability_body_with_an_optional_expected_id_and_no_new_route()
    {
        using var anonymous = factory.CreateClient();
        var document = await Json(await anonymous.GetAsync("/openapi/v1.json"));
        var paths = document.GetProperty("paths");
        var operations = paths.GetProperty("/api/eudr-cases/{caseId}/applicability");
        Assert.Equal(["get", "put"], operations.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.False(operations.GetProperty("get").TryGetProperty("requestBody", out _));
        var body = operations.GetProperty("put").GetProperty("requestBody");
        Assert.True(body.GetProperty("required").GetBoolean());
        var schema = body.GetProperty("content").GetProperty("application/json").GetProperty("schema");
        if (schema.TryGetProperty("$ref", out var reference)) schema = document.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/').Last());
        Assert.True(schema.GetProperty("properties").TryGetProperty("expectedCurrentApplicabilityId", out _));
        var required = schema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(x => x.GetString()).ToArray() : [];
        Assert.DoesNotContain("expectedCurrentApplicabilityId", required);
        Assert.Contains("regulatoryScope", required);
        Assert.True(paths.TryGetProperty("/api/eudr-cases/{caseId}/applicability-history", out _));
    }

    [Fact]
    public async Task Adding_the_scope_value_needs_no_migration_because_the_column_is_an_unconstrained_integer()
    {
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(db.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model.FindEntityType(typeof(CaseApplicability))!.GetCheckConstraints());
        var checks = await db.Database.SqlQuery<string>($"""SELECT conname AS "Value" FROM pg_constraint WHERE conrelid = '"CaseApplicabilities"'::regclass AND contype = 'c'""").ToListAsync();
        Assert.Empty(checks);
        var types = await db.Database.SqlQuery<string>($"""SELECT data_type AS "Value" FROM information_schema.columns WHERE table_name = 'CaseApplicabilities' AND column_name = 'RegulatoryScope'""").ToListAsync();
        Assert.Equal(["integer"], types);
        var domains = await db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM information_schema.columns WHERE table_name = 'CaseApplicabilities' AND column_name = 'RegulatoryScope' AND domain_name IS NOT NULL""").ToListAsync();
        Assert.Equal([0], domains);
    }
}
