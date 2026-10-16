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
using static Provena.Api.IntegrationTests.RiskTestSupport;
namespace Provena.Api.IntegrationTests;

// Risk Assessment & Mitigation v1 over real HTTP and PostgreSQL: the Owner-only immutable assessment of one Owner review, member-recorded mitigation measures, the freshness derived from the
// review, the new close gate for ExternalDdsCompleted, the typed ReviewAlreadyBacksClosure conflict and the reopen path (new review -> new assessment -> new closure). Each scenario provisions
// its own organization through AuthService (the shared auth rate limiter is nearly consumed by other classes).
[Collection("Api")]
public sealed class RiskTests(ApiFactory factory)
{
    private const string Password = "Test-Only-Password-928!";
    private const string BackedMessage = "This review already backs a closure; record a new review before closing the case again.";
    private sealed record Owner(HttpClient Client, Guid Org, Guid UserId);

    // ---- fixtures -------------------------------------------------------------------------------------------------------------------------
    private async Task<Owner> NewOwner()
    {
        var name = "Risk-" + Guid.NewGuid().ToString("N")[..8];
        await using var scope = factory.Services.CreateAsyncScope();
        var token = await scope.ServiceProvider.GetRequiredService<AuthService>().Register(new RegisterRequest($"{name}@example.test", Password, "Test", "User", "Org " + name, null), CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Organizations.Where(x => x.Name == "Org " + name).Select(x => x.Id).SingleAsync();
        var user = await db.OrganizationUsers.Where(x => x.OrganizationId == org).Select(x => x.UserId).SingleAsync();
        return new(Client(token.AccessToken), org, user);
    }
    private HttpClient Client(string token) { var c = factory.CreateClient(); c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); return c; }
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
    private async Task Mutate(Guid org, Func<AppDbContext, Task> change) { await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org); await change(db); await db.SaveChangesAsync(); }
    private Task<int> Assessments(Guid org, Guid caseId) => Query(org, db => db.RiskAssessments.CountAsync(x => x.EudrCaseId == caseId));
    private Task<int> Measures(Guid org, Guid caseId) => Query(org, db => db.RiskMitigationMeasures.CountAsync(x => x.EudrCaseId == caseId));
    private Task<int> Closures(Guid org, Guid caseId) => Query(org, db => db.CaseClosures.CountAsync(x => x.EudrCaseId == caseId));
    private Task<int> Audits(Guid org, Guid caseId, string type) => Query(org, db => db.AuditEvents.CountAsync(x => x.EudrCaseId == caseId && x.EventType == type));

    private static readonly Opt Light = new(Recipients: false, Lot: false, Documents: false);
    private static Task<HttpResponseMessage> Close(HttpClient c, Guid caseId, Guid? reviewId, string? reference = null) => c.PostAsJsonAsync($"/api/eudr-cases/{caseId}/close", new { expectedReviewId = reviewId, externalDdsReference = reference });
    private static Task<HttpResponseMessage> Reopen(HttpClient c, Guid caseId, Guid closureId) => c.PostAsJsonAsync($"/api/eudr-cases/{caseId}/reopen", new { expectedClosureId = closureId, reason = "The data needs a correction." });
    private static async Task<JsonElement> Lifecycle(HttpClient c, Guid caseId) => await Json(await c.GetAsync($"/api/eudr-cases/{caseId}/lifecycle"), HttpStatusCode.OK);
    private static Guid? LatestId(JsonElement completeness) => completeness.GetProperty("latestReview").ValueKind == JsonValueKind.Null ? null : completeness.GetProperty("latestReview").GetProperty("reviewId").GetGuid();
    private static async Task<Guid> Review(HttpClient c, Guid caseId, string decision = "AcceptedAsCompleteUnderPolicy")
    {
        var state = await Completeness(c, caseId);
        var review = await Json(await c.PostAsJsonAsync($"/api/eudr-cases/{caseId}/reviews", new { decision, rationale = "Reviewed against the recorded facts.", expectedFingerprint = Fingerprint(state), expectedLatestReviewId = LatestId(state) }), HttpStatusCode.Created);
        return review.GetProperty("reviewId").GetGuid();
    }
    private static async Task<JsonElement> Problem(HttpResponseMessage response, HttpStatusCode status)
    { var json = await Json(response, status); Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType); return json; }
    private static async Task<Guid> UploadLate(HttpClient c, Guid caseId)
    { using var doc = Upload("late-" + Guid.NewGuid().ToString("N")[..6] + ".txt", "text/plain", "late"); return (await Json(await c.PostAsync($"/api/eudr-cases/{caseId}/documents", doc), HttpStatusCode.OK)).GetProperty("id").GetGuid(); }
    // A new current document puts the SupportingDocuments requirement back under review; a later Accepted review needs the Owner's decision on it again (as for the other evidence tests).
    private static async Task AcceptSubmitted(HttpClient c, Guid caseId)
    {
        foreach (var r in (await Json(await c.GetAsync($"/api/eudr-cases/{caseId}"))).GetProperty("requirements").EnumerateArray().Where(r => r.GetProperty("status").GetString() == "Submitted").ToList())
            Assert.Equal(HttpStatusCode.NoContent, (await Patch(c, $"/api/eudr-cases/{caseId}/requirements/{r.GetProperty("id").GetGuid()}", new { status = "Accepted", reviewerNote = "ok" })).StatusCode);
    }
    private static List<object> ConcernFindings(string note = "Cooperative mixes lots from several farms.")
        => Findings(new Dictionary<string, (string Finding, string? Note)> { ["SUPPLY_CHAIN_COMPLEXITY_AND_MIXING"] = ("Concern", note) });
    private static string[] Areas(JsonElement overview) => overview.GetProperty("changedAreas").EnumerateArray().Select(x => x.GetString()!).ToArray();

    private async Task<(Owner Owner, World World, Guid ReviewId)> Reviewed()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org);
        return (o, w, await Review(o.Client, w.CaseId));
    }

    // ---- authorization: Owner before case and body ------------------------------------------------------------------------------------------
    [Fact]
    public async Task Recording_an_assessment_is_owner_only_before_the_case_and_the_body_while_any_member_reads_and_records_mitigation()
    {
        var (o, w, review) = await Reviewed(); var a1 = await AssessOk(o.Client, w.CaseId, review, "NotNegligible", ConcernFindings()); var assessmentId = a1.GetProperty("assessmentId").GetGuid();
        var other = await NewOwner(); var foreign = await Build(other.Client, other.Org, Light);
        var member = await NewMember(o.Org); using var anonymous = factory.CreateClient();
        foreach (var (method, path) in new[] { (HttpMethod.Get, Url(w.CaseId, "risk")), (HttpMethod.Get, Url(w.CaseId, "risk-assessments/" + assessmentId)), (HttpMethod.Post, Url(w.CaseId)), (HttpMethod.Post, Url(w.CaseId, "risk-mitigations")) })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(new HttpRequestMessage(method, path))).StatusCode);

        // A genuine Member: 403 for an existing case, a nonexistent case, another organization's case and an invalid, malformed or missing body - never 404, 400 or 415.
        foreach (var caseId in new[] { w.CaseId, Guid.NewGuid(), foreign.CaseId })
            Assert.Equal(HttpStatusCode.Forbidden, (await Assess(member, caseId, AssessBody(review, expectedAssessment: assessmentId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Assess(member, w.CaseId, new { reviewId = Guid.Empty, rationale = "", findings = (object?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync(Url(w.CaseId), Raw("{ not json"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync(Url(w.CaseId), null)).StatusCode);
        // The Owner is told about the content type, the case and the body in the documented order.
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await o.Client.PostAsync(Url(w.CaseId), null)).StatusCode);
        await Problem(await Assess(o.Client, Guid.NewGuid(), AssessBody(review)), HttpStatusCode.NotFound);
        await Problem(await Assess(o.Client, foreign.CaseId, AssessBody(review)), HttpStatusCode.NotFound);
        Assert.Equal(1, await Assessments(o.Org, w.CaseId)); Assert.Equal(1, await Audits(o.Org, w.CaseId, "RiskAssessmentRecorded"));

        // A Member reads the history and records a mitigation measure; the Owner may as well.
        var overview = await Json(await member.GetAsync(Url(w.CaseId, "risk")), HttpStatusCode.OK); Assert.Equal(assessmentId, overview.GetProperty("latestAssessment").GetProperty("assessmentId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync(Url(w.CaseId, "risk-assessments/" + assessmentId))).StatusCode);
        await Problem(await member.GetAsync(Url(foreign.CaseId, "risk")), HttpStatusCode.NotFound);
        var m1 = await Json(await Mitigate(member, w.CaseId, MitigationBody(assessmentId)), HttpStatusCode.Created);
        Assert.Equal(1, m1.GetProperty("number").GetInt32());
        Assert.Equal(2, (await Json(await Mitigate(o.Client, w.CaseId, MitigationBody(assessmentId)), HttpStatusCode.Created)).GetProperty("number").GetInt32());
        await Problem(await Mitigate(member, foreign.CaseId, MitigationBody(assessmentId)), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_owner_check_is_live_a_demoted_owner_with_a_valid_token_is_denied_and_restored_again()
    {
        var (o, w, review) = await Reviewed();
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync($"""UPDATE "OrganizationUsers" SET "Role" = {(int)OrganizationRole.Member} WHERE "UserId" = {o.UserId}""");
        Assert.Equal(HttpStatusCode.Forbidden, (await Assess(o.Client, w.CaseId, AssessBody(review))).StatusCode); // the JWT still says Owner
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync($"""UPDATE "OrganizationUsers" SET "Role" = {(int)OrganizationRole.Owner} WHERE "UserId" = {o.UserId}""");
        Assert.Equal(HttpStatusCode.Created, (await Assess(o.Client, w.CaseId, AssessBody(review))).StatusCode);
    }

    // ---- read model and HTTP shape ----------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_overview_starts_empty_with_the_catalogue_and_responses_have_the_documented_shape_and_no_organization_id()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org);
        var empty = await Overview(o.Client, w.CaseId);
        Assert.Equal("None", empty.GetProperty("freshness").GetString()); Assert.Equal(JsonValueKind.Null, empty.GetProperty("staleReason").ValueKind); Assert.Equal(JsonValueKind.Null, empty.GetProperty("latestAssessment").ValueKind);
        Assert.Empty(empty.GetProperty("assessments").EnumerateArray()); Assert.Empty(empty.GetProperty("mitigations").EnumerateArray()); Assert.Empty(Areas(empty));
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("latestReviewId").ValueKind); Assert.False(empty.GetProperty("latestReviewBacksClosure").GetBoolean());
        var catalogue = empty.GetProperty("catalogue");
        Assert.Equal("provena-pilot-risk-criteria", catalogue.GetProperty("id").GetString()); Assert.Equal("1", catalogue.GetProperty("version").GetString()); Assert.Equal(7, catalogue.GetProperty("criteria").GetArrayLength());
        Assert.All(catalogue.GetProperty("criteria").EnumerateArray(), c => Assert.Equal(["code", "title", "guidance"], c.EnumerateObject().Select(p => p.Name).ToArray()));
        AssertNoOrganizationId(empty);

        var review = await Review(o.Client, w.CaseId);
        Assert.Equal(review, (await Overview(o.Client, w.CaseId)).GetProperty("latestReviewId").GetGuid()); Assert.Equal("None", (await Overview(o.Client, w.CaseId)).GetProperty("freshness").GetString());
        var response = await Assess(o.Client, w.CaseId, AssessBody(review, "NotNegligible", findings: ConcernFindings()));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var a = await Json(response); var id = a.GetProperty("assessmentId").GetGuid();
        Assert.EndsWith($"/api/eudr-cases/{w.CaseId}/risk-assessments/{id}", response.Headers.Location!.AbsolutePath);
        Assert.Equal(["assessmentId", "number", "reviewId", "reviewRevisionNumber", "criteriaCatalogueId", "criteriaCatalogueVersion", "conclusion", "rationale", "findings", "consideredMitigationThroughNumber", "assessedByUserId", "assessedAtUtc"],
            a.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(1, a.GetProperty("number").GetInt32()); Assert.Equal(review, a.GetProperty("reviewId").GetGuid()); Assert.Equal(1, a.GetProperty("reviewRevisionNumber").GetInt32());
        Assert.Equal("NotNegligible", a.GetProperty("conclusion").GetString()); Assert.Equal(o.UserId, a.GetProperty("assessedByUserId").GetGuid()); Assert.Equal(JsonValueKind.Null, a.GetProperty("consideredMitigationThroughNumber").ValueKind);
        Assert.Equal(7, a.GetProperty("findings").GetArrayLength());
        Assert.Equal("Concern", a.GetProperty("findings").EnumerateArray().Single(f => f.GetProperty("criterionCode").GetString() == "SUPPLY_CHAIN_COMPLEXITY_AND_MIXING").GetProperty("finding").GetString());
        AssertNoOrganizationId(a);
        Assert.Equal(a.GetRawText(), (await Json(await o.Client.GetAsync(Url(w.CaseId, "risk-assessments/" + id)), HttpStatusCode.OK)).GetRawText()); // the POST response equals what is read back
        await Problem(await o.Client.GetAsync(Url(w.CaseId, "risk-assessments/" + Guid.NewGuid())), HttpStatusCode.NotFound);

        var measureResponse = await Mitigate(o.Client, w.CaseId, MitigationBody(id));
        Assert.Equal(HttpStatusCode.Created, measureResponse.StatusCode); Assert.EndsWith($"/api/eudr-cases/{w.CaseId}/risk", measureResponse.Headers.Location!.AbsolutePath);
        var m = await Json(measureResponse);
        Assert.Equal(["mitigationId", "number", "riskAssessmentId", "description", "performedAtUtc", "evidenceDocumentId", "recordedByUserId", "recordedAtUtc"], m.EnumerateObject().Select(p => p.Name).ToArray());
        AssertNoOrganizationId(m); AssertNoOrganizationId(await Overview(o.Client, w.CaseId));

        // Append-only: no PUT, PATCH or DELETE exists on any risk route.
        foreach (var path in new[] { Url(w.CaseId, "risk"), Url(w.CaseId, "risk-assessments/" + id), Url(w.CaseId), Url(w.CaseId, "risk-mitigations") })
            foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
                Assert.Equal(HttpStatusCode.MethodNotAllowed, (await o.Client.SendAsync(new HttpRequestMessage(method, path))).StatusCode);
    }

    // ---- validation: finding rules (400) ---------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_finding_rules_reject_incomplete_sets_missing_notes_and_the_negligible_insufficient_information_contradiction_but_accept_concern_with_negligible()
    {
        var (o, w, review) = await Reviewed(); var c = o.Client;
        var all = Findings();
        var cases = new Dictionary<string, object>
        {
            ["no findings"] = new { reviewId = review, criteriaCatalogueVersion = "1", conclusion = "Negligible", rationale = Rationale, findings = (object?)null },
            ["empty findings"] = AssessBody(review, findings: []),
            ["a criterion missing"] = AssessBody(review, findings: all.Skip(1)),
            ["duplicate code"] = AssessBody(review, findings: all.Skip(1).Append(all[1])),
            ["unknown code"] = AssessBody(review, findings: all.Skip(1).Append(new { criterionCode = "NOT_A_CRITERION", finding = "NoConcern", note = (string?)null })),
            ["concern without note"] = AssessBody(review, "NotNegligible", findings: Findings(new Dictionary<string, (string Finding, string? Note)> { ["EVIDENCE_RELIABILITY"] = ("Concern", null) })),
            ["concern with blank note"] = AssessBody(review, "NotNegligible", findings: Findings(new Dictionary<string, (string Finding, string? Note)> { ["EVIDENCE_RELIABILITY"] = ("Concern", "   ") })),
            ["insufficient information without note"] = AssessBody(review, "NotNegligible", findings: Findings(new Dictionary<string, (string Finding, string? Note)> { ["EVIDENCE_RELIABILITY"] = ("InsufficientInformation", null) })),
            ["negligible with insufficient information"] = AssessBody(review, findings: Findings(new Dictionary<string, (string Finding, string? Note)> { ["EVIDENCE_RELIABILITY"] = ("InsufficientInformation", "No data for the area.") })),
            ["unknown finding value"] = AssessBody(review, findings: all.Skip(1).Append(new { criterionCode = "EVIDENCE_RELIABILITY", finding = "Maybe", note = (string?)null })),
            ["note too long"] = AssessBody(review, "NotNegligible", findings: Findings(new Dictionary<string, (string Finding, string? Note)> { ["EVIDENCE_RELIABILITY"] = ("Concern", new string('n', 2001)) })),
            ["blank rationale"] = AssessBody(review, rationale: "   "),
            ["rationale too long"] = AssessBody(review, rationale: new string('r', 4001)),
            ["no catalogue version"] = AssessBody(review, version: ""),
            ["no conclusion"] = new { reviewId = review, criteriaCatalogueVersion = "1", conclusion = (string?)null, rationale = Rationale, findings = all },
            ["unknown conclusion"] = AssessBody(review, "Probably"),
            ["empty review id"] = AssessBody(Guid.Empty),
            ["no review id"] = new { reviewId = (Guid?)null, criteriaCatalogueVersion = "1", conclusion = "Negligible", rationale = Rationale, findings = all },
        };
        foreach (var (name, body) in cases)
        {
            var response = await Assess(c, w.CaseId, body);
            if (name == "unknown finding value" || name == "unknown conclusion") Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); // an undefined enum is rejected by the JSON binder
            else await Problem(response, HttpStatusCode.BadRequest);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync(Url(w.CaseId), Raw("{ not json"))).StatusCode);
        Assert.Equal(0, await Assessments(o.Org, w.CaseId)); Assert.Equal(0, await Audits(o.Org, w.CaseId, "RiskAssessmentRecorded"));

        // Concern + Negligible is valid: a Concern finding is a human-observed indicator, not an automatic workflow requirement.
        var ok = await AssessOk(c, w.CaseId, review, "Negligible", ConcernFindings(), "Considered and judged negligible by the Owner.");
        Assert.Equal("Negligible", ok.GetProperty("conclusion").GetString()); Assert.Equal(JsonValueKind.Null, ok.GetProperty("consideredMitigationThroughNumber").ValueKind);
        Assert.Equal("Cooperative mixes lots from several farms.", ok.GetProperty("findings").EnumerateArray().Single(f => f.GetProperty("finding").GetString() == "Concern").GetProperty("note").GetString());
        // Surrounding whitespace is trimmed and a blank note is stored as no note.
        var trimmed = await AssessOk(c, w.CaseId, review, "NotNegligible", Findings(new Dictionary<string, (string Finding, string? Note)> { ["EVIDENCE_RELIABILITY"] = ("Concern", "  padded note  "), ["OTHER_RISK_INDICATORS"] = ("NoConcern", "   ") }), "  padded rationale  ");
        Assert.Equal("padded rationale", trimmed.GetProperty("rationale").GetString());
        Assert.Equal("padded note", trimmed.GetProperty("findings").EnumerateArray().Single(f => f.GetProperty("criterionCode").GetString() == "EVIDENCE_RELIABILITY").GetProperty("note").GetString());
        Assert.Equal(JsonValueKind.Null, trimmed.GetProperty("findings").EnumerateArray().Single(f => f.GetProperty("criterionCode").GetString() == "OTHER_RISK_INDICATORS").GetProperty("note").ValueKind);
        Assert.Equal(2, trimmed.GetProperty("number").GetInt32());
    }

    // ---- assessment preconditions (409) ------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Every_assessment_conflict_has_its_typed_reason_in_the_documented_order_and_a_refused_assessment_writes_nothing()
    {
        var (o, w, r1) = await Reviewed(); var c = o.Client;
        async Task<string> Refused(object body) => Reason(await Problem(await Assess(c, w.CaseId, body), HttpStatusCode.Conflict));
        var catalogue = await Problem(await Assess(c, w.CaseId, AssessBody(r1, version: "0")), HttpStatusCode.Conflict);
        Assert.Equal("RiskCriteriaCatalogueChanged", Reason(catalogue)); Assert.Equal("1", catalogue.GetProperty("errors").GetProperty("currentVersion").GetString());
        Assert.Equal("RiskCriteriaCatalogueChanged", await Refused(AssessBody(r1, version: "0", expectedAssessment: Guid.NewGuid(), expectedMitigation: Guid.NewGuid()))); // the catalogue is checked first
        Assert.Equal("RiskAssessmentHistoryChanged", await Refused(AssessBody(r1, expectedAssessment: Guid.NewGuid(), expectedMitigation: Guid.NewGuid()))); // the assessment history before the mitigation history
        Assert.Equal("MitigationHistoryChanged", await Refused(AssessBody(r1, expectedMitigation: Guid.NewGuid())));
        Assert.Equal("ReviewNotLatest", await Refused(AssessBody(Guid.NewGuid()))); // a nonexistent review answers like any other that is not the latest
        // A stale review: a new current document changes the Evidence section.
        await UploadLate(c, w.CaseId);
        var stale = await Problem(await Assess(c, w.CaseId, AssessBody(r1)), HttpStatusCode.Conflict);
        Assert.Equal("StaleReview", Reason(stale)); Assert.Contains("Evidence", stale.GetProperty("errors").GetProperty("changedAreas").EnumerateArray().Select(x => x.GetString()));
        // A review that did not accept the dossier as complete cannot be assessed, and an older review is no longer the latest.
        var returned = await Review(c, w.CaseId, "ReturnedForCorrection");
        Assert.Equal("ReviewNotAccepted", await Refused(AssessBody(returned)));
        Assert.Equal("ReviewNotLatest", await Refused(AssessBody(r1)));
        Assert.Equal(0, await Assessments(o.Org, w.CaseId)); Assert.Equal(0, await Audits(o.Org, w.CaseId, "RiskAssessmentRecorded"));
        // Every problem body carries the standard envelope and no organization id.
        AssertNoOrganizationId(catalogue); AssertNoOrganizationId(stale);
    }

    // ---- freshness: derived from the review, never a second concept --------------------------------------------------------------------------
    [Fact]
    public async Task Freshness_follows_the_latest_review_and_its_changed_areas_while_dds_records_and_closing_do_not_stale_an_assessment()
    {
        var (o, w, r1) = await Reviewed(); var c = o.Client;
        var none = await Overview(c, w.CaseId); Assert.Equal("None", none.GetProperty("freshness").GetString()); Assert.Equal(r1, none.GetProperty("latestReviewId").GetGuid());
        var a1 = await AssessNegligibleOk(c, w.CaseId, r1); var first = await Json(await c.GetAsync(Url(w.CaseId, "risk-assessments/" + a1)), HttpStatusCode.OK);
        var current = await Overview(c, w.CaseId); Assert.Equal("Current", current.GetProperty("freshness").GetString()); Assert.Equal(JsonValueKind.Null, current.GetProperty("staleReason").ValueKind); Assert.Empty(Areas(current));

        // Facts outside the fingerprint never stale it: the External DDS record and the closure.
        await ExternalDdsTestSupport.RecordOk(c, w.CaseId, r1, "DDS-1");
        Assert.Equal("Current", (await Overview(c, w.CaseId)).GetProperty("freshness").GetString());
        var closure = await Json(await Close(c, w.CaseId, r1), HttpStatusCode.Created);
        var closed = await Overview(c, w.CaseId); Assert.Equal("Current", closed.GetProperty("freshness").GetString()); Assert.True(closed.GetProperty("latestReviewBacksClosure").GetBoolean());
        Assert.Equal(HttpStatusCode.Created, (await Reopen(c, w.CaseId, closure.GetProperty("closureId").GetGuid())).StatusCode);
        Assert.Equal("Current", (await Overview(c, w.CaseId)).GetProperty("freshness").GetString()); // reopening alone changes nothing about the assessment

        // A fact change stales the review, and so the assessment, with the review's own changed areas.
        await UploadLate(c, w.CaseId);
        var staleByFact = await Overview(c, w.CaseId);
        Assert.Equal("Stale", staleByFact.GetProperty("freshness").GetString()); Assert.Equal("ReviewStale", staleByFact.GetProperty("staleReason").GetString()); Assert.Contains("Evidence", Areas(staleByFact));
        Assert.Equal(r1, staleByFact.GetProperty("latestReviewId").GetGuid());
        // A new review supersedes it: stale for another reason and with no changed areas of its own.
        await AcceptSubmitted(c, w.CaseId);
        var r2 = await Review(c, w.CaseId);
        var superseded = await Overview(c, w.CaseId);
        Assert.Equal("Stale", superseded.GetProperty("freshness").GetString()); Assert.Equal("ReviewSuperseded", superseded.GetProperty("staleReason").GetString()); Assert.Empty(Areas(superseded)); Assert.Equal(r2, superseded.GetProperty("latestReviewId").GetGuid());
        var a2 = await AssessNegligibleOk(c, w.CaseId, r2);
        Assert.Equal("Current", (await Overview(c, w.CaseId)).GetProperty("freshness").GetString());
        // Another fact change (recipients) stales the new one too.
        await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/recipients", new { resolution = "RecipientsIdentified", recipients = new[] { new { name = "Other GmbH", postalAddress = "Weg 2, Wien", email = "other@example.test" } } }));
        var staleAgain = await Overview(c, w.CaseId); Assert.Equal("ReviewStale", staleAgain.GetProperty("staleReason").GetString()); Assert.Contains("Recipients", Areas(staleAgain));
        // History: newest first, and the first assessment is unchanged by all of it.
        Assert.Equal([2, 1], staleAgain.GetProperty("assessments").EnumerateArray().Select(a => a.GetProperty("number").GetInt32()).ToArray());
        Assert.Equal(a2, staleAgain.GetProperty("latestAssessment").GetProperty("assessmentId").GetGuid());
        Assert.Equal(first.GetRawText(), (await Json(await c.GetAsync(Url(w.CaseId, "risk-assessments/" + a1)), HttpStatusCode.OK)).GetRawText());
    }

    // ---- ReviewAlreadyBacksClosure and the reopen path --------------------------------------------------------------------------------------
    [Fact]
    public async Task After_a_reopen_the_used_review_cannot_back_a_new_assessment_or_closure_and_the_path_is_new_review_new_assessment_new_closure()
    {
        var (o, w, r1) = await Reviewed(); var c = o.Client;
        var a1 = await AssessNegligibleOk(c, w.CaseId, r1); var a1Text = (await Json(await c.GetAsync(Url(w.CaseId, "risk-assessments/" + a1)), HttpStatusCode.OK)).GetRawText();
        await ExternalDdsTestSupport.RecordOk(c, w.CaseId, r1, "DDS-1");
        var closure1 = await Json(await Close(c, w.CaseId, r1), HttpStatusCode.Created);
        Assert.Equal(a1, closure1.GetProperty("riskAssessmentId").GetGuid()); // the closure pins the exact assessment
        Assert.Equal(HttpStatusCode.Created, (await Reopen(c, w.CaseId, closure1.GetProperty("closureId").GetGuid())).StatusCode);

        // The old review is still the latest and Current, yet it backs a closure already.
        Assert.Equal("Current", (await Completeness(c, w.CaseId)).GetProperty("reviewFreshness").GetString());
        var overview = await Overview(c, w.CaseId); Assert.True(overview.GetProperty("latestReviewBacksClosure").GetBoolean()); Assert.Equal("Current", overview.GetProperty("freshness").GetString());
        var assess = await Problem(await Assess(c, w.CaseId, AssessBody(r1, expectedAssessment: a1)), HttpStatusCode.Conflict);
        var close = await Problem(await Close(c, w.CaseId, r1), HttpStatusCode.Conflict);
        foreach (var problem in new[] { assess, close })
        {
            Assert.Equal("ReviewAlreadyBacksClosure", Reason(problem)); Assert.Equal(BackedMessage, problem.GetProperty("detail").GetString()); // typed, with the detail text unchanged
            AssertNoOrganizationId(problem);
        }
        Assert.Equal(1, await Assessments(o.Org, w.CaseId));

        // Required path: a new review, a new assessment on it, then the normal gates.
        await AcceptSubmitted(c, w.CaseId);
        var r2 = await Review(c, w.CaseId);
        Assert.Contains("Review history changed", (await Problem(await Close(c, w.CaseId, r1), HttpStatusCode.Conflict)).GetProperty("detail").GetString()); // r1 is no longer the latest review at all
        var superseded = await Overview(c, w.CaseId); Assert.Equal("ReviewSuperseded", superseded.GetProperty("staleReason").GetString()); Assert.False(superseded.GetProperty("latestReviewBacksClosure").GetBoolean());
        Assert.Equal("RiskAssessmentReviewMismatch", Reason(await Problem(await Close(c, w.CaseId, r2), HttpStatusCode.Conflict))); // the only assessment is on r1
        var a2 = await AssessNegligibleOk(c, w.CaseId, r2);
        Assert.Equal("ExternalDdsReviewMismatch", Reason(await Problem(await Close(c, w.CaseId, r2), HttpStatusCode.Conflict))); // the DDS record is still the one of r1
        await ExternalDdsTestSupport.RecordOk(c, w.CaseId, r2, "DDS-2");
        var closure2 = await Json(await Close(c, w.CaseId, r2), HttpStatusCode.Created);
        Assert.Equal(a2, closure2.GetProperty("riskAssessmentId").GetGuid()); Assert.Equal(2, closure2.GetProperty("number").GetInt32());

        // Both closures stay reconstructable with their own assessments; the first assessment is unchanged.
        var lifecycle = await Lifecycle(c, w.CaseId); var closures = lifecycle.GetProperty("closures").EnumerateArray().ToList();
        Assert.Equal(a1, closures[0].GetProperty("riskAssessmentId").GetGuid()); Assert.Equal(a2, closures[1].GetProperty("riskAssessmentId").GetGuid()); Assert.NotEqual(JsonValueKind.Null, closures[0].GetProperty("reopening").ValueKind);
        Assert.Equal(a1Text, (await Json(await c.GetAsync(Url(w.CaseId, "risk-assessments/" + a1)), HttpStatusCode.OK)).GetRawText());
        AssertNoOrganizationId(lifecycle);
    }

    // ---- mitigation and reassessment --------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_member_records_measures_for_the_latest_not_negligible_assessment_and_the_owner_reassesses_on_the_same_current_review()
    {
        var (o, w, r1) = await Reviewed(); var c = o.Client; var member = await NewMember(o.Org);
        var a1 = await AssessOk(c, w.CaseId, r1, "NotNegligible", ConcernFindings()); var a1Id = a1.GetProperty("assessmentId").GetGuid(); var a1Text = a1.GetRawText();
        // Body shape (400): the expected assessment, a nonblank description, a time that is not in the future, a non-empty evidence id.
        foreach (var bad in new object[]
        {
            new { expectedRiskAssessmentId = (Guid?)null, description = "d", performedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1), evidenceDocumentId = (Guid?)null },
            MitigationBody(Guid.Empty), MitigationBody(a1Id, description: ""), MitigationBody(a1Id, description: "   "), MitigationBody(a1Id, description: new string('d', 4001)),
            MitigationBody(a1Id, performedAt: DateTimeOffset.UtcNow.AddHours(1)), MitigationBody(a1Id, evidence: Guid.Empty),
            new { expectedRiskAssessmentId = a1Id, description = "d", performedAtUtc = (DateTimeOffset?)null, evidenceDocumentId = (Guid?)null },
        })
            await Problem(await Mitigate(member, w.CaseId, bad), HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.BadRequest, (await member.PostAsync(Url(w.CaseId, "risk-mitigations"), Raw("{ not json"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await member.PostAsync(Url(w.CaseId, "risk-mitigations"), null)).StatusCode);
        Assert.Equal("RiskAssessmentChanged", Reason(await Problem(await Mitigate(member, w.CaseId, MitigationBody(Guid.NewGuid())), HttpStatusCode.Conflict)));
        Assert.Equal(0, await Measures(o.Org, w.CaseId));

        // Mitigation that cites an already reviewed document does not stale the review.
        var m1 = await Json(await Mitigate(member, w.CaseId, MitigationBody(a1Id, "  Segregated the lot.  ", evidence: w.Docs[0])), HttpStatusCode.Created);
        Assert.Equal(1, m1.GetProperty("number").GetInt32()); Assert.Equal(a1Id, m1.GetProperty("riskAssessmentId").GetGuid()); Assert.Equal("Segregated the lot.", m1.GetProperty("description").GetString()); Assert.Equal(w.Docs[0], m1.GetProperty("evidenceDocumentId").GetGuid());
        var m2 = await Json(await Mitigate(c, w.CaseId, MitigationBody(a1Id)), HttpStatusCode.Created);
        Assert.Equal(2, m2.GetProperty("number").GetInt32()); Assert.Equal(JsonValueKind.Null, m2.GetProperty("evidenceDocumentId").ValueKind);
        var overview = await Overview(c, w.CaseId); Assert.Equal("Current", overview.GetProperty("freshness").GetString());
        Assert.Equal([2, 1], overview.GetProperty("mitigations").EnumerateArray().Select(m => m.GetProperty("number").GetInt32()).ToArray());

        // The Owner reassesses on the same still-Current review: a new immutable assessment that records which measures existed.
        var a2 = await AssessOk(c, w.CaseId, r1, "Negligible", ConcernFindings("Mitigated by the segregation audit."), "Judged negligible after the measures.");
        Assert.Equal(2, a2.GetProperty("number").GetInt32()); Assert.Equal(r1, a2.GetProperty("reviewId").GetGuid()); Assert.Equal(2, a2.GetProperty("consideredMitigationThroughNumber").GetInt32());
        Assert.Equal(a1Text, (await Json(await c.GetAsync(Url(w.CaseId, "risk-assessments/" + a1Id)), HttpStatusCode.OK)).GetRawText()); // the first assessment is unchanged
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(a1Text).RootElement.GetProperty("consideredMitigationThroughNumber").ValueKind);
        // The measure answered an assessment that is no longer the latest; a Negligible latest assessment expects no measure.
        Assert.Equal("RiskAssessmentChanged", Reason(await Problem(await Mitigate(member, w.CaseId, MitigationBody(a1Id)), HttpStatusCode.Conflict)));
        Assert.Equal("MitigationNotExpected", Reason(await Problem(await Mitigate(member, w.CaseId, MitigationBody(a2.GetProperty("assessmentId").GetGuid())), HttpStatusCode.Conflict)));
        Assert.Equal(2, await Measures(o.Org, w.CaseId));
    }

    [Fact]
    public async Task Mitigation_evidence_that_adds_a_current_document_stales_the_review_so_the_path_is_new_review_then_new_assessment()
    {
        var (o, w, r1) = await Reviewed(); var c = o.Client;
        var a1 = (await AssessOk(c, w.CaseId, r1, "NotNegligible", ConcernFindings())).GetProperty("assessmentId").GetGuid();
        var doc = await UploadLate(c, w.CaseId);
        var m1 = await Json(await Mitigate(c, w.CaseId, MitigationBody(a1, evidence: doc)), HttpStatusCode.Created); Assert.Equal(doc, m1.GetProperty("evidenceDocumentId").GetGuid());
        var stale = await Overview(c, w.CaseId); Assert.Equal("Stale", stale.GetProperty("freshness").GetString()); Assert.Equal("ReviewStale", stale.GetProperty("staleReason").GetString()); Assert.Contains("Evidence", Areas(stale));
        // The same review can no longer be assessed; a new review that pins the new document can.
        Assert.Equal("StaleReview", Reason(await Problem(await Assess(c, w.CaseId, AssessBody(r1, expectedAssessment: a1, expectedMitigation: m1.GetProperty("mitigationId").GetGuid())), HttpStatusCode.Conflict)));
        await AcceptSubmitted(c, w.CaseId);
        var r2 = await Review(c, w.CaseId);
        var a2 = await AssessOk(c, w.CaseId, r2, "Negligible");
        Assert.Equal(2, a2.GetProperty("number").GetInt32()); Assert.Equal(r2, a2.GetProperty("reviewId").GetGuid()); Assert.Equal(1, a2.GetProperty("consideredMitigationThroughNumber").GetInt32());
        Assert.Equal("Current", (await Overview(c, w.CaseId)).GetProperty("freshness").GetString());
    }

    [Fact]
    public async Task Evidence_must_be_a_current_document_of_the_same_case_when_linked_and_a_later_withdrawal_keeps_the_link()
    {
        var (o, w, r1) = await Reviewed(); var c = o.Client;
        var a1 = (await AssessOk(c, w.CaseId, r1, "NotNegligible", ConcernFindings())).GetProperty("assessmentId").GetGuid();
        var second = await Build(o.Client, o.Org); var other = await NewOwner(); var foreign = await Build(other.Client, other.Org);
        await Mutate(o.Org, async db => (await db.Documents.SingleAsync(x => x.Id == w.Docs[1])).WithdrawnAtUtc = DateTimeOffset.UtcNow);
        // Unknown, another case's, another organization's and a withdrawn document all answer identically.
        foreach (var evidence in new[] { Guid.NewGuid(), second.Docs[0], foreign.Docs[0], w.Docs[1] })
        {
            var problem = await Problem(await Mitigate(c, w.CaseId, MitigationBody(a1, evidence: evidence)), HttpStatusCode.Conflict);
            Assert.Equal("EvidenceDocumentNotCurrent", Reason(problem)); AssertNoOrganizationId(problem);
        }
        Assert.Equal(0, await Measures(o.Org, w.CaseId));
        var measure = await Json(await Mitigate(c, w.CaseId, MitigationBody(a1, evidence: w.Docs[0])), HttpStatusCode.Created);
        // The document is withdrawn later: the measure and its link stay readable and the document row is retained.
        await Mutate(o.Org, async db => (await db.Documents.SingleAsync(x => x.Id == w.Docs[0])).WithdrawnAtUtc = DateTimeOffset.UtcNow);
        var overview = await Overview(c, w.CaseId);
        Assert.Equal(w.Docs[0], overview.GetProperty("mitigations")[0].GetProperty("evidenceDocumentId").GetGuid()); Assert.Equal(measure.GetProperty("mitigationId").GetGuid(), overview.GetProperty("mitigations")[0].GetProperty("mitigationId").GetGuid());
        Assert.NotNull(await Query(o.Org, db => db.Documents.Where(x => x.Id == w.Docs[0]).Select(x => x.WithdrawnAtUtc).SingleAsync()));
        Assert.Equal(w.Docs[0], await Query(o.Org, db => db.RiskMitigationMeasures.Select(x => x.EvidenceDocumentId!.Value).SingleAsync()));
    }

    // ---- the close gate ------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_not_negligible_latest_assessment_blocks_the_external_dds_completed_closure_with_safe_metadata_and_a_negligible_one_on_the_latest_review_allows_it()
    {
        var (o, w, r1) = await Reviewed(); var c = o.Client; const string Verification = "VERIF-FIXTURE-RISK-1";
        var a1 = await AssessOk(c, w.CaseId, r1, "NotNegligible", ConcernFindings("NOTE-MARKER-Q7"), "RATIONALE-MARKER-Q7");
        // The External DDS record has no risk gate: it is recorded although the latest assessment is NotNegligible.
        await ExternalDdsTestSupport.RecordOk(c, w.CaseId, r1, "DDS-1", Verification);
        var blocked = await Problem(await Close(c, w.CaseId, r1), HttpStatusCode.Conflict);
        Assert.Equal("RiskNotNegligible", Reason(blocked));
        var summary = blocked.GetProperty("errors").GetProperty("riskAssessment");
        Assert.Equal(["assessmentId", "number", "reviewId", "conclusion"], summary.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(a1.GetProperty("assessmentId").GetGuid(), summary.GetProperty("assessmentId").GetGuid()); Assert.Equal(1, summary.GetProperty("number").GetInt32()); Assert.Equal(r1, summary.GetProperty("reviewId").GetGuid()); Assert.Equal("NotNegligible", summary.GetProperty("conclusion").GetString());
        foreach (var secret in new[] { "NOTE-MARKER-Q7", "RATIONALE-MARKER-Q7", Verification, "DDS-1" }) Assert.DoesNotContain(secret, blocked.GetRawText());
        AssertNoOrganizationId(blocked);
        Assert.Equal(0, await Closures(o.Org, w.CaseId)); Assert.Equal(0, await Audits(o.Org, w.CaseId, "CaseClosed")); Assert.NotEqual("Closed", (await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}"))).GetProperty("status").GetString());

        // A later Negligible assessment of the same review allows the closure, which pins exactly that assessment.
        var a2 = await AssessNegligibleOk(c, w.CaseId, r1);
        var closure = await Json(await Close(c, w.CaseId, r1), HttpStatusCode.Created);
        Assert.Equal("ExternalDdsCompleted", closure.GetProperty("outcome").GetString()); Assert.Equal(a2, closure.GetProperty("riskAssessmentId").GetGuid()); Assert.Equal("DDS-1", closure.GetProperty("externalDdsReference").GetString());
        Assert.Equal(a2, (await Lifecycle(c, w.CaseId)).GetProperty("currentClosure").GetProperty("riskAssessmentId").GetGuid());
        Assert.Equal(a2, await Query(o.Org, db => db.CaseClosures.Where(x => x.EudrCaseId == w.CaseId).Select(x => x.RiskAssessmentId!.Value).SingleAsync()));
        var audit = await Query(o.Org, db => db.AuditEvents.Where(x => x.EventType == "CaseClosed" && x.EudrCaseId == w.CaseId).Select(x => x.Description).SingleAsync());
        Assert.Contains(a2.ToString(), audit); foreach (var secret in new[] { "NOTE-MARKER-Q7", "RATIONALE-MARKER-Q7", Verification, "DDS-1" }) Assert.DoesNotContain(secret, audit);
    }

    [Fact]
    public async Task The_close_precedence_is_stale_review_then_risk_then_the_external_dds_gate()
    {
        var (o, w, r1) = await Reviewed(); var c = o.Client;
        Assert.Equal("RiskAssessmentMissing", Reason(await Problem(await Close(c, w.CaseId, r1), HttpStatusCode.Conflict))); // no assessment, no DDS record: risk comes first
        var missing = await Problem(await Close(c, w.CaseId, r1), HttpStatusCode.Conflict); Assert.Equal(JsonValueKind.Null, missing.GetProperty("errors").GetProperty("riskAssessment").ValueKind);
        await UploadLate(c, w.CaseId);
        Assert.Equal("StaleReview", Reason(await Problem(await Close(c, w.CaseId, r1), HttpStatusCode.Conflict))); // a stale review wins over the missing assessment
        await AcceptSubmitted(c, w.CaseId);
        var r2 = await Review(c, w.CaseId);
        await AssessNegligibleOk(c, w.CaseId, r2);
        Assert.Equal("ExternalDdsRecordMissing", Reason(await Problem(await Close(c, w.CaseId, r2), HttpStatusCode.Conflict))); // with the risk gate satisfied the DDS gate speaks
        await ExternalDdsTestSupport.RecordOk(c, w.CaseId, r2, "DDS-3");
        Assert.Equal(HttpStatusCode.Created, (await Close(c, w.CaseId, r2)).StatusCode);
    }

    [Fact]
    public async Task The_external_dds_record_needs_no_risk_assessment_and_the_closure_has_no_chronology_rule_against_it()
    {
        var (o, w, r1) = await Reviewed(); var c = o.Client;
        var dds = await ExternalDdsTestSupport.RecordOk(c, w.CaseId, r1, "DDS-EARLY"); // recorded first: an external fact that happened is recorded whatever the internal order
        Assert.Equal("None", (await Overview(c, w.CaseId)).GetProperty("freshness").GetString());
        var assessment = await AssessNegligibleOk(c, w.CaseId, r1); // its server time is later than the submission
        var submitted = await Query(o.Org, db => db.ExternalDdsRecords.Where(x => x.Id == dds).Select(x => x.SubmittedAtUtc).SingleAsync());
        var assessed = await Query(o.Org, db => db.RiskAssessments.Where(x => x.Id == assessment).Select(x => x.AssessedAtUtc).SingleAsync());
        Assert.True(submitted < assessed);
        var closed = await Close(c, w.CaseId, r1);
        Assert.Equal(HttpStatusCode.Created, closed.StatusCode); Assert.Equal(assessment, (await Json(closed)).GetProperty("riskAssessmentId").GetGuid());
    }

    [Fact]
    public async Task The_other_closure_outcomes_need_no_risk_assessment_and_a_review_that_did_not_accept_the_dossier_cannot_be_assessed()
    {
        foreach (var (decision, application) in new[] { ("ConfirmedOutsideRegulatoryScope", Application("OutsideRegulatoryScope", "Pending")), ("ConfirmedUnsupportedFlow", Application(activity: "Export")) })
        {
            var o = await NewOwner(); var w = await Build(o.Client, o.Org, Light);
            await Ok(o.Client.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/applicability", application));
            var review = await Review(o.Client, w.CaseId, decision);
            Assert.Equal("ReviewNotAccepted", Reason(await Problem(await Assess(o.Client, w.CaseId, AssessBody(review)), HttpStatusCode.Conflict)));
            var closure = await Json(await Close(o.Client, w.CaseId, review), HttpStatusCode.Created);
            Assert.Equal(JsonValueKind.Null, closure.GetProperty("riskAssessmentId").ValueKind); Assert.Equal(0, await Assessments(o.Org, w.CaseId));
        }
    }

    // ---- terminal cases ------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_closed_or_archived_case_keeps_the_risk_history_readable_but_accepts_no_assessment_or_mitigation()
    {
        var (o, w, r1) = await Reviewed(); var c = o.Client;
        var a1 = await AssessNegligibleOk(c, w.CaseId, r1); await ExternalDdsTestSupport.RecordOk(c, w.CaseId, r1, "DDS-1");
        Assert.Equal(HttpStatusCode.Created, (await Close(c, w.CaseId, r1)).StatusCode);
        Assert.Contains("Closed", (await Problem(await Assess(c, w.CaseId, AssessBody(r1, expectedAssessment: a1)), HttpStatusCode.Conflict)).GetProperty("detail").GetString());
        Assert.Contains("Closed", (await Problem(await Mitigate(c, w.CaseId, MitigationBody(a1)), HttpStatusCode.Conflict)).GetProperty("detail").GetString());
        Assert.Equal(a1, (await Overview(c, w.CaseId)).GetProperty("latestAssessment").GetProperty("assessmentId").GetGuid());
        Assert.Equal(1, await Assessments(o.Org, w.CaseId)); Assert.Equal(0, await Measures(o.Org, w.CaseId));

        var (o2, w2, r2) = await Reviewed(); var a2 = (await AssessOk(o2.Client, w2.CaseId, r2, "NotNegligible", ConcernFindings())).GetProperty("assessmentId").GetGuid();
        await LifecycleTestSupport.Archive(factory.Services, w2.CaseId);
        Assert.Contains("Archived", (await Problem(await Assess(o2.Client, w2.CaseId, AssessBody(r2, expectedAssessment: a2)), HttpStatusCode.Conflict)).GetProperty("detail").GetString());
        Assert.Contains("Archived", (await Problem(await Mitigate(o2.Client, w2.CaseId, MitigationBody(a2)), HttpStatusCode.Conflict)).GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.OK, (await o2.Client.GetAsync(Url(w2.CaseId, "risk"))).StatusCode);
        Assert.Equal(1, await Assessments(o2.Org, w2.CaseId)); Assert.Equal(0, await Measures(o2.Org, w2.CaseId));
    }

    // ---- concurrency (the case lock) --------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Two_simultaneous_assessments_serialize_one_wins_and_the_other_sees_the_history_changed()
    {
        var (o, w, r1) = await Reviewed(); var c = o.Client;
        var results = await Task.WhenAll(Assess(c, w.CaseId, AssessBody(r1, "Negligible")), Assess(c, w.CaseId, AssessBody(r1, "NotNegligible", findings: ConcernFindings())));
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], results.Select(r => r.StatusCode).Order().ToArray());
        Assert.Equal("RiskAssessmentHistoryChanged", Reason(await Json(results.Single(r => r.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict)));
        Assert.Equal(1, await Assessments(o.Org, w.CaseId)); Assert.Equal(1, await Audits(o.Org, w.CaseId, "RiskAssessmentRecorded"));
    }

    [Fact]
    public async Task A_mitigation_racing_an_assessment_serializes_and_the_loser_sees_the_change()
    {
        var (o, w, r1) = await Reviewed(); var c = o.Client;
        var a1 = (await AssessOk(c, w.CaseId, r1, "NotNegligible", ConcernFindings())).GetProperty("assessmentId").GetGuid();
        var assess = Assess(c, w.CaseId, AssessBody(r1, "Negligible", a1, null)); var mitigate = Mitigate(c, w.CaseId, MitigationBody(a1));
        var results = await Task.WhenAll(assess, mitigate);
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], results.Select(r => r.StatusCode).Order().ToArray());
        // If the measure committed first, the assessment (which had not seen it) is refused; if the assessment committed first, the measure answers a superseded assessment.
        if (results[1].StatusCode == HttpStatusCode.Created) { Assert.Equal("MitigationHistoryChanged", Reason(await Json(results[0], HttpStatusCode.Conflict))); Assert.Equal(1, await Assessments(o.Org, w.CaseId)); Assert.Equal(1, await Measures(o.Org, w.CaseId)); }
        else { Assert.Equal("RiskAssessmentChanged", Reason(await Json(results[1], HttpStatusCode.Conflict))); Assert.Equal(2, await Assessments(o.Org, w.CaseId)); Assert.Equal(0, await Measures(o.Org, w.CaseId)); }
    }

    [Fact]
    public async Task A_closure_racing_a_new_not_negligible_assessment_never_closes_on_an_assessment_other_than_the_latest()
    {
        var (o, w, r1) = await Reviewed(); var c = o.Client;
        var a1 = await AssessNegligibleOk(c, w.CaseId, r1); await ExternalDdsTestSupport.RecordOk(c, w.CaseId, r1, "DDS-RACE");
        var assess = Assess(c, w.CaseId, AssessBody(r1, "NotNegligible", a1, null, findings: ConcernFindings())); var close = Close(c, w.CaseId, r1);
        var results = await Task.WhenAll(assess, close);
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], results.Select(r => r.StatusCode).Order().ToArray());
        if (results[1].StatusCode == HttpStatusCode.Created)
        {
            Assert.Equal(a1, await Query(o.Org, db => db.CaseClosures.Where(x => x.EudrCaseId == w.CaseId).Select(x => x.RiskAssessmentId!.Value).SingleAsync())); // closed on the latest assessment, which the late one could not replace
            Assert.Equal(1, await Assessments(o.Org, w.CaseId));
        }
        else
        {
            Assert.Equal("RiskNotNegligible", Reason(await Json(results[1], HttpStatusCode.Conflict))); Assert.Equal(0, await Closures(o.Org, w.CaseId)); Assert.Equal(2, await Assessments(o.Org, w.CaseId));
        }
    }

    // ---- audit safety ---------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Audit_events_carry_ids_numbers_the_catalogue_version_and_the_conclusion_but_never_free_text_or_document_names()
    {
        var (o, w, r1) = await Reviewed(); var c = o.Client;
        var a1 = await AssessOk(c, w.CaseId, r1, "NotNegligible", ConcernFindings("NOTE-MARKER-9F3"), "RATIONALE-MARKER-9F3"); var a1Id = a1.GetProperty("assessmentId").GetGuid();
        var m1 = await Json(await Mitigate(c, w.CaseId, MitigationBody(a1Id, "DESC-MARKER-9F3", evidence: w.Docs[0])), HttpStatusCode.Created); var m1Id = m1.GetProperty("mitigationId").GetGuid();
        var assessmentAudit = await Query(o.Org, db => db.AuditEvents.Where(x => x.EudrCaseId == w.CaseId && x.EventType == "RiskAssessmentRecorded").Select(x => x.Description).SingleAsync());
        var mitigationAudit = await Query(o.Org, db => db.AuditEvents.Where(x => x.EudrCaseId == w.CaseId && x.EventType == "RiskMitigationRecorded").Select(x => x.Description).SingleAsync());
        Assert.Contains(a1Id.ToString(), assessmentAudit); Assert.Contains(r1.ToString(), assessmentAudit); Assert.Contains("NotNegligible", assessmentAudit); Assert.Contains("version 1", assessmentAudit); Assert.Contains("number 1", assessmentAudit);
        Assert.Contains(m1Id.ToString(), mitigationAudit); Assert.Contains(a1Id.ToString(), mitigationAudit);
        var rows = await Query(o.Org, db => db.AuditEvents.Where(x => x.EudrCaseId == w.CaseId).Select(x => new { x.Description, x.MetadataJson }).ToListAsync());
        var everything = string.Join('\n', rows.Select(r => r.Description + " " + r.MetadataJson)) + (await Details(c, w.CaseId)).GetProperty("auditEvents").GetRawText();
        foreach (var secret in new[] { "NOTE-MARKER-9F3", "RATIONALE-MARKER-9F3", "DESC-MARKER-9F3", "evidence0.txt" }) Assert.DoesNotContain(secret, everything);
        Assert.Equal(1, await Audits(o.Org, w.CaseId, "RiskAssessmentRecorded")); Assert.Equal(1, await Audits(o.Org, w.CaseId, "RiskMitigationRecorded"));
    }
    private static async Task<JsonElement> Details(HttpClient c, Guid caseId) => await Json(await c.GetAsync($"/api/eudr-cases/{caseId}"));

    // ---- tenant isolation -------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Risk_records_are_isolated_by_organization_and_by_case()
    {
        var (o, w, r1) = await Reviewed(); var a1 = await AssessNegligibleOk(o.Client, w.CaseId, r1);
        var other = await NewOwner(); var foreign = await Build(other.Client, other.Org, Light);
        // Another organization sees nothing of this case, and cannot write to it.
        await Problem(await other.Client.GetAsync(Url(w.CaseId, "risk")), HttpStatusCode.NotFound);
        await Problem(await other.Client.GetAsync(Url(w.CaseId, "risk-assessments/" + a1)), HttpStatusCode.NotFound);
        await Problem(await Assess(other.Client, w.CaseId, AssessBody(r1)), HttpStatusCode.NotFound);
        await Problem(await Mitigate(other.Client, w.CaseId, MitigationBody(a1)), HttpStatusCode.NotFound);
        // The same assessment id is not readable through another case, of the same or of another organization.
        await Problem(await other.Client.GetAsync(Url(foreign.CaseId, "risk-assessments/" + a1)), HttpStatusCode.NotFound);
        var second = await Build(o.Client, o.Org, Light);
        await Problem(await o.Client.GetAsync(Url(second.CaseId, "risk-assessments/" + a1)), HttpStatusCode.NotFound);
        Assert.Empty((await Overview(o.Client, second.CaseId)).GetProperty("assessments").EnumerateArray());
        // A review of another case is not a review of this one.
        Assert.Equal("ReviewNotLatest", Reason(await Problem(await Assess(o.Client, second.CaseId, AssessBody(r1)), HttpStatusCode.Conflict)));
        Assert.Equal(0, await Assessments(o.Org, second.CaseId)); Assert.Equal(0, await Assessments(other.Org, foreign.CaseId));
    }

    // ---- working export ---------------------------------------------------------------------------------------------------------------------
    private static async Task<Dictionary<string, string>> ReadZip(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
        var files = new Dictionary<string, string>();
        foreach (var entry in zip.Entries) { using var reader = new StreamReader(entry.Open()); files[entry.FullName] = await reader.ReadToEndAsync(); }
        return files;
    }
    private static async Task<JsonElement> WorkingManifest(HttpClient c, Guid caseId)
    {
        var files = await ReadZip(await c.GetAsync($"/api/eudr-cases/{caseId}/export"));
        return JsonDocument.Parse(files.Single(f => f.Key.EndsWith("/manifest.json")).Value.TrimStart('﻿')).RootElement.Clone();
    }
    private static string Compact(JsonElement element) => JsonSerializer.Serialize(element);

    [Fact]
    public async Task The_working_export_carries_the_complete_risk_history_with_full_text_findings_evidence_and_the_closure_reference()
    {
        var (o, w, r1) = await Reviewed(); var c = o.Client; var member = await NewMember(o.Org);
        var a1 = await AssessOk(c, w.CaseId, r1, "NotNegligible", ConcernFindings("NOTE-EXPORT-1"), "RATIONALE-EXPORT-1"); var a1Id = a1.GetProperty("assessmentId").GetGuid();
        var m1 = await Json(await Mitigate(member, w.CaseId, MitigationBody(a1Id, "DESC-EXPORT-1", evidence: w.Docs[0])), HttpStatusCode.Created);
        var m2 = await Json(await Mitigate(c, w.CaseId, MitigationBody(a1Id, "DESC-EXPORT-2")), HttpStatusCode.Created);
        var a2 = await AssessOk(c, w.CaseId, r1, "Negligible", ConcernFindings("NOTE-EXPORT-2"), "RATIONALE-EXPORT-2"); var a2Id = a2.GetProperty("assessmentId").GetGuid();
        await ExternalDdsTestSupport.RecordOk(c, w.CaseId, r1, "DDS-EXPORT");
        var closure = await Json(await Close(c, w.CaseId, r1), HttpStatusCode.Created);

        var manifest = await WorkingManifest(c, w.CaseId); var risk = manifest.GetProperty("risk");
        Assert.Equal(["freshness", "staleReason", "changedAreas", "latestReviewId", "latestReviewBacksClosure", "latestAssessmentId", "assessments", "mitigations"], risk.EnumerateObject().Select(p => p.Name).ToArray());
        // All assessments, oldest first, each exactly as the API returns it (full rationale, every finding with its note, the provenance number).
        var assessments = risk.GetProperty("assessments").EnumerateArray().ToList();
        Assert.Equal([1, 2], assessments.Select(a => a.GetProperty("number").GetInt32()).ToArray());
        Assert.Equal(Compact(a1), Compact(assessments[0])); Assert.Equal(Compact(a2), Compact(assessments[1]));
        Assert.Equal("RATIONALE-EXPORT-1", assessments[0].GetProperty("rationale").GetString()); Assert.Equal(r1, assessments[0].GetProperty("reviewId").GetGuid());
        Assert.Equal(7, assessments[1].GetProperty("findings").GetArrayLength());
        Assert.Equal("NOTE-EXPORT-2", assessments[1].GetProperty("findings").EnumerateArray().Single(f => f.GetProperty("finding").GetString() == "Concern").GetProperty("note").GetString());
        Assert.Equal(JsonValueKind.Null, assessments[0].GetProperty("consideredMitigationThroughNumber").ValueKind); Assert.Equal(2, assessments[1].GetProperty("consideredMitigationThroughNumber").GetInt32());
        Assert.Equal("provena-pilot-risk-criteria", assessments[0].GetProperty("criteriaCatalogueId").GetString()); Assert.Equal("1", assessments[0].GetProperty("criteriaCatalogueVersion").GetString());
        Assert.Equal(o.UserId, assessments[1].GetProperty("assessedByUserId").GetGuid()); Assert.Equal(a2.GetProperty("assessedAtUtc").GetDateTimeOffset(), assessments[1].GetProperty("assessedAtUtc").GetDateTimeOffset());
        // All measures, oldest first, with their evidence ids, actor and times.
        var measures = risk.GetProperty("mitigations").EnumerateArray().ToList();
        Assert.Equal([1, 2], measures.Select(m => m.GetProperty("number").GetInt32()).ToArray());
        Assert.Equal(m1.GetProperty("mitigationId").GetGuid(), measures[0].GetProperty("mitigationId").GetGuid()); Assert.Equal(m2.GetProperty("mitigationId").GetGuid(), measures[1].GetProperty("mitigationId").GetGuid());
        Assert.Equal(w.Docs[0], measures[0].GetProperty("evidenceDocumentId").GetGuid()); Assert.Equal(JsonValueKind.Null, measures[1].GetProperty("evidenceDocumentId").ValueKind);
        Assert.Equal(a1Id, measures[0].GetProperty("riskAssessmentId").GetGuid()); Assert.Equal("DESC-EXPORT-1", measures[0].GetProperty("description").GetString());
        Assert.NotEqual(Guid.Empty, measures[0].GetProperty("recordedByUserId").GetGuid()); Assert.Equal(m1.GetProperty("performedAtUtc").GetDateTimeOffset(), measures[0].GetProperty("performedAtUtc").GetDateTimeOffset(), TimeSpan.FromMilliseconds(1)); // PostgreSQL stores microseconds
        // The closure's riskAssessmentId resolves to an exported assessment, with no further API call.
        var closureRisk = manifest.GetProperty("lifecycle").GetProperty("closures")[0].GetProperty("riskAssessmentId").GetGuid();
        Assert.Equal(a2Id, closureRisk); Assert.Equal(closure.GetProperty("riskAssessmentId").GetGuid(), closureRisk);
        Assert.Contains(closureRisk, assessments.Select(a => a.GetProperty("assessmentId").GetGuid()));
        Assert.Equal(a2Id, risk.GetProperty("latestAssessmentId").GetGuid()); Assert.Equal("Current", risk.GetProperty("freshness").GetString()); Assert.Equal(r1, risk.GetProperty("latestReviewId").GetGuid());
        Assert.True(risk.GetProperty("latestReviewBacksClosure").GetBoolean());
        Assert.Contains("Not part of the reviewed export", manifest.GetProperty("riskScope").GetString());
        AssertNoOrganizationId(risk);
        // The free text lives in the retained export only, never in the generic audit text.
        var audit = string.Join('\n', await Query(o.Org, db => db.AuditEvents.Where(x => x.EudrCaseId == w.CaseId).Select(x => x.Description).ToListAsync()));
        foreach (var text in new[] { "RATIONALE-EXPORT-1", "NOTE-EXPORT-1", "DESC-EXPORT-1", "DESC-EXPORT-2" }) Assert.DoesNotContain(text, audit);
        // GET risk and the export agree.
        var api = await Overview(c, w.CaseId);
        Assert.Equal(api.GetProperty("freshness").GetString(), risk.GetProperty("freshness").GetString());
        Assert.Equal(api.GetProperty("assessments").EnumerateArray().Reverse().Select(Compact).ToArray(), assessments.Select(Compact).ToArray());
        Assert.Equal(api.GetProperty("mitigations").EnumerateArray().Reverse().Select(Compact).ToArray(), measures.Select(Compact).ToArray()); // persisted values, including recordedAtUtc
    }

    [Fact]
    public async Task The_working_export_reports_risk_freshness_with_the_same_reason_and_changed_areas_as_get_risk()
    {
        var (o, w, r1) = await Reviewed(); var c = o.Client;
        var none = (await WorkingManifest(c, w.CaseId)).GetProperty("risk");
        Assert.Equal("None", none.GetProperty("freshness").GetString()); Assert.Equal(JsonValueKind.Null, none.GetProperty("staleReason").ValueKind); Assert.Empty(none.GetProperty("changedAreas").EnumerateArray()); Assert.Equal(JsonValueKind.Null, none.GetProperty("latestAssessmentId").ValueKind);
        await AssessNegligibleOk(c, w.CaseId, r1);
        Assert.Equal("Current", (await WorkingManifest(c, w.CaseId)).GetProperty("risk").GetProperty("freshness").GetString());

        await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/recipients", new { resolution = "RecipientsIdentified", recipients = new[] { new { name = "Other GmbH", postalAddress = "Weg 2, Wien", email = "other@example.test" } } }));
        var stale = (await WorkingManifest(c, w.CaseId)).GetProperty("risk"); var api = await Overview(c, w.CaseId);
        Assert.Equal("Stale", stale.GetProperty("freshness").GetString()); Assert.Equal("ReviewStale", stale.GetProperty("staleReason").GetString()); Assert.Contains("Recipients", Areas(stale));
        Assert.Equal(Areas(api), Areas(stale)); Assert.Equal(api.GetProperty("staleReason").GetString(), stale.GetProperty("staleReason").GetString());

        await Review(c, w.CaseId);
        var superseded = (await WorkingManifest(c, w.CaseId)).GetProperty("risk");
        Assert.Equal("Stale", superseded.GetProperty("freshness").GetString()); Assert.Equal("ReviewSuperseded", superseded.GetProperty("staleReason").GetString()); Assert.Empty(Areas(superseded));
    }

    [Fact]
    public async Task A_case_with_no_risk_records_still_exports_with_an_empty_risk_section()
    {
        var o = await NewOwner();
        foreach (var options in new[] { new Opt(), Light })
        {
            var w = await Build(o.Client, o.Org, options);
            var manifest = await WorkingManifest(o.Client, w.CaseId); var risk = manifest.GetProperty("risk");
            Assert.Equal("None", risk.GetProperty("freshness").GetString()); Assert.Empty(risk.GetProperty("assessments").EnumerateArray()); Assert.Empty(risk.GetProperty("mitigations").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, risk.GetProperty("latestReviewId").ValueKind); Assert.False(risk.GetProperty("latestReviewBacksClosure").GetBoolean());
            Assert.Equal(JsonValueKind.Null, manifest.GetProperty("lifecycle").GetProperty("currentClosure").ValueKind);
        }
        // A legacy-shaped closure (no risk assessment) also exports, with a null reference.
        var o2 = await NewOwner();
        var w2 = await Build(o2.Client, o2.Org, Light);
        await Ok(o2.Client.PutAsJsonAsync($"/api/eudr-cases/{w2.CaseId}/applicability", Application("OutsideRegulatoryScope", "Pending")));
        var review = await Review(o2.Client, w2.CaseId, "ConfirmedOutsideRegulatoryScope");
        Assert.Equal(HttpStatusCode.Created, (await Close(o2.Client, w2.CaseId, review)).StatusCode);
        var closed = await WorkingManifest(o2.Client, w2.CaseId);
        Assert.Equal(JsonValueKind.Null, closed.GetProperty("lifecycle").GetProperty("closures")[0].GetProperty("riskAssessmentId").ValueKind); Assert.Empty(closed.GetProperty("risk").GetProperty("assessments").EnumerateArray());
    }

    [Fact]
    public async Task The_reviewed_export_is_unchanged_by_risk_records_and_carries_no_risk_data()
    {
        var (o, w, r1) = await Reviewed(); var c = o.Client;
        string Url_() => $"/api/eudr-cases/{w.CaseId}/reviews/{r1}/export";
        // review.json differs between two calls only by exportedAtUtc: normalise it away, keep every other byte of every entry.
        static string Normalise(string name, string text)
        {
            if (!name.EndsWith("/review.json")) return text;
            var node = System.Text.Json.Nodes.JsonNode.Parse(text.TrimStart('﻿'))!.AsObject(); node.Remove("exportedAtUtc");
            return node.ToJsonString();
        }
        async Task<(Dictionary<string, string> Files, string[] ReviewProperties)> Reviewed_() { var files = await ReadZip(await c.GetAsync(Url_())); var review = files.Single(f => f.Key.EndsWith("/review.json")); return (files.ToDictionary(f => f.Key, f => Normalise(f.Key, f.Value)), JsonDocument.Parse(review.Value.TrimStart('﻿')).RootElement.EnumerateObject().Select(p => p.Name).ToArray()); }
        var before = await Reviewed_();

        var a1 = await AssessOk(c, w.CaseId, r1, "NotNegligible", ConcernFindings("NOTE-REVIEWED-1"), "RATIONALE-REVIEWED-1"); var a1Id = a1.GetProperty("assessmentId").GetGuid();
        await Json(await Mitigate(c, w.CaseId, MitigationBody(a1Id, "DESC-REVIEWED-1", evidence: w.Docs[0])), HttpStatusCode.Created);
        await AssessNegligibleOk(c, w.CaseId, r1);
        var after = await Reviewed_();

        Assert.Equal(before.Files.Keys.Order().ToArray(), after.Files.Keys.Order().ToArray()); // same entries
        foreach (var (name, text) in before.Files) Assert.Equal(text, after.Files[name]); // same content, entry by entry (review.json without its export time)
        Assert.Equal(before.ReviewProperties, after.ReviewProperties); // same schema
        Assert.DoesNotContain(after.ReviewProperties, p => p.Contains("risk", StringComparison.OrdinalIgnoreCase));
        foreach (var text in new[] { "RATIONALE-REVIEWED-1", "NOTE-REVIEWED-1", "DESC-REVIEWED-1", "riskAssessment", "riskMitigation" }) Assert.DoesNotContain(after.Files.Values, v => v.Contains(text, StringComparison.Ordinal));
    }
}
