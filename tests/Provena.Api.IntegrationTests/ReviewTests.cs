using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Provena.Api.Auth;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
using Provena.Api.Services;
using Xunit;
using static Provena.Api.IntegrationTests.CompletenessReadModelTests;
namespace Provena.Api.IntegrationTests;

// Slice 5 / commit 3: Owner-only immutable reviews, optimistic concurrency, freshness, snapshot immutability, the reviewed export and the lock behaviour, over real HTTP and PostgreSQL.
// Each scenario provisions its own organization through AuthService (the shared auth rate limiter is nearly consumed by other classes).
[Collection("Api")]
public sealed class ReviewTests(ApiFactory factory)
{
    private const string Password = "Test-Only-Password-928!";
    private sealed record Owner(HttpClient Client, Guid Org, Guid UserId);

    private async Task<Owner> NewOwner()
    {
        var name = "Review-" + Guid.NewGuid().ToString("N")[..8];
        await using var scope = factory.Services.CreateAsyncScope();
        var token = await scope.ServiceProvider.GetRequiredService<AuthService>().Register(new RegisterRequest($"{name}@example.test", Password, "Test", "User", "Org " + name, null), CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Organizations.Where(x => x.Name == "Org " + name).Select(x => x.Id).SingleAsync();
        var user = await db.OrganizationUsers.Where(x => x.OrganizationId == org).Select(x => x.UserId).SingleAsync();
        return new(Client(token.AccessToken), org, user);
    }
    private HttpClient Client(string token) { var c = factory.CreateClient(); c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); return c; }

    // A real Member of the organization: registered with an own organization, then moved to the target organization with the Member role, and logged in so the JWT is genuine.
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
        var token = await auth.Login(new LoginRequest(email, Password), CancellationToken.None);
        return Client(token.AccessToken);
    }
    private AppDbContext TenantDb(IServiceScope scope, Guid org)
    {
        var tenant = new TenantContext(new HttpContextAccessor()); tenant.SetOrganization(org);
        return new AppDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>(), tenant);
    }
    private async Task<int> Count<T>(Guid org, Func<AppDbContext, IQueryable<T>> query) where T : class
    { await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org); return await query(db).CountAsync(); }
    private Task<int> Revisions(Guid org, Guid caseId) => Count(org, db => db.CaseRevisions.Where(x => x.EudrCaseId == caseId));

    // ---- request helpers ------------------------------------------------------------------------------------------------------------------
    private static object Body(string decision, string fingerprint, Guid? latest = null, string rationale = "Reviewed against the recorded facts.") => new { decision, rationale, expectedFingerprint = fingerprint, expectedLatestReviewId = latest };
    private static Task<HttpResponseMessage> Post(HttpClient c, Guid caseId, object body) => c.PostAsJsonAsync($"/api/eudr-cases/{caseId}/reviews", body);
    private static Guid? LatestId(JsonElement completeness) => completeness.GetProperty("latestReview").ValueKind == JsonValueKind.Null ? null : completeness.GetProperty("latestReview").GetProperty("reviewId").GetGuid();
    // Loads the case exactly as an Owner would (fingerprint + latest review) and records the review.
    private static async Task<HttpResponseMessage> ReviewNow(HttpClient c, Guid caseId, string decision = "ReturnedForCorrection")
    { var state = await Completeness(c, caseId); return await Post(c, caseId, Body(decision, Fingerprint(state), LatestId(state))); }
    private static async Task<JsonElement> Reviewed(HttpClient c, Guid caseId, string decision = "ReturnedForCorrection") => await Json(await ReviewNow(c, caseId, decision), HttpStatusCode.Created);
    private static async Task<JsonElement> Problem(HttpResponseMessage response, HttpStatusCode status)
    { var json = await Json(response, status); Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType); return json; }
    private static async Task<string> FreshnessOf(HttpClient c, Guid caseId) => (await Completeness(c, caseId)).GetProperty("reviewFreshness").GetString()!;
    private static async Task<string[]> ChangedAreas(HttpClient c, Guid caseId) => (await Completeness(c, caseId)).GetProperty("latestReview").GetProperty("changedAreas").EnumerateArray().Select(x => x.GetString()!).ToArray();
    private static async Task<Dictionary<string, byte[]>> ReadZip(HttpResponseMessage response)
    {
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var files = new Dictionary<string, byte[]>();
        foreach (var e in zip.Entries) { await using var s = e.Open(); using var m = new MemoryStream(); await s.CopyToAsync(m); files[e.FullName] = m.ToArray(); }
        return files;
    }

    // ---- authorization (Owner before case and body) -----------------------------------------------------------------------------------------
    [Fact]
    public async Task Review_creation_is_owner_only_and_a_member_learns_nothing_about_the_case_or_the_body()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org); var other = await NewOwner(); var foreign = await Build(other.Client, other.Org, new Opt(Lot: false, Documents: false));
        var member = await NewMember(o.Org); var fp = Fingerprint(await Completeness(o.Client, w.CaseId)); using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(anonymous, w.CaseId, Body("ReturnedForCorrection", fp))).StatusCode);

        // A genuine Member: 403 for an existing case, a nonexistent case, another organization's case and an invalid body - never 404 or 400.
        foreach (var (caseId, body) in new (Guid, object)[] { (w.CaseId, Body("ReturnedForCorrection", fp)), (Guid.NewGuid(), Body("ReturnedForCorrection", fp)), (foreign.CaseId, Body("ReturnedForCorrection", fp)),
                                                              (w.CaseId, new { decision = "Nonsense" }), (w.CaseId, Body("ReturnedForCorrection", "", rationale: ""))})
            Assert.Equal(HttpStatusCode.Forbidden, (await Post(member, caseId, body)).StatusCode);
        Assert.Equal(0, await Revisions(o.Org, w.CaseId));
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync($"/api/eudr-cases/{w.CaseId}/reviews", null)).StatusCode); // no body and no content type: still 403, not 415
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync($"/api/eudr-cases/{w.CaseId}/reviews", Raw("{ not json"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await o.Client.PostAsync($"/api/eudr-cases/{w.CaseId}/reviews", null)).StatusCode); // the Owner is told about the content type
        // A Member can read the history.
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/eudr-cases/{w.CaseId}/reviews")).StatusCode);

        // The Owner: the case and the body are examined in the documented order.
        await Problem(await Post(o.Client, Guid.NewGuid(), Body("ReturnedForCorrection", fp)), HttpStatusCode.NotFound);
        await Problem(await Post(o.Client, foreign.CaseId, Body("ReturnedForCorrection", fp)), HttpStatusCode.NotFound);
        await Problem(await Post(o.Client, w.CaseId, Body("ReturnedForCorrection", fp, rationale: " ")), HttpStatusCode.BadRequest);
        await Problem(await Post(o.Client, w.CaseId, Body("ReturnedForCorrection", "not-a-fingerprint")), HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.BadRequest, (await o.Client.PostAsync($"/api/eudr-cases/{w.CaseId}/reviews", Raw("""{"decision":"Nonsense","rationale":"r","expectedFingerprint":"a"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await o.Client.PostAsync($"/api/eudr-cases/{w.CaseId}/reviews", Raw("""{"decision":0,"rationale":"r","expectedFingerprint":"a"}"""))).StatusCode); // integer enums are rejected
        Assert.Equal(0, await Revisions(o.Org, w.CaseId));
    }

    [Fact]
    public async Task The_owner_check_is_live_a_demoted_owner_with_a_valid_token_is_denied()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org, new Opt(Lot: false, Documents: false));
        var fp = Fingerprint(await Completeness(o.Client, w.CaseId));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "OrganizationUsers" SET "Role" = {(int)OrganizationRole.Member} WHERE "UserId" = {o.UserId}""");
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(o.Client, w.CaseId, Body("ReturnedForCorrection", fp))).StatusCode); // the JWT still says Owner
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync($"""UPDATE "OrganizationUsers" SET "Role" = {(int)OrganizationRole.Owner} WHERE "UserId" = {o.UserId}""");
        Assert.Equal(HttpStatusCode.Created, (await Post(o.Client, w.CaseId, Body("ReturnedForCorrection", fp))).StatusCode);
    }

    [Fact]
    public async Task A_terminal_case_rejects_reviews_but_keeps_its_reviews_readable()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org, new Opt(Lot: false, Documents: false));
        var review = await Reviewed(o.Client, w.CaseId);
        var fp = Fingerprint(await Completeness(o.Client, w.CaseId));
        await LifecycleTestSupport.Archive(factory.Services, w.CaseId); // archive is independent of the workflow status and read-only
        await Problem(await Post(o.Client, w.CaseId, Body("ReturnedForCorrection", fp, review.GetProperty("reviewId").GetGuid())), HttpStatusCode.Conflict);
        await Problem(await Post(o.Client, w.CaseId, Body("ReturnedForCorrection", fp, rationale: "")), HttpStatusCode.Conflict); // 409 before the body is examined
        Assert.Equal(1, await Revisions(o.Org, w.CaseId));
        Assert.Equal(HttpStatusCode.OK, (await o.Client.GetAsync($"/api/eudr-cases/{w.CaseId}/reviews/{review.GetProperty("reviewId").GetGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await o.Client.GetAsync($"/api/eudr-cases/{w.CaseId}/reviews/{review.GetProperty("reviewId").GetGuid()}/export")).StatusCode);
        Assert.Equal("Current", await FreshnessOf(o.Client, w.CaseId));
    }

    // ---- a successful review ----------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task An_owner_records_an_accepted_review_that_pins_exactly_what_was_loaded()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org);
        var before = await Completeness(o.Client, w.CaseId); var detailBefore = await Json(await o.Client.GetAsync($"/api/eudr-cases/{w.CaseId}"));
        Assert.Equal("None", before.GetProperty("reviewFreshness").GetString()); Assert.Equal(JsonValueKind.Null, before.GetProperty("latestReview").ValueKind);
        var response = await ReviewNow(o.Client, w.CaseId, "AcceptedAsCompleteUnderPolicy");
        var review = await Json(response, HttpStatusCode.Created);
        var reviewId = review.GetProperty("reviewId").GetGuid();

        Assert.EndsWith($"/api/eudr-cases/{w.CaseId}/reviews/{reviewId}", response.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.OK, (await o.Client.GetAsync(response.Headers.Location)).StatusCode); // the Location resolves
        Assert.Equal(1, review.GetProperty("revisionNumber").GetInt32()); Assert.Equal("AcceptedAsCompleteUnderPolicy", review.GetProperty("decision").GetString()); Assert.Equal(o.UserId, review.GetProperty("reviewerUserId").GetGuid());
        Assert.Equal(Fingerprint(before), review.GetProperty("fingerprint").GetString()); Assert.Equal(1, review.GetProperty("fingerprintVersion").GetInt32()); Assert.Equal(CompletenessPolicy.Version, review.GetProperty("policyVersion").GetString());
        Assert.Equal(1, review.GetProperty("snapshotSchemaVersion").GetInt32());
        Assert.Equal(DossierFingerprintSections(), review.GetProperty("sectionHashes").EnumerateObject().Select(p => p.Name).Order().ToArray());
        AssertNoOrganizationId(review);

        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, o.Org);
        var revision = await db.CaseRevisions.AsNoTracking().SingleAsync(x => x.EudrCaseId == w.CaseId); var record = await db.ReviewRecords.AsNoTracking().SingleAsync(x => x.EudrCaseId == w.CaseId);
        Assert.Equal(1, revision.Number); Assert.Equal(revision.Id, record.CaseRevisionId); Assert.Equal(reviewId, record.Id); Assert.Equal(o.UserId, record.ReviewerUserId); Assert.Equal(CompletenessPolicy.Version, record.PolicyVersion); Assert.Equal(Fingerprint(before), revision.Fingerprint);
        var lot = await db.CaseLots.AsNoTracking().Current().SingleAsync(); var application = await db.CaseApplicabilities.AsNoTracking().Current().SingleAsync();
        Assert.Equal(lot.Id, revision.CaseLotId); Assert.Equal(lot.CaseDossierId, revision.CaseDossierId); Assert.Equal(application.Id, revision.CaseApplicabilityId);
        Assert.Equal(before.GetProperty("applicability").GetProperty("currentVersionId").GetGuid(), revision.CaseApplicabilityId);
        Assert.Equal((await db.CaseRecipientStatements.AsNoTracking().Current().SingleAsync()).Id, revision.CaseRecipientStatementId); Assert.Equal((await db.CaseMasterDataVersions.AsNoTracking().Current().SingleAsync()).Id, revision.CaseMasterDataVersionId);
        Assert.Equal((await db.Geolocations.AsNoTracking().Current().SingleAsync()).Id, revision.GeoSubmissionId);
        Assert.Equal(w.Docs.Order(), await db.CaseRevisionDocuments.Where(x => x.CaseRevisionId == revision.Id).Select(x => x.CaseDocumentId).OrderBy(x => x).ToListAsync());
        Assert.Equal(w.Links.Order(), await db.CaseRevisionEvidenceLinks.Where(x => x.CaseRevisionId == revision.Id).Select(x => x.CaseEvidenceLinkId).OrderBy(x => x).ToListAsync());
        var audits = await db.AuditEvents.AsNoTracking().Where(x => x.EudrCaseId == w.CaseId && x.EventType == "CaseReviewRecorded").ToListAsync();
        var audit = Assert.Single(audits); Assert.Contains(reviewId.ToString(), audit.Description); Assert.DoesNotContain("Reviewed against", audit.Description); Assert.DoesNotContain("example.test", audit.Description); Assert.DoesNotContain("Ilica", audit.Description);

        // A review changes nothing else: requirements, status and Complete eligibility are untouched.
        var detailAfter = await Json(await o.Client.GetAsync($"/api/eudr-cases/{w.CaseId}"));
        Assert.Equal(detailBefore.GetProperty("status").GetString(), detailAfter.GetProperty("status").GetString()); Assert.Equal(detailBefore.GetProperty("requirements").GetRawText(), detailAfter.GetProperty("requirements").GetRawText());

        var after = await Completeness(o.Client, w.CaseId); var latest = after.GetProperty("latestReview");
        Assert.Equal("Current", after.GetProperty("reviewFreshness").GetString()); Assert.Equal(reviewId, latest.GetProperty("reviewId").GetGuid()); Assert.Equal(revision.Id, latest.GetProperty("caseRevisionId").GetGuid());
        Assert.Equal("AcceptedAsCompleteUnderPolicy", latest.GetProperty("decision").GetString()); Assert.False(latest.GetProperty("policyChanged").GetBoolean()); Assert.Empty(latest.GetProperty("changedAreas").EnumerateArray());
        Assert.Equal(CompletenessPolicy.Version, latest.GetProperty("policyVersion").GetString()); Assert.Equal(JsonValueKind.String, latest.GetProperty("reviewedAtUtc").ValueKind);
        Assert.Equal(Fingerprint(before), Fingerprint(after)); // recording a review is not a fact change
        Assert.Equal("ReadyForOwnerReview", after.GetProperty("evidenceReadiness").GetString()); // there is still no "Reviewed" evidence state
    }
    private static string[] DossierFingerprintSections() => new[] { "Shipment", "MasterData", "Applicability", "Recipients", "Lot", "Requirements", "Evidence" }.Order().ToArray();

    // ---- decisions ---------------------------------------------------------------------------------------------------------------------------
    private static object ApplicationWith(string scope = "InScope", string flow = "Supported", string activity = "PlacingOnMarketByImport", string role = "Operator", string? roleDescription = null) => new
    {
        productDescription = "Green coffee beans", singleRelevantCommodity = true, activityType = activity, operatorRole = role, operatorRoleDescription = roleDescription, activityDate = "2026-06-01", activityDateKind = "Planned",
        scopeCitation = "Regulation (EU) 2023/1115", legalBasisVersion = "2023/1115", effectiveDate = "2026-01-01", reasoning = "Assessed", regulatoryScope = scope, flowSupport = flow,
    };
    private static async Task<JsonElement> Failure(HttpResponseMessage response)
    {
        var problem = await Problem(response, HttpStatusCode.Conflict);
        return problem.GetProperty("errors");
    }
    private static string[] FailingCodes(JsonElement errors) => errors.GetProperty("failingItems").EnumerateArray().Select(i => i.GetProperty("code").GetString()!).ToArray();

    [Fact]
    public async Task Acceptance_requires_a_complete_supported_dossier_without_drift_and_reports_the_failing_items()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org);
        // Drift (a shared catalogue edit that was not adopted) blocks acceptance but not the other decisions' bookkeeping.
        await Ok(o.Client.PutAsJsonAsync($"/api/suppliers/{w.SupplierId}", new { name = "Edited supplier", email = "edited@example.test", postalAddress = "Street 5, Sao Paulo", countryCode = "BR" }));
        var drifted = await Completeness(o.Client, w.CaseId);
        var errors = await Failure(await Post(o.Client, w.CaseId, Body("AcceptedAsCompleteUnderPolicy", Fingerprint(drifted), LatestId(drifted))));
        Assert.Contains("supplier:", Reason(errors)); Assert.Contains("refresh the case master data", Reason(errors)); Assert.DoesNotContain(FailingCodes(errors), c => c.StartsWith("CATALOGUE_DRIFT")); Assert.Empty(FailingCodes(errors));
        Assert.Equal(0, await Revisions(o.Org, w.CaseId));
        await Ok(o.Client.PostAsync($"/api/eudr-cases/{w.CaseId}/master-data/refresh", null));
        Assert.Equal(HttpStatusCode.Created, (await ReviewNow(o.Client, w.CaseId, "AcceptedAsCompleteUnderPolicy")).StatusCode);

        // An incomplete dossier lists the failing item codes; ReturnedForCorrection is still allowed.
        var light = await Build(o.Client, o.Org, new Opt(Recipients: false, Lot: false, Documents: false));
        var state = await Completeness(o.Client, light.CaseId);
        var failing = FailingCodes(await Failure(await Post(o.Client, light.CaseId, Body("AcceptedAsCompleteUnderPolicy", Fingerprint(state), LatestId(state)))));
        Assert.All(failing, code => Assert.Contains(state.GetProperty("items").EnumerateArray(), i => i.GetProperty("code").GetString() == code)); // every failing item is a real completeness item
        Assert.Contains("PARTY_RECIPIENTS", failing); Assert.Contains("LOT_PRESENT", failing); Assert.Contains("EVIDENCE_LEGALITY_PRESENT", failing); Assert.Contains("REQUIREMENT_Geolocation", failing);
        Assert.Equal(HttpStatusCode.Created, (await ReviewNow(o.Client, light.CaseId, "ReturnedForCorrection")).StatusCode);
    }

    [Fact]
    public async Task Outside_scope_and_unsupported_flow_confirmations_follow_the_recorded_applicability()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org, new Opt(Recipients: false, Lot: false, Documents: false));
        var url = $"/api/eudr-cases/{w.CaseId}/applicability";
        // Supported flow: neither confirmation applies.
        var supported = await Completeness(o.Client, w.CaseId);
        Assert.Contains("requires OutsideRegulatoryScope", Reason(await Failure(await Post(o.Client, w.CaseId, Body("ConfirmedOutsideRegulatoryScope", Fingerprint(supported), null)))));
        Assert.Contains("requires UnsupportedFlow", Reason(await Failure(await Post(o.Client, w.CaseId, Body("ConfirmedUnsupportedFlow", Fingerprint(supported), null)))));

        await Ok(o.Client.PutAsJsonAsync(url, ApplicationWith(scope: "OutsideRegulatoryScope", flow: "Pending")));
        Assert.Equal(HttpStatusCode.Created, (await ReviewNow(o.Client, w.CaseId, "ConfirmedOutsideRegulatoryScope")).StatusCode); // flow support may stay Pending; no InScope completeness is required
        var state = await Completeness(o.Client, w.CaseId);
        Assert.Contains("requires UnsupportedFlow", Reason(await Failure(await Post(o.Client, w.CaseId, Body("ConfirmedUnsupportedFlow", Fingerprint(state), LatestId(state))))));

        await Ok(o.Client.PutAsJsonAsync(url, ApplicationWith(activity: "Export")));
        Assert.Equal(HttpStatusCode.Created, (await ReviewNow(o.Client, w.CaseId, "ConfirmedUnsupportedFlow")).StatusCode);

        // OtherUnsupported with its required description derives UnsupportedFlow and can be confirmed.
        await Ok(o.Client.PutAsJsonAsync(url, ApplicationWith(role: "OtherUnsupported", roleDescription: "Downstream trader")));
        Assert.Equal("UnsupportedFlow", (await Completeness(o.Client, w.CaseId)).GetProperty("applicability").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Created, (await ReviewNow(o.Client, w.CaseId, "ConfirmedUnsupportedFlow")).StatusCode);
        Assert.Equal(3, await Revisions(o.Org, w.CaseId));
    }
    private static string Reason(JsonElement errors) => errors.GetProperty("reason").GetString()!;

    [Fact]
    public async Task A_shipment_mismatch_blocks_every_positive_decision_but_not_a_return_for_correction()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org, new Opt(Lot: false, Documents: false));
        var supplierB = (await Json(await o.Client.PostAsJsonAsync("/api/suppliers", new { name = "Supplier B", email = "b@example.test", postalAddress = "B street", countryCode = "AR" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var productY = (await Json(await o.Client.PostAsJsonAsync("/api/products", new { name = "Product Y", cnCode = "0901", commodityType = "Coffee" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await Ok(o.Client.PutAsJsonAsync($"/api/shipments/{w.ShipmentId}", new { referenceNumber = w.Reference, supplierId = supplierB, productId = productY, quantity = 10, unit = "Kg" }));
        var mismatch = await Completeness(o.Client, w.CaseId); Assert.Equal("Invalid", StatusOf(mismatch, "MASTER_DATA_SHIPMENT_MATCH"));
        foreach (var decision in new[] { "AcceptedAsCompleteUnderPolicy", "ConfirmedOutsideRegulatoryScope", "ConfirmedUnsupportedFlow" })
            Assert.Contains("MASTER_DATA_SHIPMENT_MATCH", FailingCodes(await Failure(await Post(o.Client, w.CaseId, Body(decision, Fingerprint(mismatch), null)))));
        Assert.Equal(0, await Revisions(o.Org, w.CaseId));
        Assert.Equal(HttpStatusCode.Created, (await Post(o.Client, w.CaseId, Body("ReturnedForCorrection", Fingerprint(mismatch), null))).StatusCode);

        // Refreshing adopts the shipment; the old assessment (product X) is then ProductChanged and still blocks every positive decision.
        await Ok(o.Client.PostAsync($"/api/eudr-cases/{w.CaseId}/master-data/refresh", null));
        var changed = await Completeness(o.Client, w.CaseId); Assert.Equal("ProductChanged", changed.GetProperty("applicability").GetProperty("status").GetString());
        foreach (var decision in new[] { "AcceptedAsCompleteUnderPolicy", "ConfirmedOutsideRegulatoryScope", "ConfirmedUnsupportedFlow" })
        {
            var errors = await Failure(await Post(o.Client, w.CaseId, Body(decision, Fingerprint(changed), LatestId(changed))));
            Assert.Contains("ProductChanged", Reason(errors));
        }
        Assert.Equal(1, await Revisions(o.Org, w.CaseId));
        await Ok(o.Client.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/applicability", Application())); // a new assessment clears ProductChanged
        Assert.Equal("WithinSupportedScope", (await Completeness(o.Client, w.CaseId)).GetProperty("applicability").GetProperty("status").GetString());
    }

    // ---- optimistic concurrency ---------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_review_must_name_the_fingerprint_and_latest_review_the_owner_loaded()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org, new Opt(Lot: false, Documents: false));
        var state = await Completeness(o.Client, w.CaseId); var fp = Fingerprint(state);
        var audits = await Count(o.Org, db => db.AuditEvents.Where(x => x.EudrCaseId == w.CaseId && x.EventType == "CaseReviewRecorded"));

        var stale = await Problem(await Post(o.Client, w.CaseId, Body("ReturnedForCorrection", new string('0', 64), null)), HttpStatusCode.Conflict);
        Assert.Equal("Dossier or review history changed since it was loaded", stale.GetProperty("detail").GetString());
        Assert.Equal(0, await Revisions(o.Org, w.CaseId)); Assert.Equal(audits, await Count(o.Org, db => db.AuditEvents.Where(x => x.EudrCaseId == w.CaseId && x.EventType == "CaseReviewRecorded")));
        await Problem(await Post(o.Client, w.CaseId, Body("ReturnedForCorrection", fp, Guid.NewGuid())), HttpStatusCode.Conflict); // an expected review that does not exist
        Assert.Equal(0, await Revisions(o.Org, w.CaseId));

        var first = await Json(await Post(o.Client, w.CaseId, Body("ReturnedForCorrection", fp, null)), HttpStatusCode.Created); // null = "no review existed when I loaded it"
        var firstId = first.GetProperty("reviewId").GetGuid();
        await Problem(await Post(o.Client, w.CaseId, Body("ReturnedForCorrection", fp, null)), HttpStatusCode.Conflict); // null no longer matches
        await Problem(await Post(o.Client, w.CaseId, Body("ReturnedForCorrection", fp, Guid.NewGuid())), HttpStatusCode.Conflict);
        Assert.Equal(1, await Revisions(o.Org, w.CaseId));
        var second = await Json(await Post(o.Client, w.CaseId, Body("ReturnedForCorrection", fp, firstId)), HttpStatusCode.Created);
        Assert.Equal(2, second.GetProperty("revisionNumber").GetInt32());
        Assert.Equal(second.GetProperty("reviewId").GetGuid(), LatestId(await Completeness(o.Client, w.CaseId)));
        // A review of a changed dossier with the old fingerprint is refused.
        await Ok(o.Client.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/recipients", new { resolution = "NotApplicable", notApplicableBasis = "Stays with the operator", recipients = Array.Empty<object>() }));
        await Problem(await Post(o.Client, w.CaseId, Body("ReturnedForCorrection", fp, second.GetProperty("reviewId").GetGuid())), HttpStatusCode.Conflict);
        Assert.Equal(2, await Revisions(o.Org, w.CaseId));
    }

    [Fact]
    public async Task Two_simultaneous_reviews_of_the_same_state_produce_exactly_one_revision()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org, new Opt(Lot: false, Documents: false));
        var state = await Completeness(o.Client, w.CaseId); var body = Body("ReturnedForCorrection", Fingerprint(state), null);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Post(o.Client, w.CaseId, body)));
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], results.Select(r => r.StatusCode).Order().ToArray());
        Assert.Equal(1, await Revisions(o.Org, w.CaseId)); Assert.Equal(1, await Count(o.Org, db => db.ReviewRecords.Where(x => x.EudrCaseId == w.CaseId)));
        Assert.Equal(1, await Count(o.Org, db => db.AuditEvents.Where(x => x.EudrCaseId == w.CaseId && x.EventType == "CaseReviewRecorded")));
        var latestState = await Completeness(o.Client, w.CaseId);
        var many = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Post(o.Client, w.CaseId, Body("ReturnedForCorrection", Fingerprint(latestState), LatestId(latestState)))));
        Assert.Equal(1, many.Count(r => r.StatusCode == HttpStatusCode.Created)); Assert.Equal(4, many.Count(r => r.StatusCode == HttpStatusCode.Conflict)); Assert.Equal(2, await Revisions(o.Org, w.CaseId));
    }

    // ---- database locks (deterministic coordination through pg_stat_activity) --------------------------------------------------------------------
    private async Task WaitForLockWaiter()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await using var connection = new NpgsqlConnection(scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetConnectionString());
        await connection.OpenAsync();
        for (var i = 0; i < 300; i++)
        {
            await using var command = new NpgsqlCommand("""SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND pid <> pg_backend_pid()""", connection);
            if ((long)(await command.ExecuteScalarAsync())! > 0) return;
            await Task.Delay(50);
        }
        Assert.Fail("No database session started waiting for a lock.");
    }
    private async Task<NpgsqlConnection> OpenConnection(IServiceScope scope)
    { var c = new NpgsqlConnection(scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetConnectionString()); await c.OpenAsync(); return c; }

    [Fact]
    public async Task A_review_waits_for_an_in_flight_catalogue_edit_and_then_sees_its_drift()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org);
        var state = await Completeness(o.Client, w.CaseId); var fp = Fingerprint(state);
        await using var scope = factory.Services.CreateAsyncScope(); await using var connection = await OpenConnection(scope); await using var tx = await connection.BeginTransactionAsync();
        await using (var update = new NpgsqlCommand("""UPDATE "Suppliers" SET "Email" = 'locked@example.test' WHERE "Id" = @id""", connection, tx)) { update.Parameters.AddWithValue("id", w.SupplierId); await update.ExecuteNonQueryAsync(); }

        var review = Post(o.Client, w.CaseId, Body("AcceptedAsCompleteUnderPolicy", fp, null)); // takes the case lock, then waits on the supplier row (FOR SHARE)
        await WaitForLockWaiter();
        Assert.False(review.IsCompleted, "The review must wait for the uncommitted catalogue update.");
        await tx.CommitAsync();
        var errors = await Failure(await review); // the edit committed first: the review sees the drift and refuses acceptance, with nothing inserted
        Assert.Contains("supplier:", Reason(errors)); Assert.Equal(0, await Revisions(o.Org, w.CaseId));
    }

    [Fact]
    public async Task A_catalogue_edit_waits_for_a_review_in_progress_and_a_review_waits_for_the_case_lock()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org, new Opt(Lot: false, Documents: false));
        var fp = Fingerprint(await Completeness(o.Client, w.CaseId));
        await using var scope = factory.Services.CreateAsyncScope(); await using var connection = await OpenConnection(scope); await using var tx = await connection.BeginTransactionAsync();
        await using (var lockCase = new NpgsqlCommand("""SELECT "Id" FROM "Cases" WHERE "Id" = @id FOR UPDATE""", connection, tx)) { lockCase.Parameters.AddWithValue("id", w.CaseId); await lockCase.ExecuteScalarAsync(); }
        var review = Post(o.Client, w.CaseId, Body("ReturnedForCorrection", fp, null));
        await WaitForLockWaiter();
        Assert.False(review.IsCompleted, "The review must wait for the case lock.");
        await tx.CommitAsync();
        Assert.Equal(HttpStatusCode.Created, (await review).StatusCode);
        Assert.Equal(1, await Revisions(o.Org, w.CaseId));
    }

    // ---- case-locked writers racing a review: both orders, forced through the PostgreSQL lock queue ------------------------------------------------
    // A raw transaction holds the case row lock; the first request is observed waiting on it (pg_stat_activity), then the second is started and observed waiting too.
    // Releasing the lock lets PostgreSQL grant it in queue order, so each order is deterministic without sleeps in production code or timing assumptions in the test.
    private async Task WaitForLockWaiters(int count)
    {
        await using var scope = factory.Services.CreateAsyncScope(); await using var connection = await OpenConnection(scope);
        for (var i = 0; i < 300; i++)
        {
            await using var command = new NpgsqlCommand("""SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND pid <> pg_backend_pid()""", connection);
            if ((long)(await command.ExecuteScalarAsync())! >= count) return;
            await Task.Delay(50);
        }
        Assert.Fail($"Fewer than {count} database session(s) started waiting for the case lock.");
    }
    private async Task<(HttpResponseMessage First, HttpResponseMessage Second)> Ordered(Guid caseId, Func<Task<HttpResponseMessage>> first, Func<Task<HttpResponseMessage>> second)
    {
        await using var scope = factory.Services.CreateAsyncScope(); await using var connection = await OpenConnection(scope); await using var tx = await connection.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("""SELECT "Id" FROM "Cases" WHERE "Id" = @id FOR UPDATE""", connection, tx)) { hold.Parameters.AddWithValue("id", caseId); await hold.ExecuteScalarAsync(); }
        var a = first(); await WaitForLockWaiters(1);
        var b = second(); await WaitForLockWaiters(2);
        Assert.False(a.IsCompleted || b.IsCompleted, "Both requests must be queued behind the case lock.");
        await tx.CommitAsync();
        return (await a, await b);
    }

    private sealed record Writer(World W, Func<Task<HttpResponseMessage>> Write, string[] Areas, Action<JsonElement> AssertPreWriteSnapshot);

    private async Task AssertSerializable(Func<Owner, Task<Writer>> prepare)
    {
        // Order A: the writer gets the case lock first. The review then sees a different fingerprint than the Owner loaded: 409, and nothing is recorded.
        {
            var o = await NewOwner(); var p = await prepare(o); var w = p.W; var fp = Fingerprint(await Completeness(o.Client, w.CaseId));
            var (write, review) = await Ordered(w.CaseId, p.Write, () => Post(o.Client, w.CaseId, Body("ReturnedForCorrection", fp, null)));
            Assert.True(write.IsSuccessStatusCode, "The writer must succeed.");
            var problem = await Problem(review, HttpStatusCode.Conflict); Assert.Equal("Dossier or review history changed since it was loaded", problem.GetProperty("detail").GetString());
            Assert.Equal(0, await Revisions(o.Org, w.CaseId)); Assert.Equal(0, await Count(o.Org, db => db.ReviewRecords.Where(x => x.EudrCaseId == w.CaseId)));
            Assert.Equal(0, await Count(o.Org, db => db.CaseRevisionDocuments.Where(x => x.EudrCaseId == w.CaseId)));
            Assert.Equal(0, await Count(o.Org, db => db.AuditEvents.Where(x => x.EudrCaseId == w.CaseId && x.EventType == "CaseReviewRecorded")));
            var after = await Completeness(o.Client, w.CaseId); Assert.NotEqual(fp, Fingerprint(after)); Assert.Equal("None", after.GetProperty("reviewFreshness").GetString());
            Assert.Equal(HttpStatusCode.Created, (await ReviewNow(o.Client, w.CaseId)).StatusCode); // reloading and reviewing the new state works
        }
        // Order B: the review gets the case lock first. It records the complete pre-write state; the writer commits afterwards and the review becomes Stale.
        {
            var o = await NewOwner(); var p = await prepare(o); var w = p.W; var fp = Fingerprint(await Completeness(o.Client, w.CaseId));
            var (review, write) = await Ordered(w.CaseId, () => Post(o.Client, w.CaseId, Body("ReturnedForCorrection", fp, null)), p.Write);
            var created = await Json(review, HttpStatusCode.Created); var reviewId = created.GetProperty("reviewId").GetGuid();
            Assert.True(write.IsSuccessStatusCode, "The writer must succeed after the review.");
            Assert.Equal(fp, created.GetProperty("fingerprint").GetString());
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                await using var db = TenantDb(scope, o.Org);
                var revision = await db.CaseRevisions.AsNoTracking().SingleAsync(x => x.EudrCaseId == w.CaseId);
                Assert.Equal(fp, revision.Fingerprint); Assert.Equal(1, revision.Number);
                Assert.Equal(1, await db.ReviewRecords.CountAsync(x => x.EudrCaseId == w.CaseId));
                Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.EudrCaseId == w.CaseId && x.EventType == "CaseReviewRecorded"));
            }
            p.AssertPreWriteSnapshot(await Json(await o.Client.GetAsync($"/api/eudr-cases/{w.CaseId}/reviews/{reviewId}"))); // the snapshot is the whole pre-write state, not a torn mix
            var state = await Completeness(o.Client, w.CaseId);
            Assert.Equal("Stale", state.GetProperty("reviewFreshness").GetString()); Assert.Equal(reviewId, LatestId(state)); Assert.NotEqual(fp, Fingerprint(state));
            Assert.Equal(p.Areas.Order(), state.GetProperty("latestReview").GetProperty("changedAreas").EnumerateArray().Select(x => x.GetString()!).Order());
        }
    }

    [Fact]
    public Task A_review_racing_a_master_data_refresh_is_serialized() => AssertSerializable(async o =>
    {
        var w = await Build(o.Client, o.Org, new Opt(Lot: false, Documents: false));
        var original = (await Json(await o.Client.GetAsync($"/api/suppliers/{w.SupplierId}"))).GetProperty("name").GetString()!;
        await Ok(o.Client.PutAsJsonAsync($"/api/suppliers/{w.SupplierId}", new { name = "Raced supplier", email = "raced@example.test", postalAddress = "Raced street", countryCode = "BR" })); // drift only: the fingerprint is unchanged until the refresh
        return new Writer(w, () => o.Client.PostAsync($"/api/eudr-cases/{w.CaseId}/master-data/refresh", null), ["MasterData"],
            detail => Assert.Equal(original, detail.GetProperty("snapshot").GetProperty("facts").GetProperty("masterData").GetProperty("supplier").GetProperty("name").GetString()));
    });

    [Fact]
    public Task A_review_racing_an_evidence_link_withdrawal_is_serialized() => AssertSerializable(async o =>
    {
        var w = await Build(o.Client, o.Org);
        return new Writer(w, () => o.Client.DeleteAsync($"/api/eudr-cases/{w.CaseId}/evidence-links/{w.Links[0]}"), ["Evidence"], detail =>
        {
            Assert.Equal(2, detail.GetProperty("snapshot").GetProperty("facts").GetProperty("evidence").GetProperty("evidenceLinks").GetArrayLength());
            Assert.Contains(w.Links[0], detail.GetProperty("pins").GetProperty("evidenceLinkIds").EnumerateArray().Select(x => x.GetGuid()));
        });
    });

    [Fact]
    public Task A_review_racing_a_document_upload_is_serialized() => AssertSerializable(async o =>
    {
        var w = await Build(o.Client, o.Org);
        return new Writer(w, () => { var body = Upload("raced.txt", "text/plain", "raced evidence"); return o.Client.PostAsync($"/api/eudr-cases/{w.CaseId}/documents", body); }, ["Requirements", "Evidence"], detail =>
        {
            Assert.Equal(2, detail.GetProperty("snapshot").GetProperty("facts").GetProperty("evidence").GetProperty("documents").GetArrayLength());
            Assert.Equal(2, detail.GetProperty("pins").GetProperty("documentIds").GetArrayLength());
        });
    });

    [Fact]
    public Task A_review_racing_a_shipment_edit_is_serialized() => AssertSerializable(async o =>
    {
        var w = await Build(o.Client, o.Org, new Opt(Lot: false, Documents: false));
        return new Writer(w, () => o.Client.PutAsJsonAsync($"/api/shipments/{w.ShipmentId}", new { referenceNumber = w.Reference, supplierId = w.SupplierId, productId = w.ProductId, quantity = 12, unit = "Kg" }), ["Shipment"],
            detail => Assert.Equal(10m, detail.GetProperty("snapshot").GetProperty("facts").GetProperty("shipment").GetProperty("quantity").GetDecimal()));
    });

    // ---- POST body status matrix (the Owner check runs before the body is parsed) -------------------------------------------------------------------
    [Fact]
    public async Task The_review_post_body_status_matrix_keeps_the_owner_check_ahead_of_body_parsing()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org, new Opt(Lot: false, Documents: false)); var member = await NewMember(o.Org);
        var url = $"/api/eudr-cases/{w.CaseId}/reviews"; var fp = Fingerprint(await Completeness(o.Client, w.CaseId));
        StringContent Json_(string text) => new(text, Encoding.UTF8, "application/json");

        // A Member is denied before anything about the body is examined.
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync(url, Json_(""))).StatusCode);                         // empty JSON body
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync(url, Json_("{ not json"))).StatusCode);                // malformed JSON
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync(url, new StringContent("hello", Encoding.UTF8, "text/plain"))).StatusCode); // unsupported media type
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync(url, null)).StatusCode);                                 // no body and no content type
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync(url, Json_("""{"decision":"Nonsense"}"""))).StatusCode); // unbindable enum

        // The Owner is told what is wrong with the request.
        Assert.Equal(HttpStatusCode.BadRequest, (await o.Client.PostAsync(url, Json_(""))).StatusCode);                          // application/json + empty body
        Assert.Equal(HttpStatusCode.BadRequest, (await o.Client.PostAsync(url, Json_("{ not json"))).StatusCode);                // application/json + malformed JSON
        Assert.Equal(HttpStatusCode.BadRequest, (await o.Client.PostAsync(url, Json_("[]"))).StatusCode);                        // valid JSON of the wrong shape
        Assert.Equal(HttpStatusCode.BadRequest, (await o.Client.PostAsync(url, Json_("""{"decision":"Nonsense","rationale":"r","expectedFingerprint":"a"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await o.Client.PostAsync(url, Json_($$"""{"decision":"ReturnedForCorrection","rationale":"  ","expectedFingerprint":"{{fp}}"}"""))).StatusCode); // valid JSON, validation errors
        Assert.Equal(HttpStatusCode.BadRequest, (await o.Client.PostAsync(url, Json_("""{"decision":"ReturnedForCorrection","rationale":"r","expectedFingerprint":"short"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await o.Client.PostAsync(url, new StringContent("hello", Encoding.UTF8, "text/plain"))).StatusCode); // genuinely unsupported media type
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await o.Client.PostAsync(url, null)).StatusCode);
        Assert.Equal(0, await Revisions(o.Org, w.CaseId));
    }

    [Fact]
    public async Task Facts_that_change_after_a_review_make_it_stale_and_name_the_changed_sections()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org); var c = o.Client;
        await Reviewed(c, w.CaseId); Assert.Equal("Current", await FreshnessOf(c, w.CaseId));

        // Same-status requirement re-review: same status and note, new ReviewedAtUtc.
        var requirement = (await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}"))).GetProperty("requirements").EnumerateArray().First(r => r.GetProperty("status").GetString() == "NotRequired").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await Patch(c, $"/api/eudr-cases/{w.CaseId}/requirements/{requirement}", new { status = "NotRequired", reviewerNote = "Not needed in this pilot case" })).StatusCode);
        Assert.Equal("Stale", await FreshnessOf(c, w.CaseId)); Assert.Equal(["Requirements"], await ChangedAreas(c, w.CaseId));

        await Reviewed(c, w.CaseId); Assert.Equal("Current", await FreshnessOf(c, w.CaseId));
        await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/applicability", Application(description: "A revised assessed description")));
        Assert.Equal(["Applicability"], await ChangedAreas(c, w.CaseId));

        await Reviewed(c, w.CaseId);
        await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/recipients", new { resolution = "NotApplicable", notApplicableBasis = "Stays with the operator", recipients = Array.Empty<object>() }));
        Assert.Equal(["Recipients"], await ChangedAreas(c, w.CaseId));

        await Reviewed(c, w.CaseId);
        await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/dossier/lot", new { lotReference = "LOT-NEW" + Guid.NewGuid().ToString("N")[..6], quantity = 10, unit = "Kg", contributions = new[] { new { producerId = w.ProducerId, quantity = 10, unit = "Kg", plots = new[] { new { productionPlotId = w.PlotId, plotRevisionId = w.RevisionId, productionStartDate = "2024-01-01", productionEndDate = "2024-06-30" } } } } })); // production period change via a new lot version
        Assert.Equal(["Lot"], await ChangedAreas(c, w.CaseId));

        await Reviewed(c, w.CaseId);
        await Ok(c.PutAsJsonAsync($"/api/suppliers/{w.SupplierId}", new { name = "Edited supplier", email = "edited@example.test", postalAddress = "Street 5, Sao Paulo", countryCode = "BR" }));
        Assert.Equal("Current", await FreshnessOf(c, w.CaseId)); // a catalogue edit alone is only drift
        await Ok(c.PostAsync($"/api/eudr-cases/{w.CaseId}/master-data/refresh", null));
        Assert.Equal("Stale", await FreshnessOf(c, w.CaseId)); Assert.Equal(["MasterData"], await ChangedAreas(c, w.CaseId));

        await Reviewed(c, w.CaseId);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/eudr-cases/{w.CaseId}/evidence-links/{w.Links[0]}")).StatusCode);
        Assert.Equal(["Evidence"], await ChangedAreas(c, w.CaseId));

        await Reviewed(c, w.CaseId);
        using (var body = Upload("another.txt", "text/plain", "more evidence")) await Ok(c.PostAsync($"/api/eudr-cases/{w.CaseId}/documents", body));
        Assert.Contains("Evidence", await ChangedAreas(c, w.CaseId)); // the upload also puts the requirement back to Submitted
        Assert.Contains("Requirements", await ChangedAreas(c, w.CaseId));

        await Reviewed(c, w.CaseId);
        using (var geo = Upload("plot2.geojson", "application/geo+json", """{"type":"Polygon","coordinates":[[[-48,-20],[-47,-20],[-47,-18],[-48,-20]]]}""")) await Ok(c.PostAsync($"/api/eudr-cases/{w.CaseId}/geolocation", geo));
        Assert.Contains("Evidence", await ChangedAreas(c, w.CaseId));

        Assert.Equal(8, await Revisions(o.Org, w.CaseId)); // one review per stage
    }

    [Fact]
    public async Task A_shipment_edit_makes_a_review_stale_in_the_shipment_section()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org, new Opt(Lot: false, Documents: false));
        await Reviewed(o.Client, w.CaseId);
        await Ok(o.Client.PutAsJsonAsync($"/api/shipments/{w.ShipmentId}", new { referenceNumber = w.Reference, supplierId = w.SupplierId, productId = w.ProductId, quantity = 11, unit = "Kg" }));
        Assert.Equal("Stale", await FreshnessOf(o.Client, w.CaseId)); Assert.Equal(["Shipment"], await ChangedAreas(o.Client, w.CaseId));
    }

    [Fact]
    public async Task Live_catalogue_labels_context_and_lifecycle_changes_do_not_stale_a_review()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org); var c = o.Client;
        var originalSupplierName = (await Json(await c.GetAsync($"/api/suppliers/{w.SupplierId}"))).GetProperty("name").GetString();
        await Reviewed(c, w.CaseId, "AcceptedAsCompleteUnderPolicy");
        async Task StillCurrent() { var s = await Completeness(c, w.CaseId); Assert.Equal("Current", s.GetProperty("reviewFreshness").GetString()); Assert.Empty(s.GetProperty("latestReview").GetProperty("changedAreas").EnumerateArray()); }

        await Ok(c.PutAsJsonAsync($"/api/suppliers/{w.SupplierId}", new { name = "Edited supplier", email = "edited@example.test", postalAddress = "Street 5, Sao Paulo", countryCode = "BR" }));
        await Ok(c.PutAsJsonAsync("/api/operator-profile", new { legalName = "Renamed operator", postalAddress = "Elsewhere 2", email = "new@example.test", establishmentCountryCode = "HR" }));
        var drifted = await Completeness(c, w.CaseId); Assert.Contains(drifted.GetProperty("advisories").EnumerateArray(), a => a.GetProperty("code").GetString() == "CATALOGUE_DRIFT_SUPPLIER");
        Assert.Contains(drifted.GetProperty("advisories").EnumerateArray(), a => a.GetProperty("code").GetString() == "CATALOGUE_DRIFT_OPERATOR");
        await StillCurrent();
        await Ok(c.PutAsJsonAsync($"/api/producers/{w.ProducerId}", new { name = "Renamed producer", countryCode = "BR", isActive = false }));
        await Ok(c.PutAsJsonAsync($"/api/production-plots/{w.PlotId}", new { name = "Renamed plot", isActive = true }));
        await StillCurrent();
        var newRevision = (await Json(await c.PostAsync($"/api/production-plots/{w.PlotId}/revisions", Raw($$"""{"countryCode":"BR","geometry":{{Bigger}},"areaHectares":null,"note":null}""")), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await Ok(c.PutAsJsonAsync($"/api/production-plots/{w.PlotId}", new { name = "Deactivated plot", isActive = false })); await StillCurrent();
        var superseded = await Completeness(c, w.CaseId); Assert.Contains(superseded.GetProperty("advisories").EnumerateArray(), a => a.GetProperty("code").GetString() == "PINNED_REVISION_SUPERSEDED");
        await StillCurrent();
        // A context-only refresh creates a new working copy but not a new fingerprint.
        await Ok(c.PutAsJsonAsync($"/api/suppliers/{w.SupplierId}", new { name = originalSupplierName, email = "supplier@example.test", postalAddress = "Street 5, Sao Paulo", countryCode = "BR" }));
        await Ok(c.PutAsJsonAsync("/api/operator-profile", new { legalName = "Operator d.o.o.", postalAddress = "Ilica 1, Zagreb", email = "operator@example.test", establishmentCountryCode = "HR" }));
        await Ok(c.PutAsJsonAsync($"/api/products/{w.ProductId}", new { name = "Context rename", cnCode = "0901", commodityType = "Coffee", description = "Another catalogue text" }));
        Assert.Equal(1, (await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}/master-data-history"))).GetArrayLength());
        await Ok(c.PostAsync($"/api/eudr-cases/{w.CaseId}/master-data/refresh", null));
        Assert.Equal(2, (await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}/master-data-history"))).GetArrayLength());
        await StillCurrent();
        // Exports and the supplier-request lifecycle (and the resulting case status) are not facts either.
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/eudr-cases/{w.CaseId}/export")).StatusCode);
        await Ok(c.PostAsync($"/api/eudr-cases/{w.CaseId}/supplier-request", null));
        await StillCurrent();
        Assert.NotEqual(Guid.Empty, newRevision);
    }

    [Fact]
    public async Task A_policy_version_difference_is_informational_and_a_closed_case_keeps_a_current_review_current()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org); var c = o.Client;
        await Ok(c.PostAsync($"/api/eudr-cases/{w.CaseId}/supplier-request", null)); // the request puts the case into ReadyForExport
        var review = await Reviewed(c, w.CaseId, "AcceptedAsCompleteUnderPolicy");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<AppDbContext>(); var id = review.GetProperty("caseRevisionId").GetGuid(); var reviewId = review.GetProperty("reviewId").GetGuid();
            await admin.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "CaseRevisions" SET "PolicyVersion" = '0' WHERE "Id" = {id}""");   // simulate a review made under an older policy
            await admin.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "ReviewRecords" SET "PolicyVersion" = '0' WHERE "Id" = {reviewId}""");
        }
        var changed = await Completeness(c, w.CaseId);
        Assert.Equal("Current", changed.GetProperty("reviewFreshness").GetString()); Assert.True(changed.GetProperty("latestReview").GetProperty("policyChanged").GetBoolean()); Assert.Equal("0", changed.GetProperty("latestReview").GetProperty("policyVersion").GetString());

        Assert.Equal("ReadyForExport", (await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}"))).GetProperty("status").GetString());
        await LifecycleTestSupport.SetStatus(factory.Services, w.CaseId, Provena.Api.Domain.EudrCaseStatus.Closed); // Closing arrives with the lifecycle routes (Slice 2); the review must stay Current on a Closed case.
        await Ok(c.PutAsJsonAsync($"/api/suppliers/{w.SupplierId}", new { name = "Edited after closure", countryCode = "BR" }));
        await Ok(c.PutAsJsonAsync($"/api/producers/{w.ProducerId}", new { name = "Renamed after closure", countryCode = "BR", isActive = true }));
        await Ok(c.PostAsync($"/api/production-plots/{w.PlotId}/revisions", Raw($$"""{"countryCode":"BR","geometry":{{Bigger}},"areaHectares":null,"note":null}""")), HttpStatusCode.Created);
        var closed = await Completeness(c, w.CaseId);
        Assert.Equal("Current", closed.GetProperty("reviewFreshness").GetString());
        Assert.All(closed.GetProperty("advisories").EnumerateArray(), a => Assert.Contains("after case closure", a.GetProperty("message").GetString()));
        await Problem(await Post(c, w.CaseId, Body("ReturnedForCorrection", Fingerprint(closed), LatestId(closed))), HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/eudr-cases/{w.CaseId}/reviews")).StatusCode);
    }

    [Fact]
    public async Task A_second_review_becomes_the_latest_and_the_first_stays_readable_and_unchanged()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org, new Opt(Lot: false, Documents: false)); var c = o.Client;
        var first = await Reviewed(c, w.CaseId); var firstId = first.GetProperty("reviewId").GetGuid();
        await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/recipients", new { resolution = "NotApplicable", notApplicableBasis = "Stays with the operator", recipients = Array.Empty<object>() }));
        Assert.Equal("Stale", await FreshnessOf(c, w.CaseId));
        var firstBefore = await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}/reviews/{firstId}"));

        var second = await Reviewed(c, w.CaseId, "ConfirmedUnsupportedFlow".Replace("ConfirmedUnsupportedFlow", "ReturnedForCorrection"));
        var state = await Completeness(c, w.CaseId);
        Assert.Equal("Current", state.GetProperty("reviewFreshness").GetString()); Assert.Equal(second.GetProperty("reviewId").GetGuid(), LatestId(state)); Assert.Equal(2, second.GetProperty("revisionNumber").GetInt32());
        var list = (await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}/reviews"))).EnumerateArray().ToList();
        Assert.Equal([2, 1], list.Select(x => x.GetProperty("revisionNumber").GetInt32()).ToArray()); Assert.Equal(firstId, list[1].GetProperty("reviewId").GetGuid());
        Assert.Equal(firstBefore.GetRawText(), (await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}/reviews/{firstId}"))).GetRawText()); // immutable
        AssertNoOrganizationId(JsonDocument.Parse(JsonSerializer.Serialize(list)).RootElement);
    }

    [Fact]
    public async Task Latest_is_decided_by_revision_number_even_when_review_timestamps_collide()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org, new Opt(Lot: false, Documents: false)); var c = o.Client;
        var first = await Reviewed(c, w.CaseId); var second = await Reviewed(c, w.CaseId);
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync($"""UPDATE "ReviewRecords" SET "ReviewedAtUtc" = TIMESTAMPTZ '2026-01-01 00:00:00+00' WHERE "EudrCaseId" = {w.CaseId}""");
        var state = await Completeness(c, w.CaseId);
        Assert.Equal(second.GetProperty("reviewId").GetGuid(), LatestId(state)); Assert.NotEqual(first.GetProperty("reviewId").GetGuid(), LatestId(state));
        var list = (await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}/reviews"))).EnumerateArray().ToList();
        Assert.Equal([2, 1], list.Select(x => x.GetProperty("revisionNumber").GetInt32()).ToArray());
    }

    // ---- snapshot immutability --------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_stored_review_does_not_change_when_the_live_catalogue_labels_and_evidence_change_afterwards()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org); var c = o.Client;
        var review = await Reviewed(c, w.CaseId, "AcceptedAsCompleteUnderPolicy"); var reviewId = review.GetProperty("reviewId").GetGuid();
        var before = await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}/reviews/{reviewId}"));
        var snapshot = before.GetProperty("snapshot");
        Assert.Equal(w.Reference, snapshot.GetProperty("facts").GetProperty("shipment").GetProperty("reference").GetString());
        Assert.Equal("Supplier ", snapshot.GetProperty("facts").GetProperty("masterData").GetProperty("supplier").GetProperty("name").GetString()![..9]);
        Assert.Equal("Catalogue text", snapshot.GetProperty("context").GetProperty("productCatalogueDescription").GetString());
        Assert.StartsWith("Product ", snapshot.GetProperty("context").GetProperty("productName").GetString()); Assert.StartsWith("Producer ", snapshot.GetProperty("context").GetProperty("producers")[0].GetProperty("name").GetString());
        Assert.StartsWith("Plot ", snapshot.GetProperty("context").GetProperty("plots")[0].GetProperty("name").GetString());
        Assert.False(snapshot.GetProperty("context").GetProperty("pinnedRevisions")[0].GetProperty("supersededAtReviewTime").GetBoolean());
        Assert.Equal("provena-pilot-completeness", snapshot.GetProperty("completeness").GetProperty("policyId").GetString()); Assert.NotEmpty(snapshot.GetProperty("completeness").GetProperty("items").EnumerateArray());
        Assert.Equal(1, snapshot.GetProperty("schemaVersion").GetInt32()); AssertNoOrganizationId(before);

        await Ok(c.PutAsJsonAsync($"/api/suppliers/{w.SupplierId}", new { name = "Totally different supplier", email = "x@example.test", postalAddress = "Elsewhere", countryCode = "AR" }));
        await Ok(c.PutAsJsonAsync($"/api/products/{w.ProductId}", new { name = "Renamed product", cnCode = "0902", commodityType = "Cocoa", description = "Rewritten catalogue text" }));
        await Ok(c.PutAsJsonAsync("/api/operator-profile", new { legalName = "Another operator", postalAddress = "Elsewhere 9", email = "other@example.test", establishmentCountryCode = "DE" }));
        await Ok(c.PutAsJsonAsync($"/api/producers/{w.ProducerId}", new { name = "Renamed producer", countryCode = "AR", isActive = false }));
        await Ok(c.PutAsJsonAsync($"/api/production-plots/{w.PlotId}", new { name = "Renamed plot", isActive = true }));
        await Ok(c.PostAsync($"/api/production-plots/{w.PlotId}/revisions", Raw($$"""{"countryCode":"BR","geometry":{{Bigger}},"areaHectares":null,"note":null}""")), HttpStatusCode.Created);
        await Ok(c.PutAsJsonAsync($"/api/production-plots/{w.PlotId}", new { name = "Deactivated plot", isActive = false }));
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/eudr-cases/{w.CaseId}/documents/{w.Docs[0]}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/eudr-cases/{w.CaseId}/evidence-links/{w.Links[1]}")).StatusCode);

        var after = await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}/reviews/{reviewId}"));
        Assert.Equal(before.GetRawText(), after.GetRawText());
        Assert.Equal(w.Docs.Order(), after.GetProperty("pins").GetProperty("documentIds").EnumerateArray().Select(x => x.GetGuid()).Order()); // the withdrawn document is still pinned
        Assert.Equal(w.Links.Order(), after.GetProperty("pins").GetProperty("evidenceLinkIds").EnumerateArray().Select(x => x.GetGuid()).Order());
        Assert.Equal(2, after.GetProperty("snapshot").GetProperty("facts").GetProperty("evidence").GetProperty("documents").GetArrayLength());
        Assert.Equal("Stale", await FreshnessOf(c, w.CaseId)); // only the evidence change is a fact change
    }

    // ---- reads --------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Review_reads_are_tenant_and_case_scoped_and_have_the_documented_shape()
    {
        var a = await NewOwner(); var b = await NewOwner(); var wa = await Build(a.Client, a.Org, new Opt(Lot: false, Documents: false)); var wa2 = await Build(a.Client, a.Org, new Opt(Lot: false, Documents: false)); var wb = await Build(b.Client, b.Org, new Opt(Lot: false, Documents: false));
        var review = await Reviewed(a.Client, wa.CaseId); var reviewId = review.GetProperty("reviewId").GetGuid();
        Assert.Empty((await Json(await a.Client.GetAsync($"/api/eudr-cases/{wa2.CaseId}/reviews"))).EnumerateArray()); // another case: no reviews
        await Problem(await a.Client.GetAsync($"/api/eudr-cases/{wa2.CaseId}/reviews/{reviewId}"), HttpStatusCode.NotFound); // a review of another case
        await Problem(await a.Client.GetAsync($"/api/eudr-cases/{wa2.CaseId}/reviews/{reviewId}/export"), HttpStatusCode.NotFound);
        await Problem(await a.Client.GetAsync($"/api/eudr-cases/{wa.CaseId}/reviews/{Guid.NewGuid()}"), HttpStatusCode.NotFound);
        await Problem(await b.Client.GetAsync($"/api/eudr-cases/{wa.CaseId}/reviews"), HttpStatusCode.NotFound); // foreign case
        await Problem(await b.Client.GetAsync($"/api/eudr-cases/{wa.CaseId}/reviews/{reviewId}"), HttpStatusCode.NotFound);
        await Problem(await b.Client.GetAsync($"/api/eudr-cases/{wb.CaseId}/reviews/{reviewId}"), HttpStatusCode.NotFound); // foreign review under the caller's own case
        await Problem(await b.Client.GetAsync($"/api/eudr-cases/{wa.CaseId}/reviews/{reviewId}/export"), HttpStatusCode.NotFound);
        using var anonymous = factory.CreateClient();
        foreach (var path in new[] { $"/api/eudr-cases/{wa.CaseId}/reviews", $"/api/eudr-cases/{wa.CaseId}/reviews/{reviewId}", $"/api/eudr-cases/{wa.CaseId}/reviews/{reviewId}/export" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await a.Client.DeleteAsync($"/api/eudr-cases/{wa.CaseId}/reviews/{reviewId}")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await a.Client.PutAsJsonAsync($"/api/eudr-cases/{wa.CaseId}/reviews/{reviewId}", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await a.Client.DeleteAsync($"/api/eudr-cases/{wa.CaseId}/reviews")).StatusCode);

        var summary = (await Json(await a.Client.GetAsync($"/api/eudr-cases/{wa.CaseId}/reviews")))[0];
        Assert.Equal(["reviewId", "caseRevisionId", "revisionNumber", "decision", "reviewerUserId", "reviewedAtUtc", "policyId", "policyVersion", "fingerprint", "fingerprintVersion"], summary.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(JsonValueKind.String, summary.GetProperty("decision").ValueKind);
        var detail = await Json(await a.Client.GetAsync($"/api/eudr-cases/{wa.CaseId}/reviews/{reviewId}"));
        Assert.Equal(["reviewId", "caseRevisionId", "revisionNumber", "decision", "rationale", "reviewerUserId", "reviewedAtUtc", "policyId", "policyVersion", "snapshotSchemaVersion", "fingerprintVersion", "fingerprint", "sectionHashes", "pins", "snapshot"],
            detail.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(["caseDossierId", "caseLotId", "caseApplicabilityId", "caseRecipientStatementId", "caseMasterDataVersionId", "geoSubmissionId", "supplierSubmissionId", "documentIds", "evidenceLinkIds"], detail.GetProperty("pins").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(["schemaVersion", "facts", "context", "completeness"], detail.GetProperty("snapshot").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("pins").GetProperty("caseLotId").ValueKind); // no dossier existed: both lot pins are null
        AssertNoOrganizationId(detail);
        var latest = (await Completeness(a.Client, wa.CaseId)).GetProperty("latestReview");
        Assert.Equal(["reviewId", "caseRevisionId", "decision", "reviewedAtUtc", "policyVersion", "policyChanged", "changedAreas"], latest.EnumerateObject().Select(p => p.Name).ToArray());
    }

    // ---- reviewed export ---------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_reviewed_export_contains_exactly_the_pinned_immutable_material_and_survives_later_changes()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org); var c = o.Client;
        var review = await Reviewed(c, w.CaseId, "AcceptedAsCompleteUnderPolicy"); var reviewId = review.GetProperty("reviewId").GetGuid();
        var export = await c.GetAsync($"/api/eudr-cases/{w.CaseId}/reviews/{reviewId}/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode); Assert.Equal("application/zip", export.Content.Headers.ContentType?.MediaType); Assert.Equal("attachment", export.Content.Headers.ContentDisposition?.DispositionType);
        var files = await ReadZip(export); var root = $"case-{w.Reference}-review-1/";
        Assert.All(files.Keys, k => Assert.StartsWith(root, k));
        using var json = JsonDocument.Parse(files[root + "review.json"]); var r = json.RootElement;
        AssertNoOrganizationId(r);
        Assert.Equal(reviewId, r.GetProperty("reviewId").GetGuid()); Assert.Equal("AcceptedAsCompleteUnderPolicy", r.GetProperty("decision").GetString()); Assert.Equal("Reviewed against the recorded facts.", r.GetProperty("rationale").GetString());
        Assert.Equal(o.UserId, r.GetProperty("reviewerUserId").GetGuid()); Assert.Equal("provena-pilot-completeness", r.GetProperty("policyId").GetString()); Assert.Equal(review.GetProperty("fingerprint").GetString(), r.GetProperty("fingerprint").GetString());
        Assert.Equal(1, r.GetProperty("fingerprintVersion").GetInt32()); Assert.Equal(1, r.GetProperty("snapshotSchemaVersion").GetInt32()); Assert.Equal(JsonValueKind.String, r.GetProperty("exportedAtUtc").ValueKind);
        Assert.Contains("not a legal compliance determination, risk assessment or DDS", r.GetProperty("notice").GetString());
        var detail = await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}/reviews/{reviewId}"));
        Assert.Equal(JsonSerializer.Serialize(detail.GetProperty("snapshot")), JsonSerializer.Serialize(r.GetProperty("snapshot"))); // the stored snapshot, verbatim

        Assert.Equal(2, files.Keys.Count(k => k.StartsWith(root + "documents/"))); Assert.Contains(root + "geolocation/original.geojson", files.Keys);
        var pinnedRevisions = r.GetProperty("snapshot").GetProperty("facts").GetProperty("lot").GetProperty("contributions")[0].GetProperty("plots").EnumerateArray().Select(p => p.GetProperty("revisionId").GetGuid()).ToList();
        Assert.Equal([w.RevisionId], pinnedRevisions); Assert.Contains(root + $"plots/{w.RevisionId:N}.geojson", files.Keys);
        Assert.Equal(Encoding.UTF8.GetString(files[root + $"plots/{w.RevisionId:N}.geojson"]).Replace(" ", ""), JsonDocument.Parse(files[root + $"plots/{w.RevisionId:N}.geojson"]).RootElement.GetRawText().Replace(" ", ""));
        var oldBytes = files.Where(k => k.Key.StartsWith(root + "documents/")).ToDictionary(k => k.Key, k => k.Value);
        var supplierName = r.GetProperty("snapshot").GetProperty("facts").GetProperty("masterData").GetProperty("supplier").GetProperty("name").GetString();

        // A: a document withdrawn after the review is still in the reviewed export.
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/eudr-cases/{w.CaseId}/documents/{w.Docs[0]}")).StatusCode);
        // B: a newer plot revision supersedes the reviewed one; the export keeps the reviewed geometry and does not add the new one.
        var newRevision = (await Json(await c.PostAsync($"/api/production-plots/{w.PlotId}/revisions", Raw($$"""{"countryCode":"BR","geometry":{{Bigger}},"areaHectares":null,"note":null}""")), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        // C: the live catalogue changes.
        await Ok(c.PutAsJsonAsync($"/api/suppliers/{w.SupplierId}", new { name = "Totally different supplier", email = "x@example.test", postalAddress = "Elsewhere", countryCode = "AR" }));
        await Ok(c.PutAsJsonAsync("/api/operator-profile", new { legalName = "Another operator", postalAddress = "Elsewhere 9", email = "other@example.test", establishmentCountryCode = "DE" }));
        var again = await ReadZip(await c.GetAsync($"/api/eudr-cases/{w.CaseId}/reviews/{reviewId}/export"));
        Assert.Equal(oldBytes.Keys.Order(), again.Keys.Where(k => k.StartsWith(root + "documents/")).Order());
        foreach (var (name, bytes) in oldBytes) Assert.Equal(bytes, again[name]);
        Assert.Contains(root + $"plots/{w.RevisionId:N}.geojson", again.Keys); Assert.DoesNotContain(root + $"plots/{newRevision:N}.geojson", again.Keys);
        using var second = JsonDocument.Parse(again[root + "review.json"]);
        Assert.Equal(JsonSerializer.Serialize(r.GetProperty("snapshot")), JsonSerializer.Serialize(second.RootElement.GetProperty("snapshot")));
        Assert.Equal(supplierName, second.RootElement.GetProperty("snapshot").GetProperty("facts").GetProperty("masterData").GetProperty("supplier").GetProperty("name").GetString());
        Assert.DoesNotContain("Totally different supplier", Encoding.UTF8.GetString(again[root + "review.json"])); Assert.DoesNotContain("Another operator", Encoding.UTF8.GetString(again[root + "review.json"]));
        Assert.Equal(r.GetProperty("fingerprint").GetString(), second.RootElement.GetProperty("fingerprint").GetString());

        // D: the working export reflects the current dossier and its review freshness; the reviewed export reflects only the review.
        var working = await c.GetAsync($"/api/eudr-cases/{w.CaseId}/export");
        var workingFiles = await ReadZip(working);
        using var manifest = JsonDocument.Parse(workingFiles.Single(f => f.Key.EndsWith("/manifest.json")).Value);
        Assert.Equal(reviewId, manifest.RootElement.GetProperty("latestReview").GetProperty("reviewId").GetGuid()); Assert.Equal("Stale", manifest.RootElement.GetProperty("completeness").GetProperty("reviewFreshness").GetString());
        Assert.Equal("Working-current export. Not the reviewed record; use the review export for what was reviewed.", manifest.RootElement.GetProperty("reviewScope").GetString());
        Assert.DoesNotContain(workingFiles.Keys, k => k.Contains("-review-"));

        var audits = await Count(o.Org, db => db.AuditEvents.Where(x => x.EudrCaseId == w.CaseId && x.EventType == "CaseReviewExported"));
        Assert.Equal(2, audits);
        await using var scope = factory.Services.CreateAsyncScope(); await using var tdb = TenantDb(scope, o.Org);
        Assert.All(await tdb.AuditEvents.AsNoTracking().Where(x => x.EventType == "CaseReviewExported").ToListAsync(), e => { Assert.Contains(reviewId.ToString(), e.Description); Assert.DoesNotContain("example.test", e.Description); Assert.DoesNotContain("Supplier", e.Description); });
    }

    [Fact]
    public async Task The_reviewed_export_of_a_review_without_a_dossier_is_valid_and_members_can_download_it()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org, new Opt(Lot: false, Documents: false));
        var review = await Reviewed(o.Client, w.CaseId); var member = await NewMember(o.Org);
        var response = await member.GetAsync($"/api/eudr-cases/{w.CaseId}/reviews/{review.GetProperty("reviewId").GetGuid()}/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var files = await ReadZip(response); var root = $"case-{w.Reference}-review-1/";
        Assert.Equal([root + "review.json"], files.Keys.ToArray()); // no documents, plots or geolocation were reviewed
        using var json = JsonDocument.Parse(files[root + "review.json"]);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("snapshot").GetProperty("facts").GetProperty("lot").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/eudr-cases/{w.CaseId}/reviews")).StatusCode);
    }

    [Fact]
    public async Task The_openapi_document_describes_the_review_request_and_the_string_enums()
    {
        using var anonymous = factory.CreateClient();
        var document = await Json(await anonymous.GetAsync("/openapi/v1.json"), HttpStatusCode.OK);
        var post = document.GetProperty("paths").GetProperty("/api/eudr-cases/{caseId}/reviews").GetProperty("post");
        Assert.True(post.TryGetProperty("security", out _));
        var schemas = document.GetProperty("components").GetProperty("schemas");
        var input = post.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(["decision", "expectedFingerprint", "expectedLatestReviewId", "rationale"], input);
        Assert.Equal(["AcceptedAsCompleteUnderPolicy", "ConfirmedOutsideRegulatoryScope", "ConfirmedUnsupportedFlow", "ReturnedForCorrection"], schemas.GetProperty("ReviewDecision").GetProperty("enum").EnumerateArray().Select(x => x.GetString()!).Order().ToArray());
        Assert.Equal(["Current", "None", "Stale"], schemas.GetProperty("ReviewFreshness").GetProperty("enum").EnumerateArray().Select(x => x.GetString()!).Order().ToArray());
        Assert.DoesNotContain("organizationId", schemas.GetProperty("ReviewDetailDto").GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("organizationId", schemas.GetProperty("LatestReviewDto").GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_working_export_carries_the_latest_review_and_keeps_its_existing_contract()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org);
        async Task<JsonDocument> Manifest() => JsonDocument.Parse((await ReadZip(await o.Client.GetAsync($"/api/eudr-cases/{w.CaseId}/export"))).Single(f => f.Key.EndsWith("/manifest.json")).Value);
        using (var none = await Manifest()) { Assert.Equal(JsonValueKind.Null, none.RootElement.GetProperty("latestReview").ValueKind); Assert.Equal("None", none.RootElement.GetProperty("completeness").GetProperty("reviewFreshness").GetString()); }
        var review = await Reviewed(o.Client, w.CaseId);
        using var withReview = await Manifest(); var root = withReview.RootElement;
        Assert.Equal(review.GetProperty("reviewId").GetGuid(), root.GetProperty("latestReview").GetProperty("reviewId").GetGuid()); Assert.Equal("Current", root.GetProperty("completeness").GetProperty("reviewFreshness").GetString());
        Assert.Equal(JsonSerializer.Serialize(root.GetProperty("completeness").GetProperty("latestReview")), JsonSerializer.Serialize(root.GetProperty("latestReview")));
        Assert.StartsWith("Current working evidence at export time.", root.GetProperty("evidenceScope").GetString()); Assert.True(root.TryGetProperty("dossier", out _)); Assert.True(root.TryGetProperty("dossierScope", out _));
        Assert.Equal("Working-current export. Not the reviewed record; use the review export for what was reviewed.", root.GetProperty("reviewScope").GetString());
    }
}
