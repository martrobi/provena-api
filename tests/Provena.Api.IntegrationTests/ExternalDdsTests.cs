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
using static Provena.Api.IntegrationTests.ExternalDdsTestSupport;
namespace Provena.Api.IntegrationTests;

// External DDS Record v1 over real HTTP and PostgreSQL: the append-only record of an external submission, its Owner-only recording under the case lock, optimistic concurrency, the review rules,
// replacement semantics, terminal cases, the ExternalDdsCompleted close gate and the sensitive-value handling. Each scenario provisions its own organization through AuthService.
[Collection("Api")]
public sealed class ExternalDdsTests(ApiFactory factory)
{
    private const string Password = "Test-Only-Password-928!";
    private const string Secret = "VERIF-SECRET-9Z4K"; // fixture value: asserted to never leak outside the dedicated endpoints
    private sealed record Owner(HttpClient Client, Guid Org, Guid UserId);

    // ---- fixtures -------------------------------------------------------------------------------------------------------------------------
    private async Task<Owner> NewOwner()
    {
        var name = "Dds-" + Guid.NewGuid().ToString("N")[..8];
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
    private Task<int> Records(Guid org, Guid caseId) => Query(org, db => db.ExternalDdsRecords.CountAsync(x => x.EudrCaseId == caseId));
    private Task<int> Audits(Guid org, Guid caseId, string type) => Query(org, db => db.AuditEvents.CountAsync(x => x.EudrCaseId == caseId && x.EventType == type));
    private Task<int> Closures(Guid org, Guid caseId) => Query(org, db => db.CaseClosures.CountAsync(x => x.EudrCaseId == caseId));

    private static Task<HttpResponseMessage> Close(HttpClient c, Guid caseId, Guid? expectedReviewId, string? reference = null) => c.PostAsJsonAsync($"/api/eudr-cases/{caseId}/close", new { expectedReviewId, externalDdsReference = reference });
    private static Guid? LatestId(JsonElement completeness) => completeness.GetProperty("latestReview").ValueKind == JsonValueKind.Null ? null : completeness.GetProperty("latestReview").GetProperty("reviewId").GetGuid();
    private static async Task<Guid> Review(HttpClient c, Guid caseId, string decision)
    {
        var state = await Completeness(c, caseId);
        var review = await Json(await c.PostAsJsonAsync($"/api/eudr-cases/{caseId}/reviews", new { decision, rationale = "Reviewed against the recorded facts.", expectedFingerprint = Fingerprint(state), expectedLatestReviewId = LatestId(state) }), HttpStatusCode.Created);
        return review.GetProperty("reviewId").GetGuid();
    }
    private static async Task<JsonElement> Problem(HttpResponseMessage response, HttpStatusCode status)
    { var json = await Json(response, status); Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType); return json; }
    private static string Reason(JsonElement problem) => problem.GetProperty("errors").GetProperty("reason").GetString()!;
    private static async Task<Guid> UploadDocument(HttpClient c, Guid caseId, string name = "receipt.txt")
    { using var body = Upload(name, "text/plain", "receipt"); return (await Json(await c.PostAsync($"/api/eudr-cases/{caseId}/documents", body), HttpStatusCode.OK)).GetProperty("id").GetGuid(); }
    private static async Task<JsonElement> History(HttpClient c, Guid caseId) => await Json(await c.GetAsync($"/api/eudr-cases/{caseId}/external-dds-history"), HttpStatusCode.OK);

    // A reviewed case: the default dossier accepted as complete, or the review decision requested for a light case.
    private async Task<(Owner Owner, World World, Guid ReviewId)> Reviewed(string decision = "AcceptedAsCompleteUnderPolicy")
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org);
        return (o, w, await Review(o.Client, w.CaseId, decision));
    }

    // ---- authorization -----------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Recording_is_owner_only_a_member_learns_nothing_and_every_foreign_case_is_not_found()
    {
        var (o, w, reviewId) = await Reviewed(); var other = await NewOwner(); var foreign = await Build(other.Client, other.Org);
        var member = await NewMember(o.Org); using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(anonymous, w.CaseId, Body(reviewId))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Url(w.CaseId))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Url(w.CaseId) + "-history")).StatusCode);
        // A genuine Member: 403 for an existing, a nonexistent and another organization's case, and for an invalid or malformed body - never 404, 400 or 415.
        foreach (var caseId in new[] { w.CaseId, Guid.NewGuid(), foreign.CaseId }) Assert.Equal(HttpStatusCode.Forbidden, (await Post(member, caseId, Body(reviewId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(member, w.CaseId, new { referenceNumber = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync(Url(w.CaseId), Raw("{ not json"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync(Url(w.CaseId), null)).StatusCode);
        Assert.Equal(0, await Records(o.Org, w.CaseId)); Assert.Equal(0, await Audits(o.Org, w.CaseId, "ExternalDdsRecorded"));
        // The Owner meets the documented order: content type, unknown/foreign case, then the body.
        await Problem(await Post(o.Client, Guid.NewGuid(), Body(reviewId)), HttpStatusCode.NotFound);
        await Problem(await Post(o.Client, foreign.CaseId, Body(reviewId)), HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await o.Client.PostAsync(Url(w.CaseId), null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await o.Client.PostAsync(Url(w.CaseId), Raw("{ not json"))).StatusCode);
        // A foreign case is invisible to the reads too, and a Member can read.
        await Problem(await o.Client.GetAsync(Url(foreign.CaseId)), HttpStatusCode.NotFound); await Problem(await o.Client.GetAsync(Url(foreign.CaseId) + "-history"), HttpStatusCode.NotFound);
        await Problem(await o.Client.GetAsync(Url(Guid.NewGuid())), HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.NoContent, (await member.GetAsync(Url(w.CaseId))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync(Url(w.CaseId) + "-history")).StatusCode);
        // No PUT, PATCH or DELETE exists.
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await o.Client.SendAsync(new HttpRequestMessage(method, Url(w.CaseId)) { Content = JsonContent.Create(Body(reviewId)) })).StatusCode);
    }

    [Fact]
    public async Task The_owner_check_is_live_a_demoted_owner_with_a_valid_token_cannot_record()
    {
        var (o, w, reviewId) = await Reviewed();
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync($"""UPDATE "OrganizationUsers" SET "Role" = {(int)OrganizationRole.Member} WHERE "UserId" = {o.UserId}""");
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(o.Client, w.CaseId, Body(reviewId))).StatusCode);
        Assert.Equal(0, await Records(o.Org, w.CaseId));
    }

    // ---- first record, replacement and concurrency ------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_first_record_is_created_read_back_and_listed_and_a_replacement_retains_the_old_row()
    {
        var (o, w, reviewId) = await Reviewed(); var c = o.Client;
        Assert.Equal(HttpStatusCode.NoContent, (await c.GetAsync(Url(w.CaseId))).StatusCode);
        Assert.Empty((await History(c, w.CaseId)).EnumerateArray());

        var submitted = DateTimeOffset.UtcNow;
        var created = await Post(c, w.CaseId, Body(reviewId, "  EU-REF-1  ", $"  {Secret}  ", submittedAt: submitted));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); Assert.EndsWith(Url(w.CaseId), created.Headers.Location!.AbsolutePath);
        var first = await Json(created);
        Assert.Equal("EU-REF-1", first.GetProperty("referenceNumber").GetString()); Assert.Equal(Secret, first.GetProperty("verificationNumber").GetString()); // trimmed, stored separately
        Assert.Equal(reviewId, first.GetProperty("reviewId").GetGuid()); Assert.Equal(1, first.GetProperty("number").GetInt32()); Assert.True(first.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(o.UserId, first.GetProperty("recordedByUserId").GetGuid()); Assert.Equal(submitted.ToUnixTimeSeconds(), first.GetProperty("submittedAtUtc").GetDateTimeOffset().ToUnixTimeSeconds());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("supersedesRecordId").ValueKind); Assert.Equal(JsonValueKind.Null, first.GetProperty("supersededAtUtc").ValueKind); Assert.Equal(JsonValueKind.Null, first.GetProperty("receiptDocumentId").ValueKind);
        AssertNoOrganizationId(first);
        var firstId = first.GetProperty("recordId").GetGuid();

        var current = await Json(await c.GetAsync(Url(w.CaseId)), HttpStatusCode.OK);
        Assert.Equal(firstId, current.GetProperty("recordId").GetGuid());
        Assert.Equal([firstId], (await History(c, w.CaseId)).EnumerateArray().Select(x => x.GetProperty("recordId").GetGuid()).ToArray());

        // A replacement needs the id the Owner loaded and a reason; it keeps the old row and points at it.
        var noReason = await Problem(await Post(c, w.CaseId, Body(reviewId, "EU-REF-2", Secret, expected: firstId)), HttpStatusCode.BadRequest);
        Assert.True(noReason.GetProperty("errors").TryGetProperty("ReplacementReason", out _));
        await Problem(await Post(c, w.CaseId, Body(reviewId, "EU-REF-2", Secret, expected: firstId, reason: "   ")), HttpStatusCode.BadRequest);
        Assert.Equal(1, await Records(o.Org, w.CaseId));
        var second = await Json(await Post(c, w.CaseId, Body(reviewId, "EU-REF-2", "VERIF-SECOND-1", expected: firstId, reason: "  Reference was mistyped.  ")), HttpStatusCode.Created);
        Assert.Equal(2, second.GetProperty("number").GetInt32()); Assert.Equal(firstId, second.GetProperty("supersedesRecordId").GetGuid()); Assert.Equal("Reference was mistyped.", second.GetProperty("replacementReason").GetString());
        var secondId = second.GetProperty("recordId").GetGuid();

        Assert.Equal(secondId, (await Json(await c.GetAsync(Url(w.CaseId)), HttpStatusCode.OK)).GetProperty("recordId").GetGuid());
        var history = (await History(c, w.CaseId)).EnumerateArray().ToList();
        Assert.Equal([secondId, firstId], history.Select(x => x.GetProperty("recordId").GetGuid()).ToArray()); // newest first
        Assert.True(history[0].GetProperty("isCurrent").GetBoolean()); Assert.False(history[1].GetProperty("isCurrent").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, history[1].GetProperty("supersededAtUtc").ValueKind);
        Assert.Equal("EU-REF-1", history[1].GetProperty("referenceNumber").GetString()); Assert.Equal(Secret, history[1].GetProperty("verificationNumber").GetString()); // the old row is retained unchanged
        Assert.Equal(1, await Query(o.Org, db => db.ExternalDdsRecords.CountAsync(x => x.EudrCaseId == w.CaseId && x.SupersededAtUtc == null)));
        Assert.Equal(1, await Audits(o.Org, w.CaseId, "ExternalDdsRecorded")); Assert.Equal(1, await Audits(o.Org, w.CaseId, "ExternalDdsReplaced"));
        // A reason without a record to replace is refused.
        var other = await Reviewed(); await Problem(await Post(other.Owner.Client, other.World.CaseId, Body(other.ReviewId, reason: "nothing to replace")), HttpStatusCode.BadRequest);
        // The record changes no dossier fact: the review stays Current and is not rewritten.
        Assert.Equal("Current", (await Completeness(c, w.CaseId)).GetProperty("reviewFreshness").GetString());
    }

    [Fact]
    public async Task A_stale_expected_record_id_is_a_conflict_that_creates_no_row_and_has_no_side_effects()
    {
        var (o, w, reviewId) = await Reviewed(); var c = o.Client;
        var firstId = await RecordOk(c, w.CaseId, reviewId, "EU-REF-1");
        var audits = await Audits(o.Org, w.CaseId, "ExternalDdsReplaced") + await Audits(o.Org, w.CaseId, "ExternalDdsRecorded");
        // null while a record exists, a wrong id and an unknown id are all 409.
        foreach (var expected in new Guid?[] { null, Guid.NewGuid() })
        {
            var problem = await Problem(await Post(c, w.CaseId, Body(reviewId, "EU-REF-X", Secret, expected: expected, reason: "Correction.")), HttpStatusCode.Conflict);
            Assert.Equal("ExternalDdsHistoryChanged", Reason(problem));
        }
        var secondId = await RecordOk(c, w.CaseId, reviewId, "EU-REF-2"); // legitimately replaces the first
        // The first id is now stale: a client that loaded it cannot overwrite the newer record.
        var stale = await Problem(await Post(c, w.CaseId, Body(reviewId, "EU-REF-3", Secret, expected: firstId, reason: "Correction.")), HttpStatusCode.Conflict);
        Assert.Equal("ExternalDdsHistoryChanged", Reason(stale));
        Assert.Equal(2, await Records(o.Org, w.CaseId)); Assert.Equal(audits + 1, await Audits(o.Org, w.CaseId, "ExternalDdsReplaced") + await Audits(o.Org, w.CaseId, "ExternalDdsRecorded"));
        Assert.Equal(secondId, (await Json(await c.GetAsync(Url(w.CaseId)), HttpStatusCode.OK)).GetProperty("recordId").GetGuid());
    }

    [Fact]
    public async Task Simultaneous_first_records_are_serialized_so_exactly_one_wins()
    {
        var (o, w, reviewId) = await Reviewed();
        var results = await Task.WhenAll(Post(o.Client, w.CaseId, Body(reviewId, "EU-REF-A")), Post(o.Client, w.CaseId, Body(reviewId, "EU-REF-B")));
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], results.Select(r => r.StatusCode).Order().ToArray());
        Assert.Equal(1, await Records(o.Org, w.CaseId)); Assert.Equal(1, await Audits(o.Org, w.CaseId, "ExternalDdsRecorded"));
    }

    // ---- input validation ----------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Invalid_input_is_rejected_with_a_400_that_never_echoes_the_values_and_writes_nothing()
    {
        var (o, w, reviewId) = await Reviewed(); var c = o.Client;
        var bodies = new Dictionary<string, object>
        {
            ["ReviewId"] = Body(null), ["ReferenceNumber"] = Body(reviewId, "   "), ["VerificationNumber"] = Body(reviewId, "REF", ""), ["SubmittedAtUtc"] = new { reviewId, referenceNumber = "REF", verificationNumber = Secret },
        };
        foreach (var (field, body) in bodies)
            Assert.True((await Problem(await Post(c, w.CaseId, body), HttpStatusCode.BadRequest)).GetProperty("errors").TryGetProperty(field, out _), field);
        await Problem(await Post(c, w.CaseId, Body(reviewId, new string('x', 201))), HttpStatusCode.BadRequest);
        var tooLong = await Problem(await Post(c, w.CaseId, Body(reviewId, "REF", Secret + new string('y', 200))), HttpStatusCode.BadRequest);
        var future = await Problem(await Post(c, w.CaseId, Body(reviewId, "REF", Secret, submittedAt: DateTimeOffset.UtcNow.AddMinutes(10))), HttpStatusCode.BadRequest);
        Assert.True(future.GetProperty("errors").TryGetProperty("SubmittedAtUtc", out _));
        foreach (var problem in new[] { tooLong, future }) Assert.DoesNotContain(Secret, problem.GetRawText());
        Assert.Equal(0, await Records(o.Org, w.CaseId));
        // A non-UTC offset is normalized, not rejected.
        var created = await Json(await c.PostAsync(Url(w.CaseId), Raw($$"""{"reviewId":"{{reviewId}}","referenceNumber":"REF","verificationNumber":"V1","submittedAtUtc":"{{DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(2)):O}}"}""")), HttpStatusCode.Created);
        Assert.Equal(TimeSpan.Zero, created.GetProperty("submittedAtUtc").GetDateTimeOffset().Offset);
    }

    [Fact]
    public async Task The_submission_time_cannot_precede_the_referenced_review_equality_is_valid_and_a_failed_replacement_leaves_the_previous_record_current()
    {
        var (o, w, reviewId) = await Reviewed(); var c = o.Client;
        var reviewedAt = await Query(o.Org, db => db.ReviewRecords.Where(x => x.Id == reviewId).Select(x => x.ReviewedAtUtc).SingleAsync());
        // 1. Before the review: 400 on submittedAtUtc, no record, no audit, and the value is not echoed.
        var early = await Problem(await Post(c, w.CaseId, Body(reviewId, "REF-1", Secret, submittedAt: reviewedAt.AddTicks(-10))), HttpStatusCode.BadRequest);
        Assert.True(early.GetProperty("errors").TryGetProperty("SubmittedAtUtc", out _)); Assert.DoesNotContain(Secret, early.GetRawText());
        Assert.Equal(0, await Records(o.Org, w.CaseId)); Assert.Equal(0, await Audits(o.Org, w.CaseId, "ExternalDdsRecorded"));
        // 2. Exactly equal: accepted.
        var first = await Json(await Post(c, w.CaseId, Body(reviewId, "REF-1", Secret, submittedAt: reviewedAt)), HttpStatusCode.Created);
        var firstId = first.GetProperty("recordId").GetGuid();
        // 3. A replacement against a NEW review, dated before that review: rejected, the previous record stays current and unsuperseded.
        await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/recipients", new { resolution = "RecipientsIdentified", recipients = new[] { new { name = "Other GmbH", postalAddress = "Weg 2, Wien", email = "other@example.test" } } }));
        var review2 = await Review(c, w.CaseId, "AcceptedAsCompleteUnderPolicy");
        var reviewed2 = await Query(o.Org, db => db.ReviewRecords.Where(x => x.Id == review2).Select(x => x.ReviewedAtUtc).SingleAsync());
        await Problem(await Post(c, w.CaseId, Body(review2, "REF-2", Secret, expected: firstId, reason: "Newer review.", submittedAt: reviewedAt)), HttpStatusCode.BadRequest);
        Assert.Equal(firstId, await CurrentId(c, w.CaseId)); Assert.Equal(1, await Records(o.Org, w.CaseId));
        Assert.Null(await Query(o.Org, db => db.ExternalDdsRecords.Where(x => x.Id == firstId).Select(x => x.SupersededAtUtc).SingleAsync()));
        Assert.Equal(0, await Audits(o.Org, w.CaseId, "ExternalDdsReplaced"));
        Assert.Equal(HttpStatusCode.Created, (await Post(c, w.CaseId, Body(review2, "REF-2", Secret, expected: firstId, reason: "Newer review.", submittedAt: reviewed2))).StatusCode);
        // 4. A future time is still rejected.
        await Problem(await Post(c, w.CaseId, Body(review2, "REF-3", Secret, expected: await CurrentId(c, w.CaseId), reason: "x", submittedAt: DateTimeOffset.UtcNow.AddMinutes(10))), HttpStatusCode.BadRequest);
        Assert.Equal(2, await Records(o.Org, w.CaseId));
    }

    // ---- review rules ---------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_referenced_review_must_be_the_latest_accepted_and_current_and_a_foreign_review_id_leaks_nothing()
    {
        var o = await NewOwner(); var c = o.Client; var w = await Build(c, o.Org); var other = await NewOwner(); var foreign = await Build(other.Client, other.Org);
        var foreignReview = await Review(other.Client, foreign.CaseId, "AcceptedAsCompleteUnderPolicy");
        // No review at all, an unknown review and another organization's review answer identically.
        var noReview = await Problem(await Post(c, w.CaseId, Body(Guid.NewGuid())), HttpStatusCode.Conflict);
        var foreignProblem = await Problem(await Post(c, w.CaseId, Body(foreignReview)), HttpStatusCode.Conflict);
        Assert.Equal("ReviewNotLatest", Reason(noReview)); Assert.Equal(noReview.GetProperty("detail").GetString(), foreignProblem.GetProperty("detail").GetString());

        var returned = await Review(c, w.CaseId, "ReturnedForCorrection");
        Assert.Equal("ReviewNotAccepted", Reason(await Problem(await Post(c, w.CaseId, Body(returned)), HttpStatusCode.Conflict)));
        var accepted = await Review(c, w.CaseId, "AcceptedAsCompleteUnderPolicy");
        // The older review is no longer the latest.
        Assert.Equal("ReviewNotLatest", Reason(await Problem(await Post(c, w.CaseId, Body(returned)), HttpStatusCode.Conflict)));
        // A dossier change after the review makes it stale: refused, and the response names what changed.
        await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/recipients", new { resolution = "RecipientsIdentified", recipients = new[] { new { name = "Other GmbH", postalAddress = "Weg 2, Wien", email = "other@example.test" } } }));
        var stale = await Problem(await Post(c, w.CaseId, Body(accepted)), HttpStatusCode.Conflict);
        Assert.Equal("StaleReview", Reason(stale)); Assert.Contains("Recipients", stale.GetProperty("errors").GetProperty("changedAreas").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(0, await Records(o.Org, w.CaseId)); Assert.Equal(0, await Audits(o.Org, w.CaseId, "ExternalDdsRecorded"));
        // A fresh Current accepted review succeeds.
        var fresh = await Review(c, w.CaseId, "AcceptedAsCompleteUnderPolicy");
        Assert.Equal(fresh, (await Json(await Post(c, w.CaseId, Body(fresh)), HttpStatusCode.Created)).GetProperty("reviewId").GetGuid());
        // Reviews outside scope / unsupported flow are not external submissions either.
        var light = await NewOwner(); var lw = await Build(light.Client, light.Org, new Opt(Recipients: false, Lot: false, Documents: false));
        await Ok(light.Client.PutAsJsonAsync($"/api/eudr-cases/{lw.CaseId}/applicability", Application("OutsideRegulatoryScope", "Pending")));
        Assert.Equal("ReviewNotAccepted", Reason(await Problem(await Post(light.Client, lw.CaseId, Body(await Review(light.Client, lw.CaseId, "ConfirmedOutsideRegulatoryScope"))), HttpStatusCode.Conflict)));
    }

    // ---- receipt document ------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_receipt_must_be_a_current_document_of_the_same_case_and_a_later_withdrawal_keeps_the_historical_link()
    {
        var o = await NewOwner(); var c = o.Client; var w = await Build(c, o.Org); var other = await NewOwner(); var foreign = await Build(other.Client, other.Org);
        var receipt = await UploadDocument(c, w.CaseId); var withdrawn = await UploadDocument(c, w.CaseId, "old.txt"); var siblingCase = await Build(c, o.Org); var siblingDoc = await UploadDocument(c, siblingCase.CaseId);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/eudr-cases/{w.CaseId}/documents/{withdrawn}")).StatusCode);
        // New uploads reset the supporting-documents decision: accept it again so the dossier can be reviewed as complete.
        var supporting = (await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}"))).GetProperty("requirements").EnumerateArray().Single(r => r.GetProperty("type").GetString() == "SupportingDocuments").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await Patch(c, $"/api/eudr-cases/{w.CaseId}/requirements/{supporting}", new { status = "Accepted", reviewerNote = "ok" })).StatusCode);
        var reviewId = await Review(c, w.CaseId, "AcceptedAsCompleteUnderPolicy");
        // Withdrawn, unknown, another case's and another organization's documents all answer the same 409.
        var details = new List<string>();
        foreach (var doc in new[] { withdrawn, Guid.NewGuid(), siblingDoc, foreign.Docs[0] })
        {
            var problem = await Problem(await Post(c, w.CaseId, Body(reviewId, receipt: doc)), HttpStatusCode.Conflict);
            Assert.Equal("ReceiptDocumentNotCurrent", Reason(problem)); details.Add(problem.GetProperty("detail").GetString()!);
        }
        Assert.Single(details.Distinct()); Assert.Equal(0, await Records(o.Org, w.CaseId));

        var record = await Json(await Post(c, w.CaseId, Body(reviewId, receipt: receipt)), HttpStatusCode.Created);
        Assert.Equal(receipt, record.GetProperty("receiptDocumentId").GetGuid());
        // Withdrawing the receipt afterwards keeps the record and its link (the bytes are retained too).
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/eudr-cases/{w.CaseId}/documents/{receipt}")).StatusCode);
        Assert.Equal(receipt, (await Json(await c.GetAsync(Url(w.CaseId)), HttpStatusCode.OK)).GetProperty("receiptDocumentId").GetGuid());
        Assert.Equal(receipt, (await History(c, w.CaseId))[0].GetProperty("receiptDocumentId").GetGuid());
        Assert.NotNull(await Query(o.Org, db => db.Documents.Where(x => x.Id == receipt).Select(x => x.WithdrawnAtUtc).SingleAsync()));
    }

    // ---- terminal cases ------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_closed_or_archived_case_keeps_the_records_readable_but_accepts_no_new_record_or_replacement()
    {
        var (o, w, reviewId) = await Reviewed(); var c = o.Client;
        var recordId = await RecordOk(c, w.CaseId, reviewId, "EU-REF-1");
        Assert.Equal(HttpStatusCode.Created, (await Close(c, w.CaseId, reviewId)).StatusCode);
        var attempt = await Problem(await Post(c, w.CaseId, Body(reviewId, "EU-REF-2", Secret, expected: recordId, reason: "Too late.")), HttpStatusCode.Conflict);
        Assert.Contains("Closed", attempt.GetProperty("detail").GetString());
        Assert.Equal(recordId, (await Json(await c.GetAsync(Url(w.CaseId)), HttpStatusCode.OK)).GetProperty("recordId").GetGuid());
        Assert.Single((await History(c, w.CaseId)).EnumerateArray()); Assert.Equal(1, await Records(o.Org, w.CaseId));
        // Archived: independent of the status (archiving a closed case is allowed), and equally read-only.
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync($"/api/eudr-cases/{w.CaseId}/archive", new { reason = "Retention hold." })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(c, w.CaseId, Body(reviewId, "EU-REF-2", Secret, expected: recordId, reason: "Too late."))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(Url(w.CaseId))).StatusCode); Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(Url(w.CaseId) + "-history")).StatusCode);

        var (o2, w2, review2) = await Reviewed();
        Assert.Equal(HttpStatusCode.Created, (await o2.Client.PostAsJsonAsync($"/api/eudr-cases/{w2.CaseId}/archive", new { reason = "Retention hold." })).StatusCode);
        Assert.Contains("Archived", (await Problem(await Post(o2.Client, w2.CaseId, Body(review2)), HttpStatusCode.Conflict)).GetProperty("detail").GetString());
        Assert.Equal(0, await Records(o2.Org, w2.CaseId));
    }

    // ---- the ExternalDdsCompleted close gate --------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Closing_as_ExternalDdsCompleted_needs_the_record_of_exactly_the_latest_review_and_a_matching_legacy_reference_and_a_failed_close_writes_nothing()
    {
        var (o, w, reviewId) = await Reviewed(); var c = o.Client;
        var link = await Json(await c.PostAsync($"/api/eudr-cases/{w.CaseId}/supplier-request", null)); var token = link.GetProperty("publicUrl").GetString()!.Split('/').Last();
        // 1. No record: the free-text field alone can no longer close a case.
        foreach (var reference in new string?[] { null, "OLD-CLIENT-REF" })
        {
            var missing = await Problem(await Close(c, w.CaseId, reviewId, reference), HttpStatusCode.Conflict);
            Assert.Equal("ExternalDdsRecordMissing", Reason(missing)); Assert.Equal(JsonValueKind.Null, missing.GetProperty("errors").GetProperty("externalDds").ValueKind);
        }
        // 2. A record of an OLDER review: new review of a changed dossier, so the record points at a review that is no longer the latest.
        var oldRecord = await RecordOk(c, w.CaseId, reviewId, "EU-REF-1", Secret);
        await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/recipients", new { resolution = "RecipientsIdentified", recipients = new[] { new { name = "Other GmbH", postalAddress = "Weg 2, Wien", email = "other@example.test" } } }));
        Assert.Equal("StaleReview", Reason(await Problem(await Close(c, w.CaseId, reviewId), HttpStatusCode.Conflict))); // stale wins over every DDS error
        var review2 = await Review(c, w.CaseId, "AcceptedAsCompleteUnderPolicy");
        var mismatch = await Problem(await Close(c, w.CaseId, review2), HttpStatusCode.Conflict);
        Assert.Equal("ExternalDdsReviewMismatch", Reason(mismatch));
        var summary = mismatch.GetProperty("errors").GetProperty("externalDds");
        Assert.Equal(oldRecord, summary.GetProperty("recordId").GetGuid()); Assert.Equal(reviewId, summary.GetProperty("reviewId").GetGuid()); Assert.Equal("EU-REF-1", summary.GetProperty("referenceNumber").GetString());
        // 3. The record of the latest review, but a conflicting legacy value.
        var recordId = await RecordOk(c, w.CaseId, review2, "EU-REF-2", Secret);
        var conflicting = await Problem(await Close(c, w.CaseId, review2, "EU-REF-OTHER"), HttpStatusCode.Conflict);
        Assert.Equal("ExternalDdsReferenceMismatch", Reason(conflicting)); Assert.Equal(recordId, conflicting.GetProperty("errors").GetProperty("externalDds").GetProperty("recordId").GetGuid());
        foreach (var problem in new[] { mismatch, conflicting }) { Assert.DoesNotContain(Secret, problem.GetRawText()); Assert.False(problem.GetProperty("errors").GetProperty("externalDds").TryGetProperty("verificationNumber", out _)); }
        // None of the failed closes wrote anything: no closure, no audit event, the status is unchanged and the supplier link still works.
        Assert.Equal(0, await Closures(o.Org, w.CaseId)); Assert.Equal(0, await Audits(o.Org, w.CaseId, "CaseClosed")); Assert.NotEqual("Closed", (await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}"))).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync($"/public/supplier-requests/{token}")).StatusCode);

        // 4. Matching record + Current accepted review + Matched quantities: 201, with the record's reference (a matching, untrimmed legacy value is accepted).
        var closed = await Close(c, w.CaseId, review2, "  EU-REF-2  ");
        Assert.Equal(HttpStatusCode.Created, closed.StatusCode);
        var closure = await Json(closed);
        Assert.Equal("ExternalDdsCompleted", closure.GetProperty("outcome").GetString()); Assert.Equal("EU-REF-2", closure.GetProperty("externalDdsReference").GetString());
        Assert.Equal("EU-REF-2", await Query(o.Org, db => db.CaseClosures.Where(x => x.EudrCaseId == w.CaseId).Select(x => x.ExternalDdsReference).SingleAsync()));
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync($"/public/supplier-requests/{token}")).StatusCode);
        // The close audit names the record id, never the reference or the verification number.
        var audit = await Query(o.Org, db => db.AuditEvents.Where(x => x.EventType == "CaseClosed" && x.EudrCaseId == w.CaseId).Select(x => x.Description).SingleAsync());
        Assert.Contains(recordId.ToString(), audit); Assert.DoesNotContain(Secret, audit); Assert.DoesNotContain("EU-REF-2", audit);
    }

    [Fact]
    public async Task Closing_without_the_legacy_reference_uses_the_record_and_other_outcomes_need_no_record()
    {
        var (o, w, reviewId) = await Reviewed(); var c = o.Client;
        await RecordOk(c, w.CaseId, reviewId, "EU-REF-7");
        var closure = await Json(await Close(c, w.CaseId, reviewId), HttpStatusCode.Created);
        Assert.Equal("EU-REF-7", closure.GetProperty("externalDdsReference").GetString());

        // Outside scope and unsupported flow close with no External DDS record at all, and still reject a legacy reference.
        foreach (var (decision, application) in new[] { ("ConfirmedOutsideRegulatoryScope", Application("OutsideRegulatoryScope", "Pending")), ("ConfirmedUnsupportedFlow", Application(activity: "Export")) })
        {
            var l = await NewOwner(); var lw = await Build(l.Client, l.Org, new Opt(Recipients: false, Lot: false, Documents: false));
            await Ok(l.Client.PutAsJsonAsync($"/api/eudr-cases/{lw.CaseId}/applicability", application));
            var review = await Review(l.Client, lw.CaseId, decision);
            await Problem(await Close(l.Client, lw.CaseId, review, "EU-REF-X"), HttpStatusCode.BadRequest);
            Assert.Equal(HttpStatusCode.Created, (await Close(l.Client, lw.CaseId, review)).StatusCode);
            Assert.Equal(0, await Records(l.Org, lw.CaseId));
        }
    }

    // ---- the verification number is not sensitive-data-by-accident --------------------------------------------------------------------------
    [Fact]
    public async Task The_verification_number_appears_only_in_the_dedicated_endpoints_never_in_other_dtos_audit_text_or_problem_details()
    {
        var (o, w, reviewId) = await Reviewed(); var c = o.Client;
        await Json(await Post(c, w.CaseId, Body(reviewId, "EU-REF-1", Secret)), HttpStatusCode.Created);
        var recordId = await CurrentId(c, w.CaseId);
        await Json(await Post(c, w.CaseId, Body(reviewId, "EU-REF-2", Secret, expected: recordId, reason: "Reference was mistyped.")), HttpStatusCode.Created);
        // Conflicts and validation errors on the same endpoint.
        var problems = new[]
        {
            await Problem(await Post(c, w.CaseId, Body(reviewId, "EU-REF-3", Secret, expected: recordId, reason: "Stale.")), HttpStatusCode.Conflict),
            await Problem(await Post(c, w.CaseId, Body(reviewId, "EU-REF-3", Secret, expected: await CurrentId(c, w.CaseId))), HttpStatusCode.BadRequest),
            await Problem(await Post(c, w.CaseId, Body(Guid.NewGuid(), "EU-REF-3", Secret, expected: await CurrentId(c, w.CaseId), reason: "x")), HttpStatusCode.Conflict),
        };
        foreach (var problem in problems) Assert.DoesNotContain(Secret, problem.GetRawText());
        // Audit descriptions and details carry ids only.
        var audits = await Query(o.Org, db => db.AuditEvents.Where(x => x.EudrCaseId == w.CaseId).Select(x => x.Description).ToListAsync());
        Assert.DoesNotContain(audits, a => a.Contains(Secret)); Assert.DoesNotContain(audits, a => a.Contains("EU-REF-"));
        foreach (var text in audits.Where(a => a.Contains("External DDS")).Select(a => a.ToLowerInvariant()))
            foreach (var claim in new[] { "approved", "accepted by", "verified", "compliant" }) Assert.DoesNotContain(claim, text); // a record of an external fact, no regulatory claim
        // No generic DTO of the case carries it.
        foreach (var url in new[] { $"/api/eudr-cases/{w.CaseId}", $"/api/eudr-cases/{w.CaseId}/lifecycle", $"/api/eudr-cases/{w.CaseId}/completeness", $"/api/eudr-cases/{w.CaseId}/reviews", "/api/eudr-cases", $"/api/eudr-cases/{w.CaseId}/reviews/{reviewId}" })
            Assert.DoesNotContain(Secret, await (await c.GetAsync(url)).Content.ReadAsStringAsync());
        Assert.DoesNotContain(typeof(CaseClosureDto).GetProperties(), p => p.Name.Contains("Verification")); Assert.Contains(typeof(ExternalDdsRecordDto).GetProperties(), p => p.Name == "VerificationNumber");
        Assert.DoesNotContain(typeof(ExternalDdsSummaryDto).GetProperties(), p => p.Name.Contains("Verification"));
    }

    // ---- OpenAPI -----------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_openapi_document_describes_the_manually_read_external_dds_body_and_no_mutation_other_than_post()
    {
        using var anonymous = factory.CreateClient();
        var document = await Json(await anonymous.GetAsync("/openapi/v1.json"));
        var paths = document.GetProperty("paths");
        var route = paths.GetProperty("/api/eudr-cases/{caseId}/external-dds");
        Assert.Equal(["get", "post"], route.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(["get"], paths.GetProperty("/api/eudr-cases/{caseId}/external-dds-history").EnumerateObject().Select(p => p.Name).ToArray());
        var schema = route.GetProperty("post").GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        if (schema.TryGetProperty("$ref", out var reference)) schema = document.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/').Last());
        Assert.Equal(["referenceNumber", "reviewId", "submittedAtUtc", "verificationNumber"], schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()!).Order().ToArray());
    }
}
