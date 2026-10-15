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

// Post-completion Concern & Corrective Action v1 over real HTTP and PostgreSQL: concerns only on Completed or archived cases, actions, notifications, the Owner-only resolution, the
// concurrency guarantees under the case lock and the isolation of the completed case (status, review, closure, External DDS record). Each scenario provisions its own organization.
[Collection("Api")]
public sealed class ConcernTests(ApiFactory factory)
{
    private const string Password = "Test-Only-Password-928!";
    private sealed record Owner(HttpClient Client, Guid Org, Guid UserId);

    // ---- fixtures -------------------------------------------------------------------------------------------------------------------------
    private async Task<Owner> NewOwner()
    {
        var name = "Cn-" + Guid.NewGuid().ToString("N")[..8];
        await using var scope = factory.Services.CreateAsyncScope();
        var token = await scope.ServiceProvider.GetRequiredService<AuthService>().Register(new RegisterRequest($"{name}@example.test", Password, "Test", "User", "Org " + name, null), CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Organizations.Where(x => x.Name == "Org " + name).Select(x => x.Id).SingleAsync();
        var user = await db.OrganizationUsers.Where(x => x.OrganizationId == org).Select(x => x.UserId).SingleAsync();
        return new(Client(token.AccessToken), org, user);
    }
    private HttpClient Client(string token) { var c = factory.CreateClient(); c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); return c; }
    private async Task<HttpClient> NewMember(Guid org) => (await NewMemberWithId(org)).Client;
    private async Task<(HttpClient Client, Guid UserId)> NewMemberWithId(Guid org)
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
    private async Task Mutate(Guid org, Func<AppDbContext, Task> change) { await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org); await change(db); await db.SaveChangesAsync(); }
    private Task<int> Audits(Guid org, Guid caseId, string type) => Query(org, db => db.AuditEvents.CountAsync(x => x.EudrCaseId == caseId && x.EventType == type));

    private static string Url(Guid caseId, Guid? concernId = null, string? tail = null) => $"/api/eudr-cases/{caseId}/concerns" + (concernId == null ? "" : $"/{concernId}") + (tail == null ? "" : "/" + tail);
    private static object ConcernBody(string? summary = "Supplier reported new information", string? details = "A new deforestation alert was reported for the sourcing region.", string category = "NewInformation", DateTimeOffset? raisedAt = null)
        => new { category, summary, details, raisedAtUtc = raisedAt ?? DateTimeOffset.UtcNow.AddMinutes(-5) };
    private static object ActionBody(string? description = "Quarantined the remaining stock.", DateTimeOffset? performedAt = null, Guid? evidence = null)
        => new { description, performedAtUtc = performedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1), evidenceDocumentId = evidence };
    private static object NotificationBody(Guid? recipient, string channel = "Email", DateTimeOffset? notifiedAt = null, string? note = null, Guid? evidence = null)
        => new { recipientId = recipient, channel, notifiedAtUtc = notifiedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1), note, evidenceDocumentId = evidence };
    private static object ResolutionBody(string outcome = "CorrectiveActionCompleted", string? rationale = "All affected parties were informed.") => new { outcome, rationale };
    private static async Task<JsonElement> Problem(HttpResponseMessage response, HttpStatusCode status)
    { var json = await Json(response, status); Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType); return json; }
    private static string Reason(JsonElement problem) => problem.GetProperty("errors").GetProperty("reason").GetString()!;

    // A case that is Completed (status Closed): the lifecycle machinery is covered elsewhere, so the status is set directly. Documents and a recipient exist (Lot is not needed).
    private async Task<(Owner Owner, World World)> Completed()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org, new Opt(Lot: false));
        await LifecycleTestSupport.SetStatus(factory.Services, w.CaseId, EudrCaseStatus.Closed);
        return (o, w);
    }
    private async Task<Guid> Concern(HttpClient c, Guid caseId, object? body = null)
        => (await Json(await c.PostAsJsonAsync(Url(caseId), body ?? ConcernBody()), HttpStatusCode.Created)).GetProperty("concernId").GetGuid();
    private Task<Guid> RecipientId(Guid org, Guid caseId) => Query(org, db => db.CaseRecipients.Where(r => db.CaseRecipientStatements.Any(s => s.Id == r.CaseRecipientStatementId && s.EudrCaseId == caseId)).Select(r => r.Id).FirstAsync());
    private static async Task<JsonElement> Detail(HttpClient c, Guid caseId, Guid concernId) => await Json(await c.GetAsync(Url(caseId, concernId)), HttpStatusCode.OK);

    // ---- creating a concern ------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_concern_is_accepted_for_a_completed_case_and_leaves_the_case_status_alone()
    {
        var (o, w) = await Completed(); var c = o.Client;
        var response = await c.PostAsJsonAsync(Url(w.CaseId), ConcernBody("  Alert received  ", "  Details of the alert.  "));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await Json(response);
        Assert.EndsWith($"/concerns/{created.GetProperty("concernId").GetGuid()}", response.Headers.Location!.AbsolutePath);
        Assert.Equal(1, created.GetProperty("number").GetInt32()); Assert.Equal("Alert received", created.GetProperty("summary").GetString()); Assert.Equal("Details of the alert.", created.GetProperty("details").GetString());
        Assert.Equal("Open", created.GetProperty("status").GetString()); Assert.Equal("NewInformation", created.GetProperty("category").GetString()); Assert.Equal(o.UserId, created.GetProperty("recordedByUserId").GetGuid());
        Assert.Empty(created.GetProperty("actions").EnumerateArray()); Assert.Empty(created.GetProperty("notifications").EnumerateArray()); Assert.Equal(JsonValueKind.Null, created.GetProperty("resolution").ValueKind);
        AssertNoOrganizationId(created);
        Assert.Equal("Closed", (await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}"))).GetProperty("status").GetString()); // still completed
        Assert.Equal(1, await Audits(o.Org, w.CaseId, "ConcernRecorded"));
        var audit = await Query(o.Org, db => db.AuditEvents.Where(x => x.EventType == "ConcernRecorded" && x.EudrCaseId == w.CaseId).Select(x => x.Description).SingleAsync());
        Assert.DoesNotContain("Details of the alert", audit); Assert.DoesNotContain("Alert received", audit); Assert.Contains(created.GetProperty("concernId").GetString()!, audit);
    }

    [Fact]
    public async Task A_concern_is_accepted_for_an_archived_case_and_refused_for_an_editable_case()
    {
        var o = await NewOwner(); var archived = await Build(o.Client, o.Org, new Opt(Lot: false)); var open = await Build(o.Client, o.Org, new Opt(Lot: false));
        await LifecycleTestSupport.Archive(factory.Services, archived.CaseId);
        Assert.Equal(HttpStatusCode.Created, (await o.Client.PostAsJsonAsync(Url(archived.CaseId), ConcernBody())).StatusCode);
        Assert.Equal("Draft", (await Json(await o.Client.GetAsync($"/api/eudr-cases/{archived.CaseId}"))).GetProperty("status").GetString()); // archive is independent; the status is untouched
        var refused = await Problem(await o.Client.PostAsJsonAsync(Url(open.CaseId), ConcernBody()), HttpStatusCode.Conflict);
        Assert.Equal("CaseNotCompleted", Reason(refused));
        Assert.Equal(0, await Query(o.Org, db => db.CaseConcerns.CountAsync(x => x.EudrCaseId == open.CaseId)));
        // An unarchived, never-closed case is editable again and refuses a concern.
        await LifecycleTestSupport.Unarchive(factory.Services, archived.CaseId);
        Assert.Equal("CaseNotCompleted", Reason(await Problem(await o.Client.PostAsJsonAsync(Url(archived.CaseId), ConcernBody()), HttpStatusCode.Conflict)));
    }

    [Fact]
    public async Task Any_member_may_record_a_concern_while_tenants_anonymous_callers_and_other_verbs_are_refused()
    {
        var (o, w) = await Completed(); var member = await NewMember(o.Org); var other = await NewOwner(); using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Created, (await member.PostAsJsonAsync(Url(w.CaseId), ConcernBody())).StatusCode);
        var concernId = await Concern(o.Client, w.CaseId);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync(Url(w.CaseId))).StatusCode); Assert.Equal(HttpStatusCode.OK, (await member.GetAsync(Url(w.CaseId, concernId))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Url(w.CaseId))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Url(w.CaseId), ConcernBody())).StatusCode);
        // Another organization sees nothing: 404 for reads and writes, on the case and on the concern.
        await Problem(await other.Client.GetAsync(Url(w.CaseId)), HttpStatusCode.NotFound); await Problem(await other.Client.GetAsync(Url(w.CaseId, concernId)), HttpStatusCode.NotFound);
        await Problem(await other.Client.PostAsJsonAsync(Url(w.CaseId), ConcernBody()), HttpStatusCode.NotFound);
        await Problem(await other.Client.PostAsJsonAsync(Url(w.CaseId, concernId, "actions"), ActionBody()), HttpStatusCode.NotFound);
        await Problem(await other.Client.PostAsJsonAsync(Url(w.CaseId, concernId, "resolution"), ResolutionBody()), HttpStatusCode.NotFound);
        // A concern of this case is not reachable through another case of the same organization.
        var sibling = await Build(o.Client, o.Org, new Opt(Lot: false));
        await Problem(await o.Client.GetAsync(Url(sibling.CaseId, concernId)), HttpStatusCode.NotFound);
        await Problem(await o.Client.PostAsJsonAsync(Url(sibling.CaseId, concernId, "actions"), ActionBody()), HttpStatusCode.NotFound);
        Assert.Equal(2, await Query(o.Org, db => db.CaseConcerns.CountAsync(x => x.EudrCaseId == w.CaseId)));
        // No PUT, PATCH or DELETE exists anywhere.
        foreach (var url in new[] { Url(w.CaseId), Url(w.CaseId, concernId), Url(w.CaseId, concernId, "actions"), Url(w.CaseId, concernId, "notifications"), Url(w.CaseId, concernId, "resolution") })
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await o.Client.SendAsync(new HttpRequestMessage(method, url) { Content = JsonContent.Create(ConcernBody()) })).StatusCode);
    }

    [Fact]
    public async Task Invalid_concerns_are_rejected_and_write_nothing()
    {
        var (o, w) = await Completed(); var c = o.Client;
        var future = await Problem(await c.PostAsJsonAsync(Url(w.CaseId), ConcernBody(raisedAt: DateTimeOffset.UtcNow.AddMinutes(1))), HttpStatusCode.BadRequest);
        Assert.True(future.GetProperty("errors").TryGetProperty("RaisedAtUtc", out _));
        foreach (var body in new object[] { ConcernBody(summary: "   "), ConcernBody(summary: null), ConcernBody(details: ""), ConcernBody(details: "  "), ConcernBody(category: "Bogus"), new { category = "Other", summary = "s", details = "d" } })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(Url(w.CaseId), body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync(Url(w.CaseId), Raw("{ not json"))).StatusCode);
        Assert.Equal(0, await Query(o.Org, db => db.CaseConcerns.CountAsync(x => x.EudrCaseId == w.CaseId)));
        Assert.Equal(0, await Audits(o.Org, w.CaseId, "ConcernRecorded"));
    }

    // ---- corrective actions --------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_member_records_an_action_and_the_future_and_blank_cases_are_rejected()
    {
        var (o, w) = await Completed(); var member = await NewMember(o.Org); var concernId = await Concern(o.Client, w.CaseId);
        var created = await member.PostAsJsonAsync(Url(w.CaseId, concernId, "actions"), ActionBody("  Quarantined the stock.  "));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var action = await Json(created);
        Assert.Equal("Quarantined the stock.", action.GetProperty("description").GetString()); Assert.Equal(JsonValueKind.Null, action.GetProperty("evidenceDocumentId").ValueKind); AssertNoOrganizationId(action);
        Assert.Equal(HttpStatusCode.BadRequest, (await o.Client.PostAsJsonAsync(Url(w.CaseId, concernId, "actions"), ActionBody(performedAt: DateTimeOffset.UtcNow.AddMinutes(1)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await o.Client.PostAsJsonAsync(Url(w.CaseId, concernId, "actions"), ActionBody(description: "  "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await o.Client.PostAsJsonAsync(Url(w.CaseId, concernId, "actions"), new { description = "d" })).StatusCode);
        Assert.Equal(1, await Query(o.Org, db => db.ConcernActions.CountAsync(x => x.ConcernId == concernId)));
        Assert.Equal(1, await Audits(o.Org, w.CaseId, "ConcernActionRecorded"));
        var audit = await Query(o.Org, db => db.AuditEvents.Where(x => x.EventType == "ConcernActionRecorded").Select(x => x.Description).SingleAsync());
        Assert.DoesNotContain("Quarantined", audit);
        var read = await Detail(o.Client, w.CaseId, concernId); Assert.Single(read.GetProperty("actions").EnumerateArray()); Assert.Equal("Open", read.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Action_evidence_must_be_a_current_document_of_the_same_case_and_a_later_withdrawal_keeps_the_link()
    {
        var (o, w) = await Completed(); var concernId = await Concern(o.Client, w.CaseId);
        var other = await NewOwner(); var foreign = await Build(other.Client, other.Org, new Opt(Lot: false));
        var sibling = await Build(o.Client, o.Org, new Opt(Lot: false));
        var url = Url(w.CaseId, concernId, "actions");
        // Unknown, another organization's and another case's documents answer exactly alike (nothing leaks).
        var answers = new List<string>();
        foreach (var doc in new[] { Guid.NewGuid(), foreign.Docs[0], sibling.Docs[0] })
        {
            var problem = await Problem(await o.Client.PostAsJsonAsync(url, ActionBody(evidence: doc)), HttpStatusCode.Conflict);
            Assert.Equal("EvidenceDocumentNotCurrent", Reason(problem)); answers.Add(problem.GetProperty("detail").GetString()!);
        }
        Assert.Single(answers.Distinct());
        Assert.Equal(0, await Query(o.Org, db => db.ConcernActions.CountAsync(x => x.ConcernId == concernId)));

        var linked = await Json(await o.Client.PostAsJsonAsync(url, ActionBody(evidence: w.Docs[0])), HttpStatusCode.Created);
        Assert.Equal(w.Docs[0], linked.GetProperty("evidenceDocumentId").GetGuid());
        // The document is withdrawn later (the case workflow normally forbids it; the row-level transition is what matters): the link and the read survive.
        await Mutate(o.Org, async db => (await db.Documents.SingleAsync(x => x.Id == w.Docs[0])).WithdrawnAtUtc = DateTimeOffset.UtcNow);
        var read = await Detail(o.Client, w.CaseId, concernId);
        Assert.Equal(w.Docs[0], read.GetProperty("actions")[0].GetProperty("evidenceDocumentId").GetGuid());
        Assert.Equal(w.Docs[0], await Query(o.Org, db => db.ConcernActions.Where(x => x.ConcernId == concernId).Select(x => x.EvidenceDocumentId).SingleAsync()));
        // A withdrawn document cannot be newly linked.
        Assert.Equal("EvidenceDocumentNotCurrent", Reason(await Problem(await o.Client.PostAsJsonAsync(url, ActionBody(evidence: w.Docs[0])), HttpStatusCode.Conflict)));
        Assert.Equal(HttpStatusCode.Created, (await o.Client.PostAsJsonAsync(url, ActionBody(evidence: w.Docs[1]))).StatusCode); // another current document is fine
    }

    // ---- notifications ---------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_member_records_a_notification_and_the_recipient_stays_reconstructable_after_the_statement_is_superseded()
    {
        var (o, w) = await Completed(); var member = await NewMember(o.Org); var concernId = await Concern(o.Client, w.CaseId); var recipient = await RecipientId(o.Org, w.CaseId);
        var created = await member.PostAsJsonAsync(Url(w.CaseId, concernId, "notifications"), NotificationBody(recipient, "Letter", note: "  Sent by registered post.  "));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var notification = await Json(created);
        Assert.Equal("Letter", notification.GetProperty("channel").GetString()); Assert.Equal("Sent by registered post.", notification.GetProperty("note").GetString());
        var named = notification.GetProperty("recipient");
        Assert.Equal(recipient, named.GetProperty("recipientId").GetGuid()); Assert.Equal("Importer GmbH", named.GetProperty("name").GetString());
        AssertNoOrganizationId(notification);

        // The case's recipient statement is superseded by a different one later; the notification still names exactly who was notified.
        await Mutate(o.Org, async db =>
        {
            var current = await db.CaseRecipientStatements.Current().SingleAsync(x => x.EudrCaseId == w.CaseId);
            current.SupersededAtUtc = DateTimeOffset.UtcNow;
            var next = new CaseRecipientStatement { OrganizationId = o.Org, EudrCaseId = w.CaseId, Number = current.Number + 1, Resolution = RecipientResolution.RecipientsIdentified };
            db.CaseRecipientStatements.Add(next);
            db.CaseRecipients.Add(new CaseRecipient { OrganizationId = o.Org, CaseRecipientStatementId = next.Id, Name = "Someone Else Ltd", PostalAddress = "Elsewhere 2", Email = "else@example.test" });
        });
        var read = (await Detail(o.Client, w.CaseId, concernId)).GetProperty("notifications")[0].GetProperty("recipient");
        Assert.Equal(recipient, read.GetProperty("recipientId").GetGuid()); Assert.Equal("Importer GmbH", read.GetProperty("name").GetString());
        Assert.Equal("Hauptstrasse 1, Berlin", read.GetProperty("postalAddress").GetString()); Assert.Equal("importer@example.test", read.GetProperty("email").GetString());

        // The audit event carries ids only, never recipient details or the note.
        var audit = await Query(o.Org, db => db.AuditEvents.Where(x => x.EventType == "ConcernNotificationRecorded").Select(x => x.Description).SingleAsync());
        Assert.DoesNotContain("Importer", audit); Assert.DoesNotContain("example.test", audit); Assert.DoesNotContain("registered post", audit); Assert.Contains(recipient.ToString(), audit);
    }

    [Fact]
    public async Task Notification_input_recipient_and_evidence_rules_are_enforced()
    {
        var (o, w) = await Completed(); var concernId = await Concern(o.Client, w.CaseId); var recipient = await RecipientId(o.Org, w.CaseId);
        var other = await NewOwner(); var foreign = await Build(other.Client, other.Org, new Opt(Lot: false)); var foreignRecipient = await RecipientId(other.Org, foreign.CaseId);
        var sibling = await Build(o.Client, o.Org, new Opt(Lot: false)); var siblingRecipient = await RecipientId(o.Org, sibling.CaseId);
        var url = Url(w.CaseId, concernId, "notifications");
        Assert.Equal(HttpStatusCode.BadRequest, (await o.Client.PostAsJsonAsync(url, NotificationBody(recipient, notifiedAt: DateTimeOffset.UtcNow.AddMinutes(1)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await o.Client.PostAsJsonAsync(url, NotificationBody(recipient, channel: "Pigeon"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await o.Client.PostAsJsonAsync(url, NotificationBody(null))).StatusCode);
        // An unknown, another organization's or another case's recipient answers alike.
        var answers = new List<string>();
        foreach (var bad in new[] { Guid.NewGuid(), foreignRecipient, siblingRecipient })
        {
            var problem = await Problem(await o.Client.PostAsJsonAsync(url, NotificationBody(bad)), HttpStatusCode.Conflict);
            Assert.Equal("RecipientNotFound", Reason(problem)); answers.Add(problem.GetProperty("detail").GetString()!);
        }
        Assert.Single(answers.Distinct());
        // Evidence follows the action rules.
        Assert.Equal("EvidenceDocumentNotCurrent", Reason(await Problem(await o.Client.PostAsJsonAsync(url, NotificationBody(recipient, evidence: foreign.Docs[0])), HttpStatusCode.Conflict)));
        Assert.Equal(0, await Query(o.Org, db => db.ConcernNotifications.CountAsync(x => x.ConcernId == concernId)));
        var linked = await Json(await o.Client.PostAsJsonAsync(url, NotificationBody(recipient, "Portal", evidence: w.Docs[0])), HttpStatusCode.Created);
        Assert.Equal(w.Docs[0], linked.GetProperty("evidenceDocumentId").GetGuid());
        await Mutate(o.Org, async db => (await db.Documents.SingleAsync(x => x.Id == w.Docs[0])).WithdrawnAtUtc = DateTimeOffset.UtcNow);
        Assert.Equal(w.Docs[0], (await Detail(o.Client, w.CaseId, concernId)).GetProperty("notifications")[0].GetProperty("evidenceDocumentId").GetGuid());
        Assert.Equal("EvidenceDocumentNotCurrent", Reason(await Problem(await o.Client.PostAsJsonAsync(url, NotificationBody(recipient, evidence: w.Docs[0])), HttpStatusCode.Conflict)));
    }

    // ---- resolution -------------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Resolving_is_owner_only_and_a_member_learns_nothing()
    {
        var (o, w) = await Completed(); var member = await NewMember(o.Org); var concernId = await Concern(o.Client, w.CaseId); var other = await NewOwner(); var foreign = await Build(other.Client, other.Org, new Opt(Lot: false));
        // A Member: 403 for the concern, an unknown concern, an unknown case and another organization's case, and for an invalid or malformed body alike.
        foreach (var (caseId, id) in new[] { (w.CaseId, concernId), (w.CaseId, Guid.NewGuid()), (Guid.NewGuid(), concernId), (foreign.CaseId, concernId) })
            Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync(Url(caseId, id, "resolution"), ResolutionBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync(Url(w.CaseId, concernId, "resolution"), ResolutionBody(rationale: ""))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync(Url(w.CaseId, concernId, "resolution"), Raw("{ not json"))).StatusCode);
        Assert.Equal(0, await Query(o.Org, db => db.ConcernResolutions.CountAsync()));
        // The Owner meets the documented order: unknown case/concern, then the body.
        await Problem(await o.Client.PostAsJsonAsync(Url(Guid.NewGuid(), concernId, "resolution"), ResolutionBody()), HttpStatusCode.NotFound);
        await Problem(await o.Client.PostAsJsonAsync(Url(w.CaseId, Guid.NewGuid(), "resolution"), ResolutionBody()), HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.BadRequest, (await o.Client.PostAsJsonAsync(Url(w.CaseId, concernId, "resolution"), ResolutionBody(rationale: "   "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await o.Client.PostAsJsonAsync(Url(w.CaseId, concernId, "resolution"), ResolutionBody(outcome: "Bogus"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await o.Client.PostAsJsonAsync(Url(w.CaseId, concernId, "resolution"), new { outcome = "NoFurtherActionRequired" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await o.Client.PostAsJsonAsync(Url(w.CaseId, concernId, "resolution"), ResolutionBody(outcome: "EscalatedExternally"))).StatusCode); // removed from v1: escalation is an action or notification
        Assert.Equal(0, await Query(o.Org, db => db.ConcernResolutions.CountAsync()));
    }

    [Fact]
    public async Task The_owner_check_is_live_a_demoted_owner_cannot_resolve()
    {
        var (o, w) = await Completed(); var concernId = await Concern(o.Client, w.CaseId);
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync($"""UPDATE "OrganizationUsers" SET "Role" = {(int)OrganizationRole.Member} WHERE "UserId" = {o.UserId}""");
        Assert.Equal(HttpStatusCode.Forbidden, (await o.Client.PostAsJsonAsync(Url(w.CaseId, concernId, "resolution"), ResolutionBody())).StatusCode);
        Assert.Equal(0, await Query(o.Org, db => db.ConcernResolutions.CountAsync()));
    }

    [Fact]
    public async Task The_owner_resolves_once_the_server_sets_the_time_and_everything_further_is_refused_while_all_facts_stay_readable()
    {
        var (o, w) = await Completed(); var c = o.Client; var concernId = await Concern(c, w.CaseId); var recipient = await RecipientId(o.Org, w.CaseId);
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync(Url(w.CaseId, concernId, "actions"), ActionBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync(Url(w.CaseId, concernId, "notifications"), NotificationBody(recipient))).StatusCode);

        var before = DateTimeOffset.UtcNow;
        // A client-supplied resolvedAtUtc / status is ignored: the clock is the server's.
        var response = await c.PostAsJsonAsync(Url(w.CaseId, concernId, "resolution"), new { outcome = "NoFurtherActionRequired", rationale = "  Handed to the competent authority.  ", resolvedAtUtc = "2001-01-01T00:00:00Z", status = "Open" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var resolution = await Json(response); var after = DateTimeOffset.UtcNow;
        Assert.Equal("NoFurtherActionRequired", resolution.GetProperty("outcome").GetString()); Assert.Equal("Handed to the competent authority.", resolution.GetProperty("rationale").GetString());
        Assert.Equal(o.UserId, resolution.GetProperty("resolvedByUserId").GetGuid()); AssertNoOrganizationId(resolution);
        var resolvedAt = resolution.GetProperty("resolvedAtUtc").GetDateTimeOffset(); Assert.InRange(resolvedAt, before.AddSeconds(-1), after.AddSeconds(1));
        Assert.Equal(1, await Audits(o.Org, w.CaseId, "ConcernResolved"));
        var audit = await Query(o.Org, db => db.AuditEvents.Where(x => x.EventType == "ConcernResolved").Select(x => x.Description).SingleAsync());
        Assert.DoesNotContain("competent authority", audit);

        // Second resolution, further actions and notifications are refused; nothing is written.
        Assert.Equal("ConcernResolved", Reason(await Problem(await c.PostAsJsonAsync(Url(w.CaseId, concernId, "resolution"), ResolutionBody()), HttpStatusCode.Conflict)));
        Assert.Equal("ConcernResolved", Reason(await Problem(await c.PostAsJsonAsync(Url(w.CaseId, concernId, "actions"), ActionBody()), HttpStatusCode.Conflict)));
        Assert.Equal("ConcernResolved", Reason(await Problem(await c.PostAsJsonAsync(Url(w.CaseId, concernId, "notifications"), NotificationBody(recipient)), HttpStatusCode.Conflict)));
        Assert.Equal(1, await Query(o.Org, db => db.ConcernResolutions.CountAsync(x => x.ConcernId == concernId)));
        Assert.Equal(1, await Query(o.Org, db => db.ConcernActions.CountAsync(x => x.ConcernId == concernId)));
        Assert.Equal(1, await Query(o.Org, db => db.ConcernNotifications.CountAsync(x => x.ConcernId == concernId)));

        // Everything remains readable and the concern is not reopened.
        var read = await Detail(c, w.CaseId, concernId);
        Assert.Equal("Resolved", read.GetProperty("status").GetString()); Assert.Single(read.GetProperty("actions").EnumerateArray()); Assert.Single(read.GetProperty("notifications").EnumerateArray());
        Assert.Equal(resolvedAt, read.GetProperty("resolution").GetProperty("resolvedAtUtc").GetDateTimeOffset());
        var list = await Json(await c.GetAsync(Url(w.CaseId)), HttpStatusCode.OK);
        Assert.Equal("Resolved", list[0].GetProperty("status").GetString()); Assert.Equal(1, list[0].GetProperty("actionCount").GetInt32()); Assert.Equal(1, list[0].GetProperty("notificationCount").GetInt32());
        Assert.False(list[0].TryGetProperty("details", out _));

        // A later issue is a new concern on the same terminal case; the first stays resolved.
        var second = await Json(await c.PostAsJsonAsync(Url(w.CaseId), ConcernBody("Second issue")), HttpStatusCode.Created);
        Assert.Equal(2, second.GetProperty("number").GetInt32()); Assert.Equal("Open", second.GetProperty("status").GetString());
        var both = (await Json(await c.GetAsync(Url(w.CaseId)), HttpStatusCode.OK)).EnumerateArray().ToList();
        Assert.Equal([2, 1], both.Select(x => x.GetProperty("number").GetInt32()).ToArray()); // newest first
        Assert.Equal(["Open", "Resolved"], both.Select(x => x.GetProperty("status").GetString()!).ToArray());
    }

    [Fact]
    public async Task Simultaneous_resolutions_are_serialized_so_exactly_one_wins()
    {
        var (o, w) = await Completed(); var concernId = await Concern(o.Client, w.CaseId);
        var second = await NewOwnerPeer(o.Org); // a second Owner of the same organization
        var results = await Task.WhenAll(o.Client.PostAsJsonAsync(Url(w.CaseId, concernId, "resolution"), ResolutionBody("CorrectiveActionCompleted", "First owner.")),
            second.PostAsJsonAsync(Url(w.CaseId, concernId, "resolution"), ResolutionBody("NoFurtherActionRequired", "Second owner.")));
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], results.Select(r => r.StatusCode).Order().ToArray());
        Assert.Equal(1, await Query(o.Org, db => db.ConcernResolutions.CountAsync(x => x.ConcernId == concernId)));
        Assert.Equal(1, await Audits(o.Org, w.CaseId, "ConcernResolved"));
    }

    [Fact]
    public async Task An_action_or_notification_racing_a_resolution_cannot_slip_in_after_it()
    {
        var (o, w) = await Completed(); var concernId = await Concern(o.Client, w.CaseId); var recipient = await RecipientId(o.Org, w.CaseId);
        var calls = new List<Task<HttpResponseMessage>> { o.Client.PostAsJsonAsync(Url(w.CaseId, concernId, "resolution"), ResolutionBody()) };
        for (var i = 0; i < 4; i++)
        {
            calls.Add(o.Client.PostAsJsonAsync(Url(w.CaseId, concernId, "actions"), ActionBody($"Action {i}")));
            calls.Add(o.Client.PostAsJsonAsync(Url(w.CaseId, concernId, "notifications"), NotificationBody(recipient)));
        }
        var results = await Task.WhenAll(calls);
        Assert.Equal(HttpStatusCode.Created, results[0].StatusCode);
        Assert.All(results.Skip(1), r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict, r.StatusCode.ToString()));
        // The writes serialize under the case lock: every accepted action/notification is stored, every other one was refused as resolved, and none is accepted afterwards.
        var accepted = results.Skip(1).Count(r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(accepted, await Query(o.Org, db => db.ConcernActions.CountAsync(x => x.ConcernId == concernId)) + await Query(o.Org, db => db.ConcernNotifications.CountAsync(x => x.ConcernId == concernId)));
        var actions = await Query(o.Org, db => db.ConcernActions.CountAsync(x => x.ConcernId == concernId));
        Assert.Equal(HttpStatusCode.Conflict, (await o.Client.PostAsJsonAsync(Url(w.CaseId, concernId, "actions"), ActionBody())).StatusCode);
        Assert.Equal(actions, await Query(o.Org, db => db.ConcernActions.CountAsync(x => x.ConcernId == concernId)));
        Assert.Equal(1, await Query(o.Org, db => db.ConcernResolutions.CountAsync(x => x.ConcernId == concernId)));
    }

    // A second Owner of the same organization (a Member promoted in place).
    private async Task<HttpClient> NewOwnerPeer(Guid org)
    {
        var (client, userId) = await NewMemberWithId(org);
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync($"""UPDATE "OrganizationUsers" SET "Role" = {(int)OrganizationRole.Owner} WHERE "UserId" = {userId} AND "OrganizationId" = {org}""");
        return client;
    }

    // ---- isolation of the completed case -------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Concern_activity_leaves_the_case_review_closure_external_dds_and_supplier_links_untouched()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org); var c = o.Client;
        var state = await Completeness(c, w.CaseId);
        var reviewId = (await Json(await c.PostAsJsonAsync($"/api/eudr-cases/{w.CaseId}/reviews", new
        {
            decision = "AcceptedAsCompleteUnderPolicy", rationale = "Reviewed against the recorded facts.", expectedFingerprint = Fingerprint(state),
            expectedLatestReviewId = state.GetProperty("latestReview").ValueKind == JsonValueKind.Null ? (Guid?)null : state.GetProperty("latestReview").GetProperty("reviewId").GetGuid(),
        }), HttpStatusCode.Created)).GetProperty("reviewId").GetGuid();
        var recordId = await RecordOk(c, w.CaseId, reviewId, "EU-REF-ISO");
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync($"/api/eudr-cases/{w.CaseId}/close", new { expectedReviewId = reviewId })).StatusCode);

        async Task<string> Fingerprint_() => string.Join('|', await Query(o.Org, async db =>
        {
            var kase = await db.Cases.AsNoTracking().SingleAsync(x => x.Id == w.CaseId);
            var closure = await db.CaseClosures.AsNoTracking().SingleAsync(x => x.EudrCaseId == w.CaseId);
            var review = await db.ReviewRecords.AsNoTracking().SingleAsync(x => x.EudrCaseId == w.CaseId);
            var revision = await db.CaseRevisions.AsNoTracking().SingleAsync(x => x.EudrCaseId == w.CaseId);
            var dds = await db.ExternalDdsRecords.AsNoTracking().SingleAsync(x => x.EudrCaseId == w.CaseId);
            var requests = await db.SupplierRequests.AsNoTracking().Where(x => x.EudrCaseId == w.CaseId).Select(x => x.RevokedAtUtc).ToListAsync();
            return new[]
            {
                kase.Status.ToString(), kase.UpdatedAtUtc.ToString("O"), closure.Id.ToString(), closure.ClosedAtUtc.ToString("O"), closure.ExternalDdsReference ?? "", review.Id.ToString(), review.Decision.ToString(),
                revision.Fingerprint, revision.SnapshotJson, dds.Id.ToString(), dds.SupersededAtUtc?.ToString("O") ?? "", string.Join(',', requests.Select(x => x?.ToString("O") ?? "null")),
                (await db.CaseClosures.CountAsync(x => x.EudrCaseId == w.CaseId)).ToString(), (await db.CaseReopenings.CountAsync(x => x.EudrCaseId == w.CaseId)).ToString(),
                (await db.ReviewRecords.CountAsync(x => x.EudrCaseId == w.CaseId)).ToString(), (await db.ExternalDdsRecords.CountAsync(x => x.EudrCaseId == w.CaseId)).ToString(),
            };
        }));
        var completenessBefore = await Completeness(c, w.CaseId); var baseline = await Fingerprint_();
        Assert.Equal("Closed", (await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}"))).GetProperty("status").GetString());

        var concernId = await Concern(c, w.CaseId);
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync(Url(w.CaseId, concernId, "actions"), ActionBody(evidence: w.Docs[0]))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync(Url(w.CaseId, concernId, "notifications"), NotificationBody(await RecipientId(o.Org, w.CaseId)))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync(Url(w.CaseId, concernId, "resolution"), ResolutionBody())).StatusCode);

        Assert.Equal(baseline, await Fingerprint_());
        var completenessAfter = await Completeness(c, w.CaseId);
        Assert.Equal(Fingerprint(completenessBefore), Fingerprint(completenessAfter));
        Assert.Equal(completenessBefore.GetProperty("reviewFreshness").GetString(), completenessAfter.GetProperty("reviewFreshness").GetString());
        Assert.Equal("Current", completenessAfter.GetProperty("reviewFreshness").GetString());
        Assert.Equal(recordId, await CurrentId(c, w.CaseId));
        Assert.Equal("Closed", (await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}"))).GetProperty("status").GetString());
        Assert.Equal(0, await Query(o.Org, db => db.AuditEvents.CountAsync(x => x.EudrCaseId == w.CaseId && x.EventType == "CaseReopened")));
    }
}
