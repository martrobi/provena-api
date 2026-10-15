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

// Case Lifecycle v2, Slice 2: close / reopen / archive / unarchive over real HTTP and PostgreSQL. Closing needs the latest Owner review to be Current and to match what the Owner loaded;
// one review backs at most one closure; a closed or archived case rejects every business mutation; closing and archiving revoke supplier links and nothing restores them.
// Each scenario provisions its own organization through AuthService (the shared auth rate limiter is nearly consumed by other classes).
[Collection("Api")]
public sealed class CaseLifecycleTests(ApiFactory factory)
{
    private const string Password = "Test-Only-Password-928!";
    private sealed record Owner(HttpClient Client, Guid Org, Guid UserId);

    // ---- fixtures -------------------------------------------------------------------------------------------------------------------------
    private async Task<Owner> NewOwner()
    {
        var name = "Life-" + Guid.NewGuid().ToString("N")[..8];
        await using var scope = factory.Services.CreateAsyncScope();
        var token = await scope.ServiceProvider.GetRequiredService<AuthService>().Register(new RegisterRequest($"{name}@example.test", Password, "Test", "User", "Org " + name, null), CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Organizations.Where(x => x.Name == "Org " + name).Select(x => x.Id).SingleAsync();
        var user = await db.OrganizationUsers.Where(x => x.OrganizationId == org).Select(x => x.UserId).SingleAsync();
        return new(Client(token.AccessToken), org, user);
    }
    private HttpClient Client(string token) { var c = factory.CreateClient(); c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); return c; }
    // A genuine Member of the organization (moved there with the Member role, then logged in so the JWT is real).
    private async Task<HttpClient> NewMember(Guid org)
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
        return Client((await auth.Login(new LoginRequest(email, Password), CancellationToken.None)).AccessToken);
    }
    private static AppDbContext TenantDb(IServiceScope scope, Guid org)
    {
        var tenant = new TenantContext(new HttpContextAccessor()); tenant.SetOrganization(org);
        return new AppDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>(), tenant);
    }
    private async Task<T> Query<T>(Guid org, Func<AppDbContext, Task<T>> query) { await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org); return await query(db); }
    private Task<int> Closures(Guid org, Guid caseId) => Query(org, db => db.CaseClosures.CountAsync(x => x.EudrCaseId == caseId));
    private Task<int> Archivals(Guid org, Guid caseId) => Query(org, db => db.CaseArchivals.CountAsync(x => x.EudrCaseId == caseId));
    private Task<int> Audits(Guid org, Guid caseId, string type) => Query(org, db => db.AuditEvents.CountAsync(x => x.EudrCaseId == caseId && x.EventType == type));

    private static Task<HttpResponseMessage> Close(HttpClient c, Guid caseId, Guid? expectedReviewId, string? ddsReference = null) => c.PostAsJsonAsync($"/api/eudr-cases/{caseId}/close", new { expectedReviewId, externalDdsReference = ddsReference });
    // Records the external DDS submission of the review (replacing any current record), then closes as ExternalDdsCompleted with the same reference.
    private static async Task<HttpResponseMessage> CloseDds(HttpClient c, Guid caseId, Guid reviewId, string reference) { await ExternalDdsTestSupport.RecordOk(c, caseId, reviewId, reference); return await Close(c, caseId, reviewId, reference); }
    private static Task<HttpResponseMessage> Reopen(HttpClient c, Guid caseId, Guid? expectedClosureId, string? reason = "The data needs a correction.") => c.PostAsJsonAsync($"/api/eudr-cases/{caseId}/reopen", new { expectedClosureId, reason });
    private static Task<HttpResponseMessage> Archive(HttpClient c, Guid caseId, string? reason = "Retention hold.") => c.PostAsJsonAsync($"/api/eudr-cases/{caseId}/archive", new { reason });
    private static Task<HttpResponseMessage> Unarchive(HttpClient c, Guid caseId, string? reason = "Needed again.") => c.PostAsJsonAsync($"/api/eudr-cases/{caseId}/unarchive", new { reason });
    private static async Task<JsonElement> Lifecycle(HttpClient c, Guid caseId) => await Json(await c.GetAsync($"/api/eudr-cases/{caseId}/lifecycle"), HttpStatusCode.OK);
    private static Guid? LatestId(JsonElement completeness) => completeness.GetProperty("latestReview").ValueKind == JsonValueKind.Null ? null : completeness.GetProperty("latestReview").GetProperty("reviewId").GetGuid();
    // Loads the case exactly as an Owner would (fingerprint + latest review) and records the review; returns the review id.
    private static async Task<Guid> Review(HttpClient c, Guid caseId, string decision)
    {
        var state = await Completeness(c, caseId);
        var review = await Json(await c.PostAsJsonAsync($"/api/eudr-cases/{caseId}/reviews", new { decision, rationale = "Reviewed against the recorded facts.", expectedFingerprint = Fingerprint(state), expectedLatestReviewId = LatestId(state) }), HttpStatusCode.Created);
        return review.GetProperty("reviewId").GetGuid();
    }
    private static async Task<JsonElement> Problem(HttpResponseMessage response, HttpStatusCode status)
    { var json = await Json(response, status); Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType); return json; }
    private static async Task<string> StatusOf(HttpClient c, Guid caseId) => (await Json(await c.GetAsync($"/api/eudr-cases/{caseId}"))).GetProperty("status").GetString()!;
    private static async Task<JsonElement> Details(HttpClient c, Guid caseId) => await Json(await c.GetAsync($"/api/eudr-cases/{caseId}"));
    private static object Recipients(string name) => new { resolution = "RecipientsIdentified", recipients = new[] { new { name, postalAddress = "Weg 2, Wien", email = "other@example.test" } } };

    // A reviewed case ready to close: the dossier of a default Build accepted as complete (ExternalDdsCompleted), or a light case confirmed outside scope / unsupported flow.
    private static readonly Opt Light = new(Recipients: false, Lot: false, Documents: false);
    private async Task<(Owner Owner, World World, Guid ReviewId)> Reviewed(string decision, Opt? options = null)
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org, options ?? (decision == "AcceptedAsCompleteUnderPolicy" ? new Opt() : Light));
        if (decision == "ConfirmedOutsideRegulatoryScope") await Ok(o.Client.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/applicability", Application("OutsideRegulatoryScope", "Pending")));
        if (decision == "ConfirmedUnsupportedFlow") await Ok(o.Client.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/applicability", Application(activity: "Export")));
        return (o, w, await Review(o.Client, w.CaseId, decision));
    }
    private async Task<(Owner Owner, World World, JsonElement Closure)> Closed(string decision = "ConfirmedOutsideRegulatoryScope", Opt? options = null)
    {
        var (o, w, reviewId) = await Reviewed(decision, options);
        var closure = await Json(decision == "AcceptedAsCompleteUnderPolicy" ? await CloseDds(o.Client, w.CaseId, reviewId, "DDS-ABC-1") : await Close(o.Client, w.CaseId, reviewId), HttpStatusCode.Created);
        return (o, w, closure);
    }

    // ---- authorization: Owner before case and body ------------------------------------------------------------------------------------------
    [Fact]
    public async Task Close_reopen_archive_and_unarchive_are_owner_only_and_a_member_learns_nothing_about_the_case_or_the_body()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org, Light); var other = await NewOwner(); var foreign = await Build(other.Client, other.Org, Light);
        var member = await NewMember(o.Org); using var anonymous = factory.CreateClient();
        var routes = new[] { "close", "reopen", "archive", "unarchive" };
        var good = new Dictionary<string, object> { ["close"] = new { expectedReviewId = Guid.NewGuid() }, ["reopen"] = new { expectedClosureId = Guid.NewGuid(), reason = "r" }, ["archive"] = new { reason = "r" }, ["unarchive"] = new { reason = "r" } };
        foreach (var route in routes)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"/api/eudr-cases/{w.CaseId}/{route}", good[route])).StatusCode);
            // A genuine Member: 403 for an existing case, a nonexistent case, another organization's case and an invalid or malformed body - never 404, 400 or 415.
            foreach (var caseId in new[] { w.CaseId, Guid.NewGuid(), foreign.CaseId })
                Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync($"/api/eudr-cases/{caseId}/{route}", good[route])).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync($"/api/eudr-cases/{w.CaseId}/{route}", new { reason = "", expectedReviewId = Guid.Empty, expectedClosureId = Guid.Empty })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync($"/api/eudr-cases/{w.CaseId}/{route}", Raw("{ not json"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync($"/api/eudr-cases/{w.CaseId}/{route}", null)).StatusCode); // no body and no content type: still 403
            // The Owner is told about the content type, the case and the body in the documented order.
            // The Owner is told about the content type only once the state checks pass: reopen (case not closed) and unarchive (case not archived) answer 409 first.
            Assert.Equal(route is "close" or "archive" ? HttpStatusCode.UnsupportedMediaType : HttpStatusCode.Conflict, (await o.Client.PostAsync($"/api/eudr-cases/{w.CaseId}/{route}", null)).StatusCode);
            await Problem(await o.Client.PostAsJsonAsync($"/api/eudr-cases/{Guid.NewGuid()}/{route}", good[route]), HttpStatusCode.NotFound);
            await Problem(await o.Client.PostAsJsonAsync($"/api/eudr-cases/{foreign.CaseId}/{route}", good[route]), HttpStatusCode.NotFound);
        }
        Assert.Equal(0, await Closures(o.Org, w.CaseId)); Assert.Equal(0, await Archivals(o.Org, w.CaseId));
        foreach (var type in new[] { "CaseClosed", "CaseReopened", "CaseArchived", "CaseUnarchived" }) Assert.Equal(0, await Audits(o.Org, w.CaseId, type));
        // A Member can read the history.
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/eudr-cases/{w.CaseId}/lifecycle")).StatusCode);
        await Problem(await member.GetAsync($"/api/eudr-cases/{foreign.CaseId}/lifecycle"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_owner_check_is_live_a_demoted_owner_with_a_valid_token_is_denied_for_every_lifecycle_write()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org, Light);
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync($"""UPDATE "OrganizationUsers" SET "Role" = {(int)OrganizationRole.Member} WHERE "UserId" = {o.UserId}""");
        Assert.Equal(HttpStatusCode.Forbidden, (await Archive(o.Client, w.CaseId)).StatusCode); // the JWT still says Owner
        Assert.Equal(HttpStatusCode.Forbidden, (await Close(o.Client, w.CaseId, Guid.NewGuid())).StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync($"""UPDATE "OrganizationUsers" SET "Role" = {(int)OrganizationRole.Owner} WHERE "UserId" = {o.UserId}""");
        Assert.Equal(HttpStatusCode.Created, (await Archive(o.Client, w.CaseId)).StatusCode);
    }

    // ---- close: the review gate ---------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Closing_needs_the_latest_current_review_and_checks_in_the_documented_order()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org); var c = o.Client;
        // No review yet.
        var none = await Problem(await Close(c, w.CaseId, Guid.NewGuid()), HttpStatusCode.Conflict);
        Assert.Contains("No Owner review exists", none.GetProperty("detail").GetString());
        // Body shape (400) is checked before the review state; the DDS length too.
        foreach (var body in new object[] { new { }, new { expectedReviewId = (Guid?)null }, new { expectedReviewId = Guid.Empty }, new { expectedReviewId = Guid.NewGuid(), externalDdsReference = new string('x', 201) } })
            await Problem(await c.PostAsJsonAsync($"/api/eudr-cases/{w.CaseId}/close", body), HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"/api/eudr-cases/{w.CaseId}/close", Raw("{ not json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"/api/eudr-cases/{w.CaseId}/close", Raw("""{"expectedReviewId":"nope"}"""))).StatusCode);

        // A review that returned the dossier cannot close it.
        var returned = await Review(c, w.CaseId, "ReturnedForCorrection");
        var cannot = await Problem(await Close(c, w.CaseId, returned, "DDS-1"), HttpStatusCode.Conflict);
        Assert.Contains("cannot close", cannot.GetProperty("detail").GetString());

        // An older review id is not what the Owner should be closing: only the latest matches.
        var accepted = await Review(c, w.CaseId, "AcceptedAsCompleteUnderPolicy");
        var changed = await Problem(await Close(c, w.CaseId, returned, "DDS-1"), HttpStatusCode.Conflict);
        Assert.Contains("Review history changed", changed.GetProperty("detail").GetString());
        await Problem(await Close(c, w.CaseId, Guid.NewGuid(), "DDS-1"), HttpStatusCode.Conflict);

        // A stale review (the dossier changed after it) cannot close, and the response says what changed.
        await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/recipients", Recipients("Other GmbH")));
        var stale = await Problem(await Close(c, w.CaseId, accepted, "DDS-1"), HttpStatusCode.Conflict);
        Assert.Contains("stale", stale.GetProperty("detail").GetString());
        Assert.Contains("Recipients", stale.GetProperty("errors").GetProperty("changedAreas").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("Stale", (await Completeness(c, w.CaseId)).GetProperty("reviewFreshness").GetString());
        Assert.Equal(0, await Closures(o.Org, w.CaseId)); Assert.NotEqual("Closed", await StatusOf(c, w.CaseId));

        // A new review of the changed dossier closes it - ReadyForExport is not required (no supplier request was ever sent).
        Assert.Equal("Draft", await StatusOf(c, w.CaseId));
        var fresh = await Review(c, w.CaseId, "AcceptedAsCompleteUnderPolicy");
        Assert.Equal(HttpStatusCode.Created, (await CloseDds(c, w.CaseId, fresh, "DDS-1")).StatusCode);
    }

    [Theory]
    [InlineData("AcceptedAsCompleteUnderPolicy", "ExternalDdsCompleted")]
    [InlineData("ConfirmedOutsideRegulatoryScope", "OutsideRegulatoryScope")]
    [InlineData("ConfirmedUnsupportedFlow", "UnsupportedFlow")]
    public async Task Closing_maps_the_review_decision_to_the_outcome_and_applies_the_dds_reference_rule(string decision, string outcome)
    {
        var (o, w, reviewId) = await Reviewed(decision); var c = o.Client; var needsReference = outcome == "ExternalDdsCompleted";
        var link = await Json(await c.PostAsync($"/api/eudr-cases/{w.CaseId}/supplier-request", null)); var token = link.GetProperty("publicUrl").GetString()!.Split('/').Last();
        // The reference rule: other outcomes reject a legacy reference (400); ExternalDdsCompleted no longer takes the free text as its source, so without a recorded External DDS record it answers 409 whatever the field holds.
        foreach (var reference in needsReference ? new string?[] { null, "", "   ", "DDS-ABC-1" } : new string?[] { "DDS-1" })
        {
            var problem = await Problem(await Close(c, w.CaseId, reviewId, reference), needsReference ? HttpStatusCode.Conflict : HttpStatusCode.BadRequest);
            if (needsReference) Assert.Equal("ExternalDdsRecordMissing", problem.GetProperty("errors").GetProperty("reason").GetString());
            else Assert.True(problem.GetProperty("errors").TryGetProperty("ExternalDdsReference", out _));
        }
        Assert.Equal(0, await Closures(o.Org, w.CaseId)); Assert.NotEqual("Closed", await StatusOf(c, w.CaseId)); // nothing was written, the link still works
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync($"/public/supplier-requests/{token}")).StatusCode);

        if (needsReference) await ExternalDdsTestSupport.RecordOk(c, w.CaseId, reviewId, "DDS-ABC-1");
        var response = await Close(c, w.CaseId, reviewId, needsReference ? "  DDS-ABC-1  " : null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.EndsWith($"/api/eudr-cases/{w.CaseId}/lifecycle", response.Headers.Location!.AbsolutePath);
        var closure = await Json(response);
        Assert.Equal(outcome, closure.GetProperty("outcome").GetString()); Assert.Equal(decision, closure.GetProperty("reviewDecision").GetString());
        Assert.Equal(reviewId, closure.GetProperty("reviewId").GetGuid()); Assert.Equal(1, closure.GetProperty("number").GetInt32()); Assert.Equal(1, closure.GetProperty("reviewRevisionNumber").GetInt32());
        Assert.Equal(needsReference ? "DDS-ABC-1" : null, closure.GetProperty("externalDdsReference").GetString()); // trimmed
        Assert.Equal(o.UserId, closure.GetProperty("closedByUserId").GetGuid()); Assert.Equal(JsonValueKind.Null, closure.GetProperty("reopening").ValueKind);
        AssertNoOrganizationId(closure);

        // The workflow status, the details, the list and the history all show it; the review stays Current; the supplier link is revoked.
        var details = await Details(c, w.CaseId);
        Assert.Equal("Closed", details.GetProperty("status").GetString()); Assert.Equal(closure.GetProperty("closureId").GetGuid(), details.GetProperty("closure").GetProperty("closureId").GetGuid());
        Assert.Equal(JsonValueKind.Null, details.GetProperty("archive").ValueKind); Assert.False(details.TryGetProperty("completedAtUtc", out _)); Assert.False(details.TryGetProperty("externalDdsReference", out _));
        var item = (await Json(await c.GetAsync("/api/eudr-cases"))).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == w.CaseId);
        Assert.Equal("Closed", item.GetProperty("status").GetString()); Assert.Equal(outcome, item.GetProperty("closureOutcome").GetString()); Assert.False(item.GetProperty("archived").GetBoolean());
        Assert.Equal("Current", (await Completeness(c, w.CaseId)).GetProperty("reviewFreshness").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync($"/public/supplier-requests/{token}")).StatusCode);
        Assert.Equal(1, await Audits(o.Org, w.CaseId, "CaseClosed"));
        var audit = (await Query(o.Org, db => db.AuditEvents.Where(x => x.EventType == "CaseClosed" && x.EudrCaseId == w.CaseId).Select(x => x.Description).SingleAsync()));
        Assert.DoesNotContain("DDS-ABC-1", audit); // ids and the outcome only
        var lifecycle = await Lifecycle(c, w.CaseId);
        Assert.Equal("Closed", lifecycle.GetProperty("status").GetString()); Assert.False(lifecycle.GetProperty("archived").GetBoolean());
        Assert.Equal(closure.GetProperty("closureId").GetGuid(), lifecycle.GetProperty("currentClosure").GetProperty("closureId").GetGuid());
        Assert.Single(lifecycle.GetProperty("closures").EnumerateArray()); Assert.Empty(lifecycle.GetProperty("archivals").EnumerateArray());
        Assert.Equal(["status", "archived", "currentClosure", "currentArchival", "closures", "archivals"], lifecycle.EnumerateObject().Select(p => p.Name).ToArray());
        AssertNoOrganizationId(lifecycle);
        // The same review cannot close again (the case is already closed).
        Assert.Contains("already closed", (await Problem(await Close(c, w.CaseId, reviewId, needsReference ? "DDS-2" : null), HttpStatusCode.Conflict)).GetProperty("detail").GetString());
    }

    // ---- a closed or archived case is immutable ------------------------------------------------------------------------------------------------
    private async Task<List<(string Name, HttpResponseMessage Response)>> Attempt(HttpClient c, World w, JsonElement requirements, Guid requestId, bool closeAgain, bool archiveAgain)
    {
        var results = new List<(string, HttpResponseMessage)>(); var caseId = w.CaseId;
        async Task Add(string name, Task<HttpResponseMessage> request) => results.Add((name, await request));
        var requirement = requirements[0].GetProperty("id").GetGuid();
        await Add("requirement review", Patch(c, $"/api/eudr-cases/{caseId}/requirements/{requirement}", new { status = "Accepted", reviewerNote = "x" }));
        await Add("create supplier request", c.PostAsync($"/api/eudr-cases/{caseId}/supplier-request", null));
        await Add("regenerate supplier request", c.PostAsync($"/api/supplier-requests/{requestId}/regenerate", null));
        using (var doc = Upload("late.txt", "text/plain", "late")) await Add("document upload", c.PostAsync($"/api/eudr-cases/{caseId}/documents", doc));
        await Add("document withdrawal", c.DeleteAsync($"/api/eudr-cases/{caseId}/documents/{w.Docs[0]}"));
        using (var geo = Upload("late.geojson", "application/geo+json", """{"type":"Polygon","coordinates":[[[-48,-20],[-47,-20],[-47,-19],[-48,-20]]]}""")) await Add("geolocation upload", c.PostAsync($"/api/eudr-cases/{caseId}/geolocation", geo));
        await Add("lot", c.PutAsJsonAsync($"/api/eudr-cases/{caseId}/dossier/lot", new { lotReference = "LATE", quantity = 1, unit = "Kg", contributions = Array.Empty<object>() }));
        await Add("master data refresh", c.PostAsync($"/api/eudr-cases/{caseId}/master-data/refresh", null));
        await Add("applicability", c.PutAsJsonAsync($"/api/eudr-cases/{caseId}/applicability", Application()));
        await Add("recipients", c.PutAsJsonAsync($"/api/eudr-cases/{caseId}/recipients", Recipients("Late GmbH")));
        await Add("evidence link create", c.PostAsJsonAsync($"/api/eudr-cases/{caseId}/evidence-links", new { caseDocumentId = w.Docs[0], purpose = "Applicability" }));
        await Add("evidence link withdraw", c.DeleteAsync($"/api/eudr-cases/{caseId}/evidence-links/{w.Links[0]}"));
        var state = await Completeness(c, caseId);
        await Add("review", c.PostAsJsonAsync($"/api/eudr-cases/{caseId}/reviews", new { decision = "ReturnedForCorrection", rationale = "r", expectedFingerprint = Fingerprint(state), expectedLatestReviewId = LatestId(state) }));
        await Add("shipment", c.PutAsJsonAsync($"/api/shipments/{w.ShipmentId}", new { referenceNumber = w.Reference, supplierId = w.SupplierId, productId = w.ProductId, quantity = 11, unit = "Kg" }));
        if (closeAgain) await Add("close again", Close(c, caseId, LatestId(state), "DDS-LATE"));
        if (archiveAgain) await Add("archive again", Archive(c, caseId)); // archiving a closed case is allowed, so it is only a mutation to refuse while already archived
        return results;
    }

    [Fact]
    public async Task A_closed_case_rejects_every_business_mutation_until_it_is_reopened_but_stays_readable()
    {
        var o = await NewOwner(); var c = o.Client; var w = await Build(c, o.Org);
        var link = await Json(await c.PostAsync($"/api/eudr-cases/{w.CaseId}/supplier-request", null)); var requestId = link.GetProperty("requestId").GetGuid(); var token = link.GetProperty("publicUrl").GetString()!.Split('/').Last();
        var reviewId = await Review(c, w.CaseId, "AcceptedAsCompleteUnderPolicy");
        Assert.Equal(HttpStatusCode.Created, (await CloseDds(c, w.CaseId, reviewId, "DDS-1")).StatusCode);
        var requirements = (await Details(c, w.CaseId)).GetProperty("requirements");
        var before = (await Details(c, w.CaseId)).GetProperty("requirements").GetRawText();
        var audits = await Query(o.Org, db => db.AuditEvents.CountAsync(x => x.EudrCaseId == w.CaseId));

        foreach (var (name, response) in await Attempt(c, w, requirements, requestId, closeAgain: true, archiveAgain: false))
        {
            Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{name}: expected 409, got {(int)response.StatusCode}");
            Assert.Contains("closed", (await response.Content.ReadAsStringAsync()).ToLowerInvariant());
        }
        // Public capability routes also refuse a closed case. Closing revoked the link, so un-revoke it to prove the case check itself holds (defence in depth, not a product path).
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync($"""UPDATE "SupplierRequests" SET "RevokedAtUtc" = NULL WHERE "Id" = {requestId}""");
        using var publicClient = factory.CreateClient(); var pathBase = $"/public/supplier-requests/{token}";
        Assert.Equal(HttpStatusCode.Conflict, (await publicClient.GetAsync(pathBase)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await publicClient.PutAsJsonAsync(pathBase + "/details", new { countryOfProductionCode = "BR" })).StatusCode);
        using (var doc = Upload("late.txt", "text/plain", "late")) Assert.Equal(HttpStatusCode.Conflict, (await publicClient.PostAsync(pathBase + "/documents", doc)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await publicClient.PostAsync(pathBase + "/complete", null)).StatusCode);
        Assert.Equal(before, (await Details(c, w.CaseId)).GetProperty("requirements").GetRawText());
        Assert.Equal(audits, await Query(o.Org, db => db.AuditEvents.CountAsync(x => x.EudrCaseId == w.CaseId))); // rejected writes leave no trace
        Assert.Equal(1, await Closures(o.Org, w.CaseId));

        // Reads and the exports are still available; the reviewed record is untouched and Current.
        Assert.Equal("Current", (await Completeness(c, w.CaseId)).GetProperty("reviewFreshness").GetString());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/eudr-cases/{w.CaseId}/export")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/eudr-cases/{w.CaseId}/reviews/{reviewId}/export")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/eudr-cases/{w.CaseId}/reviews")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync($"/api/supplier-requests/{requestId}/revoke", null)).StatusCode); // revoking only reduces access

        // Reopening makes the same writes possible again.
        var closureId = (await Lifecycle(c, w.CaseId)).GetProperty("currentClosure").GetProperty("closureId").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await Reopen(c, w.CaseId, closureId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/recipients", Recipients("After reopen"))).StatusCode);
    }

    // ---- reopen --------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Reopening_needs_the_current_closure_and_a_reason_keeps_all_history_and_never_restores_links_and_a_review_backs_only_one_closure()
    {
        var o = await NewOwner(); var c = o.Client; var w = await Build(c, o.Org);
        var link = await Json(await c.PostAsync($"/api/eudr-cases/{w.CaseId}/supplier-request", null)); var oldToken = link.GetProperty("publicUrl").GetString()!.Split('/').Last();
        var review1 = await Review(c, w.CaseId, "AcceptedAsCompleteUnderPolicy");
        // Not closed yet.
        Assert.Contains("not closed", (await Problem(await Reopen(c, w.CaseId, Guid.NewGuid()), HttpStatusCode.Conflict)).GetProperty("detail").GetString());
        var closure1 = await Json(await CloseDds(c, w.CaseId, review1, "DDS-FIRST"), HttpStatusCode.Created); var closure1Id = closure1.GetProperty("closureId").GetGuid();

        // Body shape: expectedClosureId and a non-blank reason are required.
        foreach (var body in new object[] { new { reason = "r" }, new { expectedClosureId = Guid.Empty, reason = "r" }, new { expectedClosureId = closure1Id }, new { expectedClosureId = closure1Id, reason = "   " }, new { expectedClosureId = closure1Id, reason = new string('r', 4001) } })
            await Problem(await c.PostAsJsonAsync($"/api/eudr-cases/{w.CaseId}/reopen", body), HttpStatusCode.BadRequest);
        await Problem(await Reopen(c, w.CaseId, Guid.NewGuid()), HttpStatusCode.Conflict); // not the closure the Owner loaded
        Assert.Equal("Closed", await StatusOf(c, w.CaseId));

        var reopened = await Reopen(c, w.CaseId, closure1Id, "  Supplier sent a corrected geometry.  ");
        Assert.Equal(HttpStatusCode.Created, reopened.StatusCode);
        var reopening = await Json(reopened);
        Assert.Equal(closure1Id, reopening.GetProperty("closureId").GetGuid()); Assert.Equal("Supplier sent a corrected geometry.", reopening.GetProperty("reason").GetString()); Assert.Equal(o.UserId, reopening.GetProperty("reopenedByUserId").GetGuid());
        AssertNoOrganizationId(reopening);
        // The workflow status is derived afresh (a request was sent and the requirements are decided: Draft is not it); the closure is no longer current.
        var details = await Details(c, w.CaseId);
        Assert.Equal("ReadyForExport", details.GetProperty("status").GetString()); Assert.Equal(JsonValueKind.Null, details.GetProperty("closure").ValueKind);
        var afterReopen = await Lifecycle(c, w.CaseId);
        Assert.Equal(JsonValueKind.Null, afterReopen.GetProperty("currentClosure").ValueKind);
        var history = afterReopen.GetProperty("closures")[0];
        Assert.Equal("DDS-FIRST", history.GetProperty("externalDdsReference").GetString()); Assert.Equal(review1, history.GetProperty("reviewId").GetGuid());
        Assert.Equal(reopening.GetProperty("reopeningId").GetGuid(), history.GetProperty("reopening").GetProperty("reopeningId").GetGuid());
        // Reviews, DDS reference and audit history are untouched; links are not restored (the old token stays dead, and a member must create a new request).
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/eudr-cases/{w.CaseId}/reviews/{review1}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync($"/public/supplier-requests/{oldToken}")).StatusCode);
        Assert.NotNull(await Query(o.Org, db => db.SupplierRequests.Where(x => x.EudrCaseId == w.CaseId).Select(x => x.RevokedAtUtc).SingleAsync()));
        var reopenAudit = await Query(o.Org, db => db.AuditEvents.Where(x => x.EventType == "CaseReopened" && x.EudrCaseId == w.CaseId).Select(x => x.Description).SingleAsync());
        Assert.DoesNotContain("corrected geometry", reopenAudit); // ids only: the reason lives in the immutable reopening row

        // The old review still describes the dossier (Current) but backs a closure already: closing again needs a NEW review, even though nothing changed.
        Assert.Equal("Current", (await Completeness(c, w.CaseId)).GetProperty("reviewFreshness").GetString());
        Assert.Contains("already backs a closure", (await Problem(await Close(c, w.CaseId, review1, "DDS-SECOND"), HttpStatusCode.Conflict)).GetProperty("detail").GetString());
        var review2 = await Review(c, w.CaseId, "AcceptedAsCompleteUnderPolicy");
        var closure2 = await Json(await CloseDds(c, w.CaseId, review2, "DDS-SECOND"), HttpStatusCode.Created);
        Assert.Equal(2, closure2.GetProperty("number").GetInt32()); Assert.Equal(2, closure2.GetProperty("reviewRevisionNumber").GetInt32());

        // Both closures stay reconstructable, with their own references and reviews; the current one is the second.
        var lifecycle = await Lifecycle(c, w.CaseId);
        var closures = lifecycle.GetProperty("closures").EnumerateArray().ToList();
        Assert.Equal([1, 2], closures.Select(x => x.GetProperty("number").GetInt32()).ToArray());
        Assert.Equal(["DDS-FIRST", "DDS-SECOND"], closures.Select(x => x.GetProperty("externalDdsReference").GetString()!).ToArray());
        Assert.Equal([review1, review2], closures.Select(x => x.GetProperty("reviewId").GetGuid()).ToArray());
        Assert.Equal(JsonValueKind.Object, closures[0].GetProperty("reopening").ValueKind); Assert.Equal(JsonValueKind.Null, closures[1].GetProperty("reopening").ValueKind);
        Assert.Equal(closure2.GetProperty("closureId").GetGuid(), lifecycle.GetProperty("currentClosure").GetProperty("closureId").GetGuid());
        Assert.Equal(2, await Closures(o.Org, w.CaseId)); Assert.Equal(2, await Audits(o.Org, w.CaseId, "CaseClosed")); Assert.Equal(1, await Audits(o.Org, w.CaseId, "CaseReopened"));
        // The working export carries the same history.
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(await (await c.GetAsync($"/api/eudr-cases/{w.CaseId}/export")).Content.ReadAsByteArrayAsync()));
        using var manifest = JsonDocument.Parse(await new StreamReader(zip.Entries.Single(e => e.FullName.EndsWith("/manifest.json")).Open()).ReadToEndAsync());
        Assert.Equal(2, manifest.RootElement.GetProperty("lifecycle").GetProperty("closures").GetArrayLength());
        Assert.False(manifest.RootElement.TryGetProperty("externalDdsReference", out _)); Assert.False(manifest.RootElement.TryGetProperty("completedAtUtc", out _));
        // Reopening again with the old closure id is refused: only the current closure can be reopened.
        await Problem(await Reopen(c, w.CaseId, closure1Id), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task After_a_reopen_a_change_to_the_dossier_makes_the_old_review_stale_and_blocks_closing_until_a_new_review()
    {
        var (o, w, closure) = await Closed("AcceptedAsCompleteUnderPolicy", new Opt()); var c = o.Client;
        var review = (await Lifecycle(c, w.CaseId)).GetProperty("currentClosure").GetProperty("reviewId").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await Reopen(c, w.CaseId, closure.GetProperty("closureId").GetGuid())).StatusCode);
        await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/recipients", Recipients("Changed after reopen")));
        Assert.Equal("Stale", (await Completeness(c, w.CaseId)).GetProperty("reviewFreshness").GetString());
        await Problem(await Close(c, w.CaseId, review, "DDS-X"), HttpStatusCode.Conflict); // used AND stale: the review is refused either way
        Assert.Equal(1, await Closures(o.Org, w.CaseId));
    }

    // ---- archive / unarchive -------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Archive_is_independent_of_the_workflow_status_blocks_every_business_mutation_revokes_links_and_unarchive_restores_nothing()
    {
        var o = await NewOwner(); var c = o.Client; var w = await Build(c, o.Org);
        var other = await Build(c, o.Org, Light); // a second active case so the default list proves the filter
        var link = await Json(await c.PostAsync($"/api/eudr-cases/{w.CaseId}/supplier-request", null)); var requestId = link.GetProperty("requestId").GetGuid(); var token = link.GetProperty("publicUrl").GetString()!.Split('/').Last();
        var statusBefore = await StatusOf(c, w.CaseId);
        Assert.NotEqual("Closed", statusBefore);

        // Reasons are required and non-blank; an unknown case is 404; a case that is not archived cannot be unarchived.
        foreach (var body in new object[] { new { }, new { reason = "" }, new { reason = "   " }, new { reason = new string('r', 4001) } })
            await Problem(await c.PostAsJsonAsync($"/api/eudr-cases/{w.CaseId}/archive", body), HttpStatusCode.BadRequest);
        Assert.Contains("not archived", (await Problem(await Unarchive(c, w.CaseId), HttpStatusCode.Conflict)).GetProperty("detail").GetString());
        Assert.Equal(0, await Archivals(o.Org, w.CaseId));

        var response = await Archive(c, w.CaseId, "  Retention hold.  ");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.EndsWith($"/api/eudr-cases/{w.CaseId}/lifecycle", response.Headers.Location!.AbsolutePath);
        var archival = await Json(response);
        Assert.Equal(1, archival.GetProperty("number").GetInt32()); Assert.Equal("Retention hold.", archival.GetProperty("reason").GetString()); Assert.Equal(o.UserId, archival.GetProperty("archivedByUserId").GetGuid());
        Assert.Equal(JsonValueKind.Null, archival.GetProperty("unarchivedAtUtc").ValueKind); AssertNoOrganizationId(archival);
        Assert.Equal(statusBefore, await StatusOf(c, w.CaseId)); // archive never rewrites the workflow status
        Assert.Contains("already archived", (await Problem(await Archive(c, w.CaseId), HttpStatusCode.Conflict)).GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync($"/public/supplier-requests/{token}")).StatusCode); // links revoked

        // Details, list filtering (archived cases are hidden by default) and status filters that no longer exist.
        var details = await Details(c, w.CaseId);
        Assert.Equal(archival.GetProperty("archivalId").GetGuid(), details.GetProperty("archive").GetProperty("archivalId").GetGuid()); Assert.Equal(JsonValueKind.Null, details.GetProperty("closure").ValueKind);
        async Task<List<Guid>> ListIds(string query) => (await Json(await c.GetAsync("/api/eudr-cases?pageSize=100" + query))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.DoesNotContain(w.CaseId, await ListIds("")); Assert.Contains(other.CaseId, await ListIds(""));
        Assert.DoesNotContain(w.CaseId, await ListIds("&archived=exclude"));
        Assert.Equal([w.CaseId], await ListIds("&archived=only")); Assert.Contains(w.CaseId, await ListIds("&archived=include")); Assert.Contains(other.CaseId, await ListIds("&archived=include"));
        Assert.Equal([w.CaseId], await ListIds($"&archived=ONLY&status={statusBefore}")); // the status filter applies to the workflow status of archived cases too
        var archivedItem = (await Json(await c.GetAsync("/api/eudr-cases?archived=only"))).GetProperty("items")[0];
        Assert.True(archivedItem.GetProperty("archived").GetBoolean()); Assert.Equal(statusBefore, archivedItem.GetProperty("status").GetString());
        foreach (var query in new[] { "archived=maybe", "archived=99", "status=Archived", "status=Completed" })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/eudr-cases?" + query)).StatusCode);

        // Every business mutation is refused while archived; reads and exports are not.
        var requirements = details.GetProperty("requirements"); var audits = await Query(o.Org, db => db.AuditEvents.CountAsync(x => x.EudrCaseId == w.CaseId));
        foreach (var (name, result) in await Attempt(c, w, requirements, requestId, closeAgain: true, archiveAgain: true))
        {
            Assert.True(result.StatusCode == HttpStatusCode.Conflict, $"{name}: expected 409, got {(int)result.StatusCode}");
            Assert.Contains("archived", (await result.Content.ReadAsStringAsync()).ToLowerInvariant());
        }
        Assert.Contains("archived", (await Problem(await Reopen(c, w.CaseId, Guid.NewGuid()), HttpStatusCode.Conflict)).GetProperty("detail").GetString()!.ToLowerInvariant()); // archived is checked before "not closed"
        Assert.Equal(audits, await Query(o.Org, db => db.AuditEvents.CountAsync(x => x.EudrCaseId == w.CaseId)));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/eudr-cases/{w.CaseId}/export")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/eudr-cases/{w.CaseId}/completeness")).StatusCode);

        // Unarchive: a reason is required, the status is what it was, nothing is restored, and the period stays as history.
        await Problem(await c.PostAsJsonAsync($"/api/eudr-cases/{w.CaseId}/unarchive", new { reason = " " }), HttpStatusCode.BadRequest);
        var unarchived = await Json(await Unarchive(c, w.CaseId, "  Needed again.  "));
        Assert.Equal("Needed again.", unarchived.GetProperty("unarchiveReason").GetString()); Assert.Equal(o.UserId, unarchived.GetProperty("unarchivedByUserId").GetGuid()); Assert.Equal(JsonValueKind.String, unarchived.GetProperty("unarchivedAtUtc").ValueKind);
        Assert.Equal(statusBefore, await StatusOf(c, w.CaseId)); Assert.Contains(w.CaseId, await ListIds(""));
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync($"/public/supplier-requests/{token}")).StatusCode); // the revoked link is not restored
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/recipients", Recipients("After unarchive"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Archive(c, w.CaseId, "Second hold.")).StatusCode);
        var lifecycle = await Lifecycle(c, w.CaseId);
        var periods = lifecycle.GetProperty("archivals").EnumerateArray().ToList();
        Assert.Equal([1, 2], periods.Select(p => p.GetProperty("number").GetInt32()).ToArray());
        Assert.Equal("Retention hold.", periods[0].GetProperty("reason").GetString()); Assert.Equal("Needed again.", periods[0].GetProperty("unarchiveReason").GetString()); Assert.Equal(JsonValueKind.Null, periods[1].GetProperty("unarchivedAtUtc").ValueKind);
        Assert.True(lifecycle.GetProperty("archived").GetBoolean()); Assert.Equal(2, lifecycle.GetProperty("currentArchival").GetProperty("number").GetInt32());
        foreach (var (type, count) in new[] { ("CaseArchived", 2), ("CaseUnarchived", 1) }) Assert.Equal(count, await Audits(o.Org, w.CaseId, type));
        var archiveAudit = await Query(o.Org, db => db.AuditEvents.Where(x => x.EventType == "CaseArchived" && x.EudrCaseId == w.CaseId).Select(x => x.Description).FirstAsync());
        Assert.DoesNotContain("Retention hold", archiveAudit); // ids only
    }

    [Fact]
    public async Task Archiving_a_closed_case_keeps_it_closed_blocks_reopen_while_archived_and_loses_no_lifecycle_history()
    {
        var (o, w, closure) = await Closed(); var c = o.Client; var closureId = closure.GetProperty("closureId").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await Archive(c, w.CaseId)).StatusCode);
        Assert.Equal("Closed", await StatusOf(c, w.CaseId)); // still Closed: archive is a separate dimension
        var details = await Details(c, w.CaseId);
        Assert.Equal(closureId, details.GetProperty("closure").GetProperty("closureId").GetGuid()); Assert.NotEqual(JsonValueKind.Null, details.GetProperty("archive").ValueKind);
        Assert.Contains("archived", (await Problem(await Reopen(c, w.CaseId, closureId), HttpStatusCode.Conflict)).GetProperty("detail").GetString()!.ToLowerInvariant());
        Assert.Equal(HttpStatusCode.OK, (await Unarchive(c, w.CaseId)).StatusCode);
        Assert.Equal("Closed", await StatusOf(c, w.CaseId));
        var lifecycle = await Lifecycle(c, w.CaseId);
        Assert.Equal(closureId, lifecycle.GetProperty("currentClosure").GetProperty("closureId").GetGuid()); Assert.Single(lifecycle.GetProperty("closures").EnumerateArray()); Assert.Single(lifecycle.GetProperty("archivals").EnumerateArray());
        Assert.Equal(HttpStatusCode.Created, (await Reopen(c, w.CaseId, closureId)).StatusCode); // allowed again once unarchived
    }

    // ---- dashboard -------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Dashboard_buckets_are_mutually_exclusive_cover_every_case_once_and_shift_with_archive()
    {
        var o = await NewOwner(); var c = o.Client; var stranger = await NewOwner();
        async Task<JsonElement> Dashboard() => await Json(await c.GetAsync("/api/dashboard"));
        void Check(JsonElement d, int open, int closed, int archived, int draft, int waiting, int[] outcomes)
        {
            Assert.Equal(["total", "activeOpen", "activeOpenByStatus", "activeClosed", "activeClosedByOutcome", "archived"], d.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal(["draft", "waitingForSupplier", "reviewNeeded", "readyForExport"], d.GetProperty("activeOpenByStatus").EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal(["externalDdsCompleted", "outsideRegulatoryScope", "unsupportedFlow"], d.GetProperty("activeClosedByOutcome").EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal(open, d.GetProperty("activeOpen").GetInt32()); Assert.Equal(closed, d.GetProperty("activeClosed").GetInt32()); Assert.Equal(archived, d.GetProperty("archived").GetInt32());
            Assert.Equal(open + closed + archived, d.GetProperty("total").GetInt32());
            Assert.Equal(draft, d.GetProperty("activeOpenByStatus").GetProperty("draft").GetInt32()); Assert.Equal(waiting, d.GetProperty("activeOpenByStatus").GetProperty("waitingForSupplier").GetInt32());
            Assert.Equal(open, d.GetProperty("activeOpenByStatus").EnumerateObject().Sum(p => p.Value.GetInt32())); // the breakdown sums to its bucket
            Assert.Equal(outcomes, d.GetProperty("activeClosedByOutcome").EnumerateObject().Select(p => p.Value.GetInt32()).ToArray());
            Assert.Equal(closed, outcomes.Sum());
        }
        Check(await Dashboard(), 0, 0, 0, 0, 0, [0, 0, 0]);

        var draft = await Build(c, o.Org, Light);                                                                      // active, Draft
        var waiting = await Build(c, o.Org, Light); await Ok(c.PostAsync($"/api/eudr-cases/{waiting.CaseId}/supplier-request", null)); // active, WaitingForSupplier
        var archivedOpen = await Build(c, o.Org, Light); await Archive(c, archivedOpen.CaseId);                        // archived (Draft underneath)
        var (_, outside, _) = await ReviewedAndClosed(c, o.Org, "ConfirmedOutsideRegulatoryScope");                    // active closed
        var (_, external, _) = await ReviewedAndClosed(c, o.Org, "AcceptedAsCompleteUnderPolicy");                     // active closed
        var (_, unsupported, _) = await ReviewedAndClosed(c, o.Org, "ConfirmedUnsupportedFlow");                       // archived closed below
        await Archive(c, unsupported.CaseId);
        // A stranger's cases are never counted.
        var strangerCase = await Build(stranger.Client, stranger.Org, Light); await Archive(stranger.Client, strangerCase.CaseId);

        Check(await Dashboard(), open: 2, closed: 2, archived: 2, draft: 1, waiting: 1, outcomes: [1, 1, 0]);
        Assert.Equal(1, (await Dashboard()).GetProperty("activeClosedByOutcome").GetProperty("externalDdsCompleted").GetInt32());
        // Archiving moves exactly one count out of its active bucket; unarchiving moves it back.
        Assert.Equal(HttpStatusCode.Created, (await Archive(c, outside.CaseId)).StatusCode);
        Check(await Dashboard(), 2, 1, 3, 1, 1, [1, 0, 0]);
        Assert.Equal(HttpStatusCode.OK, (await Unarchive(c, unsupported.CaseId)).StatusCode);
        Check(await Dashboard(), 2, 2, 2, 1, 1, [1, 0, 1]);
        Assert.Equal(HttpStatusCode.Created, (await Archive(c, draft.CaseId)).StatusCode);
        Check(await Dashboard(), 1, 2, 3, 0, 1, [1, 0, 1]);
        // Reopening moves a closed case into the open bucket (recalculated status).
        var closureId = (await Lifecycle(c, external.CaseId)).GetProperty("currentClosure").GetProperty("closureId").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await Reopen(c, external.CaseId, closureId)).StatusCode);
        Check(await Dashboard(), 2, 1, 3, 1, 1, [0, 0, 1]);
    }

    private async Task<(Guid ReviewId, World World, JsonElement Closure)> ReviewedAndClosed(HttpClient c, Guid org, string decision)
    {
        var w = await Build(c, org, decision == "AcceptedAsCompleteUnderPolicy" ? new Opt() : Light);
        if (decision == "ConfirmedOutsideRegulatoryScope") await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/applicability", Application("OutsideRegulatoryScope", "Pending")));
        if (decision == "ConfirmedUnsupportedFlow") await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/applicability", Application(activity: "Export")));
        var review = await Review(c, w.CaseId, decision);
        return (review, w, await Json(decision == "AcceptedAsCompleteUnderPolicy" ? await CloseDds(c, w.CaseId, review, "DDS-D") : await Close(c, w.CaseId, review), HttpStatusCode.Created));
    }

    // ---- concurrency ---------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Simultaneous_lifecycle_writes_on_one_case_are_serialized_so_exactly_one_wins()
    {
        var (o, w, reviewId) = await Reviewed("ConfirmedOutsideRegulatoryScope"); var c = o.Client;
        // Two identical closes: one 201, one 409; exactly one closure and one audit event.
        var closes = await Task.WhenAll(Close(c, w.CaseId, reviewId), Close(c, w.CaseId, reviewId));
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], closes.Select(r => r.StatusCode).Order().ToArray());
        Assert.Equal(1, await Closures(o.Org, w.CaseId)); Assert.Equal(1, await Audits(o.Org, w.CaseId, "CaseClosed"));
        var closureId = (await Lifecycle(c, w.CaseId)).GetProperty("currentClosure").GetProperty("closureId").GetGuid();
        // Two identical reopens, then two identical archives.
        var reopens = await Task.WhenAll(Reopen(c, w.CaseId, closureId), Reopen(c, w.CaseId, closureId));
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], reopens.Select(r => r.StatusCode).Order().ToArray());
        var archives = await Task.WhenAll(Archive(c, w.CaseId), Archive(c, w.CaseId));
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], archives.Select(r => r.StatusCode).Order().ToArray());
        Assert.Equal(1, await Archivals(o.Org, w.CaseId));
        var unarchives = await Task.WhenAll(Unarchive(c, w.CaseId), Unarchive(c, w.CaseId));
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], unarchives.Select(r => r.StatusCode).Order().ToArray());
    }

    [Fact]
    public async Task A_close_racing_a_dossier_write_either_sees_it_as_stale_or_makes_the_write_fail_never_both_succeed()
    {
        for (var round = 0; round < 3; round++)
        {
            var (o, w, reviewId) = await Reviewed("AcceptedAsCompleteUnderPolicy", new Opt()); var c = o.Client;
            await ExternalDdsTestSupport.RecordOk(c, w.CaseId, reviewId, "DDS-RACE");
            var close = Close(c, w.CaseId, reviewId, "DDS-RACE"); var write = c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/recipients", Recipients("Raced GmbH"));
            var (closeResponse, writeResponse) = (await close, await write);
            var (closeStatus, writeStatus) = (closeResponse.StatusCode, writeResponse.StatusCode);
            Assert.True((closeStatus == HttpStatusCode.Created && writeStatus == HttpStatusCode.Conflict) || (closeStatus == HttpStatusCode.Conflict && writeStatus == HttpStatusCode.OK), $"close {closeStatus}, write {writeStatus}");
            if (closeStatus == HttpStatusCode.Created) Assert.Equal("Current", (await Completeness(c, w.CaseId)).GetProperty("reviewFreshness").GetString()); // the closure is backed by a Current review
            else { Assert.Equal(0, await Closures(o.Org, w.CaseId)); Assert.Equal("Stale", (await Completeness(c, w.CaseId)).GetProperty("reviewFreshness").GetString()); }
        }
    }

    // The sequence scripts/smoke.py follows: no operator profile, recipients, lot or evidence links - only the shipment, a master-data refresh and an outside-scope assessment.
    [Fact]
    public async Task The_minimal_smoke_sequence_closes_reopens_archives_and_unarchives_a_case()
    {
        var o = await NewOwner(); var c = o.Client; var w = await Build(c, o.Org, new Opt(Profile: false, SupplierFacts: false, Applicability: false, Recipients: false, Lot: false, Documents: false));
        await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/applicability", Application("OutsideRegulatoryScope", "Pending")));
        var review = await Review(c, w.CaseId, "ConfirmedOutsideRegulatoryScope");
        var closure = await Json(await Close(c, w.CaseId, review), HttpStatusCode.Created);
        Assert.Equal("OutsideRegulatoryScope", closure.GetProperty("outcome").GetString()); Assert.Equal("Closed", await StatusOf(c, w.CaseId));
        var reopening = await Json(await Reopen(c, w.CaseId, closure.GetProperty("closureId").GetGuid(), "Smoke test reopen."), HttpStatusCode.Created);
        var history = await Lifecycle(c, w.CaseId);
        Assert.Equal(JsonValueKind.Null, history.GetProperty("currentClosure").ValueKind);
        Assert.Equal(reopening.GetProperty("reopeningId").GetGuid(), history.GetProperty("closures")[0].GetProperty("reopening").GetProperty("reopeningId").GetGuid());
        Assert.Equal(HttpStatusCode.Created, (await Archive(c, w.CaseId, "Smoke test archive.")).StatusCode);
        Assert.NotEqual(JsonValueKind.Null, (await Details(c, w.CaseId)).GetProperty("archive").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await Unarchive(c, w.CaseId, "Smoke test unarchive.")).StatusCode);
        Assert.False((await Lifecycle(c, w.CaseId)).GetProperty("archived").GetBoolean());
    }

    // ---- OpenAPI ---------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_openapi_document_describes_the_manually_read_lifecycle_bodies()
    {
        using var anonymous = factory.CreateClient();
        var document = await Json(await anonymous.GetAsync("/openapi/v1.json"));
        JsonElement Body(string route) => document.GetProperty("paths").GetProperty($"/api/eudr-cases/{{caseId}}/{route}").GetProperty("post").GetProperty("requestBody");
        string[] Required(string route)
        {
            var schema = Body(route).GetProperty("content").GetProperty("application/json").GetProperty("schema");
            if (schema.TryGetProperty("$ref", out var reference)) schema = document.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/').Last());
            return schema.TryGetProperty("required", out var required) ? required.EnumerateArray().Select(x => x.GetString()!).Order().ToArray() : [];
        }
        Assert.Equal(["expectedReviewId"], Required("close"));          // externalDdsReference is an optional legacy value
        Assert.Equal(["expectedClosureId", "reason"], Required("reopen"));
        Assert.Equal(["reason"], Required("archive")); Assert.Equal(["reason"], Required("unarchive"));
        Assert.True(Body("close").GetProperty("required").GetBoolean());
        var lifecycle = document.GetProperty("paths").GetProperty("/api/eudr-cases/{caseId}/lifecycle");
        Assert.True(lifecycle.TryGetProperty("get", out _));
    }
}
