using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Provena.Api.Auth;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
using Xunit;
namespace Provena.Api.IntegrationTests;

// Slice 3 / commit 2: internal producer / production plot / plot revision API. Tenants are provisioned through AuthService directly,
// because the shared fixture's auth rate limiter (10 / 5 minutes) is already nearly consumed by the other test classes.
[Collection("Api")]
public sealed class ProvenanceTests(ApiFactory factory)
{
    private const string Square = """{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,1],[0,0]]]}""";
    private const string SquareRotated = """{"type":"Polygon","coordinates":[[[1,0],[1,1],[0,1],[0,0],[1,0]]]}""";
    private const string SquareReversed = """{"type":"Polygon","coordinates":[[[0,0],[0,1],[1,1],[1,0],[0,0]]]}""";
    private const string SquareAsMulti = """{"type":"MultiPolygon","coordinates":[[[[0,0],[1,0],[1,1],[0,1],[0,0]]]]}""";
    private const string Bigger = """{"type":"Polygon","coordinates":[[[0,0],[2,0],[2,2],[0,2],[0,0]]]}""";
    private const string Biggest = """{"type":"Polygon","coordinates":[[[0,0],[3,0],[3,3],[0,3],[0,0]]]}""";
    private const string Point = """{"type":"Point","coordinates":[10.5,-20.25]}""";
    private const string Multi = """{"type":"MultiPolygon","coordinates":[[[[0,0],[1,0],[1,1],[0,0]]],[[[5,5],[6,5],[6,6],[5,5]]]]}""";
    private const string GeoV1 = """{"type":"Polygon","coordinates":[[[-48,-20],[-47,-20],[-47,-19],[-48,-20]]]}""";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (string Token, Guid Org, string Token2, Guid Org2)? tenants;
    private async Task<(HttpClient A, Guid OrgA, HttpClient B, Guid OrgB)> Tenants()
    {
        await Gate.WaitAsync();
        try { tenants ??= await Provision(); } finally { Gate.Release(); }
        return (Client(tenants.Value.Token), tenants.Value.Org, Client(tenants.Value.Token2), tenants.Value.Org2);
    }
    private async Task<(string, Guid, string, Guid)> Provision()
    {
        async Task<(string, Guid)> One(string name)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var token = await scope.ServiceProvider.GetRequiredService<AuthService>().Register(new RegisterRequest($"{name}-{Guid.NewGuid():N}@example.test", "Test-Only-Password-928!", "Test", "User", "Org " + name, null), CancellationToken.None);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return (token.AccessToken, await db.Organizations.Where(x => x.Name == "Org " + name).Select(x => x.Id).SingleAsync());
        }
        var a = await One("Prov-" + Guid.NewGuid().ToString("N")[..8]); var b = await One("Prov-" + Guid.NewGuid().ToString("N")[..8]);
        return (a.Item1, a.Item2, b.Item1, b.Item2);
    }
    private HttpClient Client(string token)
    { var c = factory.CreateClient(); c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); return c; }
    private static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode? expected = null)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (expected.HasValue) Assert.True(expected == response.StatusCode, $"Expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        else Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }
    private static StringContent Raw(string json) => new(json, Encoding.UTF8, "application/json");
    private static string Revision(string geometry, string country = "BR", string? area = null, string? note = null) =>
        $$"""{"countryCode":"{{country}}","geometry":{{geometry}},"areaHectares":{{area ?? "null"}},"note":{{(note == null ? "null" : $"\"{note}\"")}}}""";
    private static string PlotBody(Guid producerId, string name, string revision, string? externalReference = null) =>
        $$"""{"producerId":"{{producerId}}","name":"{{name}}","externalReference":{{(externalReference == null ? "null" : $"\"{externalReference}\"")}},"notes":null,"initialRevision":{{revision}}}""";
    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..10];
    private static async Task<Guid> CreateProducer(HttpClient c, string? name = null, string? reference = null) =>
        (await Json(await c.PostAsJsonAsync("/api/producers", new { name = name ?? Unique("Producer"), externalReference = reference, countryCode = "BR", notes = "n" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
    private static async Task<(Guid Id, JsonElement Json)> CreatePlot(HttpClient c, Guid producerId, string? revision = null, string? name = null)
    {
        var json = await Json(await c.PostAsync("/api/production-plots", Raw(PlotBody(producerId, name ?? Unique("Plot"), revision ?? Revision(Square, area: "1.5")))), HttpStatusCode.Created);
        return (json.GetProperty("id").GetGuid(), json);
    }
    private static Task<HttpResponseMessage> PostRevision(HttpClient c, Guid plotId, string revision) => c.PostAsync($"/api/production-plots/{plotId}/revisions", Raw(revision));
    private static async Task<JsonElement> Problem(HttpResponseMessage response, HttpStatusCode status)
    {
        var json = await Json(response, status);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return json;
    }
    private static async Task SetPlotActive(HttpClient c, Guid plotId, bool active, string name = "Plot") =>
        await Json(await c.PutAsJsonAsync($"/api/production-plots/{plotId}", new { name, externalReference = (string?)null, notes = (string?)null, isActive = active }), HttpStatusCode.OK);
    private static Task<HttpResponseMessage> PutProducer(HttpClient c, Guid id, string name, bool active, string? reference = null) =>
        c.PutAsJsonAsync($"/api/producers/{id}", new { name, externalReference = reference, countryCode = "BR", notes = (string?)null, isActive = active });
    private AppDbContext TenantDb(IServiceScope scope, Guid org)
    {
        var tenant = new TenantContext(new HttpContextAccessor()); tenant.SetOrganization(org);
        return new AppDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>(), tenant);
    }
    private static void AssertNoOrganizationId(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject()) { Assert.NotEqual("organizationid", p.Name.ToLowerInvariant()); AssertNoOrganizationId(p.Value); }
                break;
            case JsonValueKind.Array: foreach (var item in element.EnumerateArray()) AssertNoOrganizationId(item); break;
        }
    }

    [Fact]
    public async Task Producer_crud_and_lifecycle()
    {
        var (a, _, _, _) = await Tenants(); var reference = Unique("REF");
        var response = await a.PostAsJsonAsync("/api/producers", new { name = "  Coop Alpha ", externalReference = reference, countryCode = "BR", notes = "pilot", isActive = false });
        var created = await Json(response, HttpStatusCode.Created); var id = created.GetProperty("id").GetGuid();
        Assert.True(response.Headers.Location!.IsAbsoluteUri); Assert.EndsWith($"/api/producers/{id}", response.Headers.Location.AbsolutePath);
        Assert.Equal("Coop Alpha", created.GetProperty("name").GetString());
        Assert.True(created.GetProperty("isActive").GetBoolean(), "A new producer is always active.");
        AssertNoOrganizationId(created);
        Assert.Equal(reference, (await Json(await a.GetAsync($"/api/producers/{id}"))).GetProperty("externalReference").GetString());

        var updated = await Json(await PutProducer(a, id, "Coop Alpha 2", active: false, reference));
        Assert.Equal("Coop Alpha 2", updated.GetProperty("name").GetString()); Assert.False(updated.GetProperty("isActive").GetBoolean());
        Assert.True((await Json(await PutProducer(a, id, "Coop Alpha 2", active: true, reference))).GetProperty("isActive").GetBoolean());

        await Problem(await a.PostAsJsonAsync("/api/producers", new { name = "Dup", externalReference = reference }), HttpStatusCode.Conflict);
        var other = await CreateProducer(a);
        await Problem(await PutProducer(a, other, "Other", true, reference), HttpStatusCode.Conflict);
        await Problem(await a.PostAsJsonAsync("/api/producers", new { name = "" }), HttpStatusCode.BadRequest);
        await Problem(await a.PostAsJsonAsync("/api/producers", new { name = "X", countryCode = "br" }), HttpStatusCode.BadRequest);
        await Problem(await a.PostAsJsonAsync("/api/producers", new { name = new string('x', 201) }), HttpStatusCode.BadRequest);
        await Problem(await PutProducer(a, Guid.NewGuid(), "Nope", true), HttpStatusCode.NotFound);
        await Problem(await a.GetAsync($"/api/producers/{Guid.NewGuid()}"), HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await a.DeleteAsync($"/api/producers/{id}")).StatusCode);
    }

    [Fact]
    public async Task Put_requires_an_explicit_isActive_so_an_omission_never_reactivates()
    {
        var (a, _, _, _) = await Tenants();
        // Producer.
        var producer = await CreateProducer(a); await Json(await PutProducer(a, producer, "Dormant", false));
        var missing = await Problem(await a.PutAsync($"/api/producers/{producer}", Raw("""{"name":"Dormant"}""")), HttpStatusCode.BadRequest);
        Assert.True(missing.GetProperty("errors").TryGetProperty("isActive", out _));
        await Problem(await a.PutAsync($"/api/producers/{producer}", Raw("""{"name":"Dormant","isActive":null}""")), HttpStatusCode.BadRequest);
        Assert.False((await Json(await a.GetAsync($"/api/producers/{producer}"))).GetProperty("isActive").GetBoolean());
        Assert.True((await Json(await PutProducer(a, producer, "Dormant", true))).GetProperty("isActive").GetBoolean());
        Assert.False((await Json(await PutProducer(a, producer, "Dormant", false))).GetProperty("isActive").GetBoolean());
        // POST still starts active without the field.
        Assert.True((await Json(await a.PostAsync("/api/producers", Raw($$"""{"name":"{{Unique("NoFlag")}}"}""")), HttpStatusCode.Created)).GetProperty("isActive").GetBoolean());

        // Production plot.
        var active = await CreateProducer(a); var (plotId, _) = await CreatePlot(a, active); await SetPlotActive(a, plotId, false);
        var plotMissing = await Problem(await a.PutAsync($"/api/production-plots/{plotId}", Raw("""{"name":"Plot"}""")), HttpStatusCode.BadRequest);
        Assert.True(plotMissing.GetProperty("errors").TryGetProperty("IsActive", out _) || plotMissing.GetProperty("errors").TryGetProperty("isActive", out _));
        Assert.False((await Json(await a.GetAsync($"/api/production-plots/{plotId}"))).GetProperty("isActive").GetBoolean());
        await SetPlotActive(a, plotId, true);
        Assert.True((await Json(await a.GetAsync($"/api/production-plots/{plotId}"))).GetProperty("isActive").GetBoolean());
        await SetPlotActive(a, plotId, false);
        Assert.False((await Json(await a.GetAsync($"/api/production-plots/{plotId}"))).GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Unknown_or_foreign_route_ids_stay_404_even_when_the_put_body_omits_isActive()
    {
        var (a, _, b, _) = await Tenants();
        var producer = await CreateProducer(a); var (plotId, _) = await CreatePlot(a, producer);
        var foreignProducer = await CreateProducer(b); var (foreignPlot, _) = await CreatePlot(b, foreignProducer);
        const string producerBody = """{"name":"X"}"""; const string plotBody = """{"name":"X"}""";

        // Known id: the invalid body is a 400.
        await Problem(await a.PutAsync($"/api/producers/{producer}", Raw(producerBody)), HttpStatusCode.BadRequest);
        await Problem(await a.PutAsync($"/api/production-plots/{plotId}", Raw(plotBody)), HttpStatusCode.BadRequest);
        // Foreign and unknown ids: 404, never 400.
        await Problem(await a.PutAsync($"/api/producers/{foreignProducer}", Raw(producerBody)), HttpStatusCode.NotFound);
        await Problem(await a.PutAsync($"/api/producers/{Guid.NewGuid()}", Raw(producerBody)), HttpStatusCode.NotFound);
        await Problem(await a.PutAsync($"/api/production-plots/{foreignPlot}", Raw(plotBody)), HttpStatusCode.NotFound);
        await Problem(await a.PutAsync($"/api/production-plots/{Guid.NewGuid()}", Raw(plotBody)), HttpStatusCode.NotFound);
        // The same holds for an otherwise invalid body (empty name).
        await Problem(await a.PutAsync($"/api/producers/{Guid.NewGuid()}", Raw("""{"name":""}""")), HttpStatusCode.NotFound);
        await Problem(await a.PutAsync($"/api/production-plots/{foreignPlot}", Raw("""{"name":""}""")), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Producer_list_is_paged_ordered_searchable_and_filters_by_activity()
    {
        var (a, _, b, _) = await Tenants(); var tag = Unique("pl");
        var ids = new List<Guid>();
        foreach (var (name, active) in new[] { ("B", true), ("A", true), ("C", false), ("A", true), ("D", false) })
        { var id = await CreateProducer(a, $"{tag} {name}"); ids.Add(id); if (!active) await Json(await PutProducer(a, id, $"{tag} {name}", false)); }
        await CreateProducer(b, $"{tag} foreign");

        async Task<JsonElement> Page(string query) => await Json(await a.GetAsync($"/api/producers?search={tag}&{query}"));
        var all = await Page("pageSize=100"); Assert.Equal(5, all.GetProperty("totalCount").GetInt32());
        var names = all.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("name").GetString()!).ToList();
        Assert.Equal(names.Order(StringComparer.Ordinal).ToList(), names);
        var first = await Page("pageSize=2"); var second = await Page("page=2&pageSize=2"); var third = await Page("page=3&pageSize=2");
        var paged = new[] { first, second, third }.SelectMany(x => x.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())).ToList();
        Assert.Equal(5, paged.Distinct().Count()); Assert.Equal(1, third.GetProperty("items").GetArrayLength());
        Assert.Equal(3, (await Page("isActive=true")).GetProperty("totalCount").GetInt32());
        Assert.Equal(2, (await Page("isActive=false")).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Json(await a.GetAsync("/api/producers?search=" + tag + "%20foreign"))).GetProperty("totalCount").GetInt32());
        await Problem(await a.GetAsync("/api/producers?page=0"), HttpStatusCode.BadRequest);
        await Problem(await a.GetAsync("/api/producers?pageSize=101"), HttpStatusCode.BadRequest);
        await Problem(await a.GetAsync("/api/producers?search=" + new string('x', 201)), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Plots_are_producer_scoped_and_listed_tenant_wide_with_filters()
    {
        var (a, _, b, _) = await Tenants(); var tag = Unique("mp");
        var p1 = await CreateProducer(a); var p2 = await CreateProducer(a);
        var plots = new List<Guid>();
        foreach (var (producer, n) in new[] { (p1, "1"), (p1, "2"), (p2, "1"), (p2, "2") }) plots.Add((await CreatePlot(a, producer, name: $"{tag} {n}")).Id);

        async Task<JsonElement> List(string query) => await Json(await a.GetAsync($"/api/production-plots?search={tag}&{query}"));
        Assert.Equal(4, (await List("pageSize=100")).GetProperty("totalCount").GetInt32());
        var forP1 = await List($"producerId={p1}");
        Assert.Equal(2, forP1.GetProperty("totalCount").GetInt32());
        Assert.All(forP1.GetProperty("items").EnumerateArray(), x => Assert.Equal(p1, x.GetProperty("producerId").GetGuid()));
        var item = forP1.GetProperty("items")[0];
        Assert.Equal(1, item.GetProperty("currentRevision").GetProperty("number").GetInt32());
        Assert.False(item.GetProperty("currentRevision").TryGetProperty("geometry", out _), "List items carry the revision summary only.");
        AssertNoOrganizationId(forP1);
        Assert.Equal(1, (await List("pageSize=1&page=4")).GetProperty("items").GetArrayLength());
        // Unknown or foreign producer ids as a list filter return an empty page.
        Assert.Equal(0, (await List($"producerId={Guid.NewGuid()}")).GetProperty("totalCount").GetInt32());
        var foreignProducer = await CreateProducer(b); await CreatePlot(b, foreignProducer, name: tag + " foreign");
        Assert.Equal(0, (await List($"producerId={foreignProducer}")).GetProperty("totalCount").GetInt32());
        // isActive filter and update.
        await SetPlotActive(a, plots[0], false, $"{tag} 1");
        Assert.Equal(3, (await List("isActive=true")).GetProperty("totalCount").GetInt32());
        Assert.Equal(1, (await List("isActive=false")).GetProperty("totalCount").GetInt32());
        await Problem(await a.GetAsync("/api/production-plots?producerId=not-a-guid"), HttpStatusCode.BadRequest);
        await Problem(await a.GetAsync("/api/production-plots?pageSize=0"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Plot_update_never_changes_the_producer_and_validates_input()
    {
        var (a, _, _, _) = await Tenants(); var p1 = await CreateProducer(a); var p2 = await CreateProducer(a); var (plotId, _) = await CreatePlot(a, p1);
        var updated = await Json(await a.PutAsync($"/api/production-plots/{plotId}", Raw($$"""{"producerId":"{{p2}}","name":"Renamed","externalReference":"{{Unique("PR")}}","notes":"x","isActive":true}""")));
        Assert.Equal(p1, updated.GetProperty("producerId").GetGuid()); Assert.Equal("Renamed", updated.GetProperty("name").GetString());
        Assert.Equal(p1, (await Json(await a.GetAsync($"/api/production-plots/{plotId}"))).GetProperty("producerId").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PutAsJsonAsync($"/api/production-plots/{plotId}", new { name = "", isActive = true })).StatusCode);
        await Problem(await a.PutAsJsonAsync($"/api/production-plots/{Guid.NewGuid()}", new { name = "x", isActive = true }), HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await a.DeleteAsync($"/api/production-plots/{plotId}")).StatusCode);
        // A duplicate plot reference is a conflict.
        var reference = Unique("PREF"); await CreatePlot(a, p1, name: Unique("P"), revision: null);
        await Json(await a.PostAsync("/api/production-plots", Raw(PlotBody(p1, "R1", Revision(Square), reference))), HttpStatusCode.Created);
        await Problem(await a.PostAsync("/api/production-plots", Raw(PlotBody(p1, "R2", Revision(Square), reference))), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Plot_creation_requires_an_active_producer_and_is_serialized_against_deactivation()
    {
        var (a, _, _, _) = await Tenants(); var producer = await CreateProducer(a, Unique("Inactive"));
        await Json(await PutProducer(a, producer, "Inactive", false));
        await Problem(await a.PostAsync("/api/production-plots", Raw(PlotBody(producer, "P", Revision(Square)))), HttpStatusCode.Conflict);
        Assert.Equal(0, (await Json(await a.GetAsync($"/api/production-plots?producerId={producer}"))).GetProperty("totalCount").GetInt32());
        await Json(await PutProducer(a, producer, "Inactive", true));
        await CreatePlot(a, producer);

        // Concurrent deactivation and plot creation: every outcome is either "plot created, then deactivated" or "deactivated first, plot refused".
        for (var i = 0; i < 8; i++)
        {
            var p = await CreateProducer(a);
            var deactivate = PutProducer(a, p, "Race", false); var create = a.PostAsync("/api/production-plots", Raw(PlotBody(p, "Race plot", Revision(Square))));
            var (deactivated, createdPlot) = (await deactivate, await create);
            Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
            Assert.True(createdPlot.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict, $"Unexpected {(int)createdPlot.StatusCode}");
            var count = (await Json(await a.GetAsync($"/api/production-plots?producerId={p}"))).GetProperty("totalCount").GetInt32();
            Assert.Equal(createdPlot.StatusCode == HttpStatusCode.Created ? 1 : 0, count);
            Assert.False((await Json(await a.GetAsync($"/api/producers/{p}"))).GetProperty("isActive").GetBoolean());
            // Once the deactivation has committed, no further plot can be created.
            await Problem(await a.PostAsync("/api/production-plots", Raw(PlotBody(p, "Late", Revision(Square)))), HttpStatusCode.Conflict);
        }
    }

    [Fact]
    public async Task Revisions_are_numbered_sequentially_and_noop_changes_are_rejected()
    {
        var (a, _, _, _) = await Tenants(); var producer = await CreateProducer(a); var (plotId, created) = await CreatePlot(a, producer, Revision(Square, area: "1.5"));
        Assert.Equal(1, created.GetProperty("currentRevision").GetProperty("number").GetInt32());
        // Equivalent representations, the same country and an area that differs only in scale are not a material change.
        foreach (var same in new[] { Square, SquareRotated, SquareReversed, SquareAsMulti })
            await Problem(await PostRevision(a, plotId, Revision(same, area: "1.5000")), HttpStatusCode.Conflict);
        await Problem(await PostRevision(a, plotId, Revision(Square, area: "1.5", note: "only a note")), HttpStatusCode.Conflict);

        var response = await PostRevision(a, plotId, Revision(Bigger, area: "4", note: "survey"));
        var rev2 = await Json(response, HttpStatusCode.Created); Assert.Equal(2, rev2.GetProperty("number").GetInt32());
        Assert.True(response.Headers.Location!.IsAbsoluteUri);
        Assert.Equal(rev2.GetProperty("id").GetGuid(), (await Json(await a.GetAsync(response.Headers.Location.PathAndQuery))).GetProperty("id").GetGuid());
        // Country and area changes are material on their own.
        var rev3 = await Json(await PostRevision(a, plotId, Revision(Bigger, country: "CO", area: "4")), HttpStatusCode.Created); Assert.Equal(3, rev3.GetProperty("number").GetInt32());
        var rev4 = await Json(await PostRevision(a, plotId, Revision(Bigger, country: "CO", area: "5")), HttpStatusCode.Created); Assert.Equal(4, rev4.GetProperty("number").GetInt32());
        var rev5 = await Json(await PostRevision(a, plotId, Revision(Bigger, country: "CO")), HttpStatusCode.Created); Assert.Equal(5, rev5.GetProperty("number").GetInt32());
        Assert.Equal(JsonValueKind.Null, rev5.GetProperty("areaHectares").ValueKind);
        var current = await Json(await a.GetAsync($"/api/production-plots/{plotId}"));
        Assert.Equal(5, current.GetProperty("currentRevision").GetProperty("number").GetInt32());
        Assert.Equal(JsonValueKind.Null, current.GetProperty("currentRevision").GetProperty("supersededAtUtc").ValueKind);
    }

    [Fact]
    public async Task Revision_creation_requires_an_active_plot_but_not_an_active_producer()
    {
        var (a, _, _, _) = await Tenants(); var producer = await CreateProducer(a, Unique("Prod")); var (plotId, _) = await CreatePlot(a, producer, name: "Plot");
        await SetPlotActive(a, plotId, false);
        await Problem(await PostRevision(a, plotId, Revision(Bigger)), HttpStatusCode.Conflict);
        // Deactivating the plot does not supersede its last known location.
        Assert.Equal(1, (await Json(await a.GetAsync($"/api/production-plots/{plotId}"))).GetProperty("currentRevision").GetProperty("number").GetInt32());
        await SetPlotActive(a, plotId, true);
        await Json(await PostRevision(a, plotId, Revision(Bigger)), HttpStatusCode.Created);
        // An active plot of an inactive producer can still receive corrected revisions.
        await Json(await PutProducer(a, producer, "Prod", false));
        var corrected = await Json(await PostRevision(a, plotId, Revision(Biggest)), HttpStatusCode.Created);
        Assert.Equal(3, corrected.GetProperty("number").GetInt32());
    }

    [Fact]
    public async Task Exactly_one_current_revision_exists_even_under_concurrent_requests()
    {
        var (a, org, _, _) = await Tenants(); var (plotId, _) = await CreatePlot(a, await CreateProducer(a));
        var geometries = new[] { Bigger, Biggest, """{"type":"Polygon","coordinates":[[[0,0],[4,0],[4,4],[0,4],[0,0]]]}""", """{"type":"Polygon","coordinates":[[[0,0],[5,0],[5,5],[0,5],[0,0]]]}""" };
        var responses = await Task.WhenAll(geometries.Select(g => PostRevision(a, plotId, Revision(g))));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var numbers = new List<int>(); foreach (var r in responses) numbers.Add((await Json(r)).GetProperty("number").GetInt32());
        Assert.Equal([2, 3, 4, 5], numbers.Order().ToArray());
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org);
        Assert.Equal(1, await db.PlotRevisions.CountAsync(x => x.ProductionPlotId == plotId && x.SupersededAtUtc == null));
        Assert.Equal(5, await db.PlotRevisions.CountAsync(x => x.ProductionPlotId == plotId));
        Assert.Equal(5, (await Json(await a.GetAsync($"/api/production-plots/{plotId}"))).GetProperty("currentRevision").GetProperty("number").GetInt32());

        // Identical concurrent requests: one wins, the other is a no-op conflict, never two current revisions.
        var duplicate = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => PostRevision(a, plotId, Revision("""{"type":"Point","coordinates":[1,1]}"""))));
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], duplicate.Select(x => x.StatusCode).Order().ToArray());
        Assert.Equal(1, await db.PlotRevisions.CountAsync(x => x.ProductionPlotId == plotId && x.SupersededAtUtc == null));
    }

    [Fact]
    public async Task Superseded_revisions_stay_retrievable_and_history_is_newest_first()
    {
        var (a, _, _, _) = await Tenants(); var (plotId, created) = await CreatePlot(a, await CreateProducer(a), Revision(Square, country: "BR", area: "1.5", note: "first"));
        var rev1 = created.GetProperty("currentRevision").GetProperty("id").GetGuid();
        await Json(await PostRevision(a, plotId, Revision(Bigger, country: "CO", area: "9")), HttpStatusCode.Created);
        await Json(await PostRevision(a, plotId, Revision(Point, country: "CO")), HttpStatusCode.Created);

        var old = await Json(await a.GetAsync($"/api/production-plots/{plotId}/revisions/{rev1}"));
        Assert.Equal("BR", old.GetProperty("countryCode").GetString()); Assert.Equal(1.5m, old.GetProperty("areaHectares").GetDecimal());
        Assert.Equal("first", old.GetProperty("note").GetString()); Assert.Equal("Polygon", old.GetProperty("geometryType").GetString());
        Assert.NotEqual(JsonValueKind.Null, old.GetProperty("supersededAtUtc").ValueKind);
        Assert.Equal(1.0, old.GetProperty("geometry").GetProperty("coordinates")[0][1][0].GetDouble());
        var history = await Json(await a.GetAsync($"/api/production-plots/{plotId}/revisions"));
        Assert.Equal([3, 2, 1], history.EnumerateArray().Select(x => x.GetProperty("number").GetInt32()).ToArray());
        Assert.False(history[0].TryGetProperty("geometry", out _), "History entries are summaries without geometry.");
        Assert.Equal(JsonValueKind.Null, history[0].GetProperty("supersededAtUtc").ValueKind);
        Assert.Equal(2, history.EnumerateArray().Count(x => x.GetProperty("supersededAtUtc").ValueKind != JsonValueKind.Null));
        AssertNoOrganizationId(history);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await a.DeleteAsync($"/api/production-plots/{plotId}/revisions/{rev1}")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await a.PutAsync($"/api/production-plots/{plotId}/revisions/{rev1}", Raw("{}"))).StatusCode);
    }

    [Fact]
    public async Task Geometry_is_validated_structurally_and_accepts_point_polygon_and_multipolygon()
    {
        var (a, _, _, _) = await Tenants(); var producer = await CreateProducer(a); var (plotId, _) = await CreatePlot(a, producer);
        var invalid = new[]
        {
            Revision("""{"type":"Polygon","coordinates":[[[0,0],[1,1],[0,1],[1,0],[0,0]]]}"""), // bowtie
            Revision("""{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,1]]]}"""), // unclosed
            Revision("""{"type":"Point","coordinates":[181,0]}"""),
            Revision("""{"type":"Point","coordinates":[0]}"""),
            Revision("""{"type":"LineString","coordinates":[[0,0],[1,1]]}"""),
            Revision("""{"type":"Feature","properties":{},"geometry":{"type":"Point","coordinates":[0,0]}}"""),
            Revision("""{"type":"FeatureCollection","features":[]}"""),
            Revision("\"just a string\""), Revision("null"), Revision("[]"),
            """{"countryCode":"BR","areaHectares":null}""", // missing geometry
            """{"geometry":{"type":"Point","coordinates":[0,0]}}""", // missing country
            Revision(Point, country: "br"), Revision(Point, country: "BRA"), Revision(Point, area: "0"), Revision(Point, area: "-3"), Revision(Point, area: "1.23456"),
            Revision(Point, note: new string('n', 4001)),
        };
        foreach (var body in invalid)
        {
            await Problem(await PostRevision(a, plotId, body), HttpStatusCode.BadRequest);
            await Problem(await a.PostAsync("/api/production-plots", Raw(PlotBody(producer, "Bad", body))), HttpStatusCode.BadRequest);
        }
        var oversize = Revision($$"""{"type":"Point","coordinates":[0,0],"pad":"{{new string('x', 5 * 1024 * 1024 + 1)}}"}""");
        await Problem(await PostRevision(a, plotId, oversize), HttpStatusCode.BadRequest);
        // Failed requests created nothing.
        Assert.Single((await Json(await a.GetAsync($"/api/production-plots/{plotId}/revisions"))).EnumerateArray());

        var point = await Json(await PostRevision(a, plotId, Revision(Point, area: "0.5")), HttpStatusCode.Created);
        Assert.Equal("Point", point.GetProperty("geometryType").GetString());
        var multi = await Json(await PostRevision(a, plotId, Revision(Multi)), HttpStatusCode.Created);
        Assert.Equal("MultiPolygon", multi.GetProperty("geometryType").GetString());
        var (pointPlot, _) = await CreatePlot(a, producer, Revision(Point));
        Assert.Equal("Point", (await Json(await a.GetAsync($"/api/production-plots/{pointPlot}"))).GetProperty("currentRevision").GetProperty("geometryType").GetString());
    }

    [Fact]
    public async Task Responses_serialize_geometry_as_an_object_and_never_expose_the_organization()
    {
        var (a, org, _, _) = await Tenants(); var producer = await Json(await a.PostAsJsonAsync("/api/producers", new { name = Unique("Ser") }), HttpStatusCode.Created);
        var (plotId, created) = await CreatePlot(a, producer.GetProperty("id").GetGuid(), Revision(Multi, area: "2"));
        var revision = await Json(await PostRevision(a, plotId, Revision(Square)), HttpStatusCode.Created);
        var plot = await Json(await a.GetAsync($"/api/production-plots/{plotId}"));
        foreach (var body in new[] { producer, created, revision, plot, await Json(await a.GetAsync($"/api/production-plots/{plotId}/revisions/{revision.GetProperty("id").GetGuid()}")), await Json(await a.GetAsync("/api/production-plots?pageSize=5")), await Json(await a.GetAsync("/api/producers?pageSize=5")) })
            AssertNoOrganizationId(body);
        Assert.Equal(JsonValueKind.Object, created.GetProperty("currentRevision").GetProperty("geometry").ValueKind);
        Assert.Equal(JsonValueKind.Object, revision.GetProperty("geometry").ValueKind);
        Assert.Equal("Polygon", revision.GetProperty("geometry").GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Object, plot.GetProperty("currentRevision").GetProperty("geometry").ValueKind);
        Assert.Equal("MultiPolygon", created.GetProperty("currentRevision").GetProperty("geometryType").GetString());

        // Audit events carry identifiers only: never coordinates or geometry.
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org);
        var events = await db.AuditEvents.AsNoTracking().Where(x => x.EventType.StartsWith("Producer") || x.EventType.StartsWith("ProductionPlot") || x.EventType.StartsWith("PlotRevision")).ToListAsync();
        foreach (var type in new[] { "ProducerCreated", "ProductionPlotCreated", "PlotRevisionCreated" }) Assert.Contains(events, e => e.EventType == type);
        Assert.All(events, e => { Assert.Null(e.EudrCaseId); Assert.DoesNotContain("coordinates", e.Description + e.MetadataJson, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("Polygon", e.Description, StringComparison.Ordinal); });
        Assert.Contains(events, e => e.Description.Contains(revision.GetProperty("id").GetGuid().ToString()));
        await Json(await PutProducer(a, producer.GetProperty("id").GetGuid(), "Renamed", true));
        await SetPlotActive(a, plotId, true, "Renamed plot");
        events = await db.AuditEvents.AsNoTracking().ToListAsync();
        Assert.Contains(events, e => e.EventType == "ProducerUpdated"); Assert.Contains(events, e => e.EventType == "ProductionPlotUpdated");
    }

    [Fact]
    public async Task Other_tenants_cannot_see_or_change_provenance()
    {
        var (a, _, b, _) = await Tenants(); var tag = Unique("iso");
        var producer = await CreateProducer(a, tag + " producer"); var (plotId, created) = await CreatePlot(a, producer, name: tag + " plot");
        var revisionId = created.GetProperty("currentRevision").GetProperty("id").GetGuid();

        foreach (var path in new[] { $"/api/producers/{producer}", $"/api/production-plots/{plotId}", $"/api/production-plots/{plotId}/revisions", $"/api/production-plots/{plotId}/revisions/{revisionId}" })
            await Problem(await b.GetAsync(path), HttpStatusCode.NotFound);
        await Problem(await PutProducer(b, producer, "Hijack", true), HttpStatusCode.NotFound);
        await Problem(await b.PutAsJsonAsync($"/api/production-plots/{plotId}", new { name = "Hijack", isActive = false }), HttpStatusCode.NotFound);
        await Problem(await PostRevision(b, plotId, Revision(Bigger)), HttpStatusCode.NotFound);
        Assert.Equal(0, (await Json(await b.GetAsync($"/api/producers?search={tag}"))).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Json(await b.GetAsync($"/api/production-plots?search={tag}"))).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Json(await b.GetAsync($"/api/production-plots?producerId={producer}"))).GetProperty("totalCount").GetInt32());
        // A foreign producer in the body is a validation error that does not reveal the other organization.
        var problem = await Problem(await b.PostAsync("/api/production-plots", Raw(PlotBody(producer, "Stolen", Revision(Square)))), HttpStatusCode.BadRequest);
        Assert.Equal("Producer was not found in your organization.", problem.GetProperty("detail").GetString());
        Assert.Equal("Producer was not found in your organization.", (await Problem(await b.PostAsync("/api/production-plots", Raw(PlotBody(Guid.NewGuid(), "Unknown", Revision(Square)))), HttpStatusCode.BadRequest)).GetProperty("detail").GetString());
        // Nothing of A changed.
        var plot = await Json(await a.GetAsync($"/api/production-plots/{plotId}"));
        Assert.Equal(tag + " plot", plot.GetProperty("name").GetString()); Assert.True(plot.GetProperty("isActive").GetBoolean());
        Assert.Single((await Json(await a.GetAsync($"/api/production-plots/{plotId}/revisions"))).EnumerateArray());
    }

    [Fact]
    public async Task Revision_ids_are_scoped_to_their_own_plot_and_unknown_ids_are_not_found()
    {
        var (a, _, _, _) = await Tenants(); var producer = await CreateProducer(a);
        var (plot1, created1) = await CreatePlot(a, producer); var (plot2, created2) = await CreatePlot(a, producer);
        var rev1 = created1.GetProperty("currentRevision").GetProperty("id").GetGuid(); var rev2 = created2.GetProperty("currentRevision").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync($"/api/production-plots/{plot1}/revisions/{rev1}")).StatusCode);
        await Problem(await a.GetAsync($"/api/production-plots/{plot1}/revisions/{rev2}"), HttpStatusCode.NotFound);
        await Problem(await a.GetAsync($"/api/production-plots/{plot2}/revisions/{rev1}"), HttpStatusCode.NotFound);
        await Problem(await a.GetAsync($"/api/production-plots/{plot1}/revisions/{Guid.NewGuid()}"), HttpStatusCode.NotFound);
        await Problem(await a.GetAsync($"/api/production-plots/{Guid.NewGuid()}/revisions/{rev1}"), HttpStatusCode.NotFound);
        await Problem(await a.GetAsync($"/api/production-plots/{Guid.NewGuid()}/revisions"), HttpStatusCode.NotFound);
        await Problem(await PostRevision(a, Guid.NewGuid(), Revision(Bigger)), HttpStatusCode.NotFound);
        await Problem(await a.GetAsync($"/api/production-plots/{Guid.NewGuid()}"), HttpStatusCode.NotFound);
        // A revision id is never accepted where a producer id is expected.
        await Problem(await a.GetAsync($"/api/producers/{rev1}"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Case_geolocation_evidence_is_untouched_by_provenance_activity()
    {
        var (a, org, _, _) = await Tenants();
        var s = await Json(await a.PostAsJsonAsync("/api/suppliers", new { name = "Prov Supplier", externalReference = Unique("SUP") }));
        var p = await Json(await a.PostAsJsonAsync("/api/products", new { name = "Prov Coffee", cnCode = "0901", commodityType = "Coffee", externalReference = Unique("P"), isActive = true }));
        var shipment = await Json(await a.PostAsJsonAsync("/api/shipments", new { referenceNumber = Unique("IMP-PROV"), supplierId = s.GetProperty("id").GetGuid(), productId = p.GetProperty("id").GetGuid(), quantity = 10, unit = "Kg", originCountryCode = "BR" }));
        var caseId = shipment.GetProperty("eudrCaseId").GetGuid();
        using (var body = new MultipartFormDataContent())
        {
            var file = new ByteArrayContent(Encoding.UTF8.GetBytes(GeoV1)); file.Headers.ContentType = new MediaTypeHeaderValue("application/geo+json"); body.Add(file, "file", "plot.geojson");
            await Json(await a.PostAsync($"/api/eudr-cases/{caseId}/geolocation", body));
        }
        var geoBefore = (await a.GetStringAsync($"/api/eudr-cases/{caseId}/geolocation")); var historyBefore = await a.GetStringAsync($"/api/eudr-cases/{caseId}/evidence-history");
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org);
        var rowsBefore = await db.Geolocations.AsNoTracking().CountAsync();

        var producer = await CreateProducer(a); var (plotId, _) = await CreatePlot(a, producer, Revision(GeoV1));
        await Json(await PostRevision(a, plotId, Revision(Bigger)), HttpStatusCode.Created);

        Assert.Equal(geoBefore, await a.GetStringAsync($"/api/eudr-cases/{caseId}/geolocation"));
        Assert.Equal(historyBefore, await a.GetStringAsync($"/api/eudr-cases/{caseId}/evidence-history"));
        Assert.Equal(rowsBefore, await db.Geolocations.AsNoTracking().CountAsync());
        // Point is still rejected for case uploads, even though plots accept it.
        using var pointBody = new MultipartFormDataContent(); var pointFile = new ByteArrayContent(Encoding.UTF8.GetBytes(Point)); pointFile.Headers.ContentType = new MediaTypeHeaderValue("application/geo+json"); pointBody.Add(pointFile, "file", "point.geojson");
        await Problem(await a.PostAsync($"/api/eudr-cases/{caseId}/geolocation", pointBody), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Provenance_routes_require_authentication()
    {
        using var anonymous = factory.CreateClient(); var id = Guid.NewGuid();
        foreach (var (method, path) in new (HttpMethod, string)[]
        {
            (HttpMethod.Get, "/api/producers"), (HttpMethod.Get, $"/api/producers/{id}"), (HttpMethod.Post, "/api/producers"), (HttpMethod.Put, $"/api/producers/{id}"),
            (HttpMethod.Get, "/api/production-plots"), (HttpMethod.Get, $"/api/production-plots/{id}"), (HttpMethod.Post, "/api/production-plots"), (HttpMethod.Put, $"/api/production-plots/{id}"),
            (HttpMethod.Post, $"/api/production-plots/{id}/revisions"), (HttpMethod.Get, $"/api/production-plots/{id}/revisions"), (HttpMethod.Get, $"/api/production-plots/{id}/revisions/{id}"),
        })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(new HttpRequestMessage(method, path))).StatusCode);
    }
}
