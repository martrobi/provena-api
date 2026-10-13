using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Provena.Api.Auth;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
using Xunit;
namespace Provena.Api.IntegrationTests;

// Slice 4 / commit 2: internal case dossier API (GET dossier, PUT lot, GET lot-history), the shipment lock and the export additions.
// Tenants are provisioned through AuthService directly because the shared auth rate limiter (10 / 5 minutes) is nearly consumed by other classes.
[Collection("Api")]
public sealed class CaseDossierTests(ApiFactory factory)
{
    private const string Square = """{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,1],[0,0]]]}""";
    private const string Bigger = """{"type":"Polygon","coordinates":[[[0,0],[2,0],[2,2],[0,2],[0,0]]]}""";
    private const string GeoV1 = """{"type":"Polygon","coordinates":[[[-48,-20],[-47,-20],[-47,-19],[-48,-20]]]}""";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (string TokenA, Guid OrgA, string TokenB, Guid OrgB)? tenants;
    private async Task<(HttpClient A, Guid OrgA, HttpClient B, Guid OrgB)> Tenants()
    {
        await Gate.WaitAsync();
        try { tenants ??= await Provision(); } finally { Gate.Release(); }
        return (Client(tenants.Value.TokenA), tenants.Value.OrgA, Client(tenants.Value.TokenB), tenants.Value.OrgB);
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
        var a = await One("Dossier-" + Guid.NewGuid().ToString("N")[..8]); var b = await One("Dossier-" + Guid.NewGuid().ToString("N")[..8]);
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
    private static async Task Status(Task<HttpResponseMessage> request, HttpStatusCode expected)
    { var response = await request; var body = await response.Content.ReadAsStringAsync(); Assert.True(expected == response.StatusCode, $"Expected {(int)expected}, got {(int)response.StatusCode}: {body}"); }
    private static StringContent Raw(string json) => new(json, Encoding.UTF8, "application/json");
    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..10];
    private AppDbContext TenantDb(IServiceScope scope, Guid org)
    {
        var tenant = new TenantContext(new HttpContextAccessor()); tenant.SetOrganization(org);
        return new AppDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>(), tenant);
    }

    // ---- fixtures -----------------------------------------------------------------------------------------------------------------------
    private sealed record CaseIds(Guid CaseId, Guid ShipmentId, Guid SupplierId, Guid ProductId, string Reference);
    private static async Task<CaseIds> NewCase(HttpClient c)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var s = await Json(await c.PostAsJsonAsync("/api/suppliers", new { name = "Supplier " + tag }));
        var p = await Json(await c.PostAsJsonAsync("/api/products", new { name = "Product " + tag, cnCode = "0901", commodityType = "Coffee" }));
        var reference = "DOS-" + tag;
        var shipment = await Json(await c.PostAsJsonAsync("/api/shipments", new { referenceNumber = reference, supplierId = s.GetProperty("id").GetGuid(), productId = p.GetProperty("id").GetGuid(), quantity = 10, unit = "Kg" }));
        return new(shipment.GetProperty("eudrCaseId").GetGuid(), shipment.GetProperty("shipment").GetProperty("id").GetGuid(), s.GetProperty("id").GetGuid(), p.GetProperty("id").GetGuid(), reference);
    }
    private static async Task<Guid> NewProducer(HttpClient c, string? name = null) =>
        (await Json(await c.PostAsJsonAsync("/api/producers", new { name = name ?? Unique("Producer"), externalReference = (string?)null, countryCode = "BR", notes = (string?)null }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
    private sealed record Plot(Guid Id, Guid Revision1);
    private static async Task<Plot> NewPlot(HttpClient c, Guid producerId, string? name = null, string geometry = Square)
    {
        var body = $$"""{"producerId":"{{producerId}}","name":"{{name ?? Unique("Plot")}}","externalReference":null,"notes":null,"initialRevision":{"countryCode":"BR","geometry":{{geometry}},"areaHectares":1.5,"note":null} }""";
        var json = await Json(await c.PostAsync("/api/production-plots", Raw(body)), HttpStatusCode.Created);
        return new(json.GetProperty("id").GetGuid(), json.GetProperty("currentRevision").GetProperty("id").GetGuid());
    }
    private static async Task<Guid> NewRevision(HttpClient c, Guid plotId, string geometry)
    {
        var body = $$"""{"countryCode":"BR","geometry":{{geometry}},"areaHectares":null,"note":null}""";
        return (await Json(await c.PostAsync($"/api/production-plots/{plotId}/revisions", Raw(body)), HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }
    private static Task SetProducerActive(HttpClient c, Guid id, string name, bool active) =>
        Status(c.PutAsJsonAsync($"/api/producers/{id}", new { name, externalReference = (string?)null, countryCode = "BR", notes = (string?)null, isActive = active }), HttpStatusCode.OK);
    private static Task SetPlotActive(HttpClient c, Guid id, string name, bool active) =>
        Status(c.PutAsJsonAsync($"/api/production-plots/{id}", new { name, externalReference = (string?)null, notes = (string?)null, isActive = active }), HttpStatusCode.OK);

    private static object Link(Guid plotId, Guid revisionId, string? start = null, string? end = null) => new { productionPlotId = plotId, plotRevisionId = revisionId, productionStartDate = start, productionEndDate = end };
    private static object Contribution(Guid producerId, decimal quantity, params object[] plots) => new { producerId, quantity, unit = "Kg", plots };
    private static object Lot(string reference, decimal quantity, params object[] contributions) => new { lotReference = reference, quantity, unit = "Kg", note = (string?)null, contributions };
    private static Task<HttpResponseMessage> Put(HttpClient c, Guid caseId, object body) => c.PutAsJsonAsync($"/api/eudr-cases/{caseId}/dossier/lot", body);
    private static async Task<JsonElement> Save(HttpClient c, Guid caseId, object body) => await Json(await Put(c, caseId, body), HttpStatusCode.OK);
    private static async Task<JsonElement> Dossier(HttpClient c, Guid caseId) => await Json(await c.GetAsync($"/api/eudr-cases/{caseId}/dossier"), HttpStatusCode.OK);
    private static async Task<JsonElement> History(HttpClient c, Guid caseId) => await Json(await c.GetAsync($"/api/eudr-cases/{caseId}/dossier/lot-history"), HttpStatusCode.OK);
    private static Guid LotId(JsonElement dossier) => dossier.GetProperty("currentLot").GetProperty("id").GetGuid();
    private static int LotNumber(JsonElement dossier) => dossier.GetProperty("currentLot").GetProperty("number").GetInt32();
    private static async Task<JsonElement> Problem(HttpResponseMessage response, HttpStatusCode status)
    {
        var json = await Json(response, status);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return json;
    }
    private static void AssertNoOrganizationId(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object: foreach (var p in element.EnumerateObject()) { Assert.NotEqual("organizationid", p.Name.ToLowerInvariant()); AssertNoOrganizationId(p.Value); } break;
            case JsonValueKind.Array: foreach (var item in element.EnumerateArray()) AssertNoOrganizationId(item); break;
        }
    }
    // A producer with one plot at revision 1: the smallest attributable source.
    private sealed record Source(Guid ProducerId, string ProducerName, Plot Plot, string PlotName);
    private static async Task<Source> NewSource(HttpClient c, string producerPrefix = "Producer", string plotPrefix = "Plot", string geometry = Square)
    {
        var producerName = Unique(producerPrefix); var producerId = await NewProducer(c, producerName);
        var plotName = Unique(plotPrefix); return new(producerId, producerName, await NewPlot(c, producerId, plotName, geometry), plotName);
    }
    private static object Single(Source s, decimal quantity, string? start = null, string? end = null) => Contribution(s.ProducerId, quantity, Link(s.Plot.Id, s.Plot.Revision1, start, end));

    // ---- A. lifecycle -------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Lifecycle_no_dossier_first_save_creates_lot_one_and_each_material_save_supersedes()
    {
        var (a, orgA, _, _) = await Tenants(); var ids = await NewCase(a); var source = await NewSource(a);
        var empty = await a.GetAsync($"/api/eudr-cases/{ids.CaseId}/dossier");
        Assert.Equal(HttpStatusCode.NoContent, empty.StatusCode); Assert.Empty(await empty.Content.ReadAsByteArrayAsync());
        Assert.Equal(0, (await History(a, ids.CaseId)).GetArrayLength());

        var reference = Unique("LOT");
        var first = await Save(a, ids.CaseId, Lot(reference, 10, Single(source, 10)));
        Assert.Equal(1, LotNumber(first)); Assert.Equal(ids.CaseId, first.GetProperty("eudrCaseId").GetGuid());
        Assert.Equal(10m, first.GetProperty("shipmentQuantity").GetDecimal()); Assert.Equal("Kg", first.GetProperty("shipmentUnit").GetString());
        Assert.Equal("Reconciled", first.GetProperty("currentLot").GetProperty("reconciliation").GetProperty("status").GetString());
        AssertNoOrganizationId(first);

        var second = await Save(a, ids.CaseId, Lot(reference, 20, Single(source, 20)));
        Assert.Equal(2, LotNumber(second)); Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        Assert.NotEqual(LotId(first), LotId(second));
        Assert.Equal(2, LotNumber(await Dossier(a, ids.CaseId)));

        var history = await History(a, ids.CaseId);
        Assert.Equal([2, 1], history.EnumerateArray().Select(x => x.GetProperty("number").GetInt32()).ToArray());
        Assert.Equal(JsonValueKind.Null, history[0].GetProperty("supersededAtUtc").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, history[1].GetProperty("supersededAtUtc").ValueKind);
        Assert.Equal(10m, history[1].GetProperty("quantity").GetDecimal());

        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, orgA);
        var dossier = await db.CaseDossiers.SingleAsync(x => x.EudrCaseId == ids.CaseId);
        Assert.Single(await db.CaseLots.Current().Where(x => x.CaseDossierId == dossier.Id).ToListAsync());
        Assert.Equal(2, await db.CaseLots.CountAsync(x => x.CaseDossierId == dossier.Id));
        Assert.Equal(dossier.Id, first.GetProperty("id").GetGuid());
    }

    // ---- B. multiple sources ------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Multiple_producers_and_plots_reconcile_and_are_ordered_deterministically()
    {
        var (a, _, _, _) = await Tenants(); var ids = await NewCase(a);
        var beta = await NewSource(a, "Beta"); var alpha = await NewSource(a, "Alpha");
        var alphaSecondPlot = await NewPlot(a, alpha.ProducerId, "Zeta-" + Guid.NewGuid().ToString("N")[..6], Bigger);
        var body = Lot(Unique("LOT"), 10,
            Contribution(beta.ProducerId, 4, Link(beta.Plot.Id, beta.Plot.Revision1, "2024-01-01", "2024-06-30")),
            Contribution(alpha.ProducerId, 6, Link(alphaSecondPlot.Id, alphaSecondPlot.Revision1), Link(alpha.Plot.Id, alpha.Plot.Revision1, "2024-02-01", "2024-02-01")));
        var dossier = await Save(a, ids.CaseId, body);
        var lot = dossier.GetProperty("currentLot"); var reconciliation = lot.GetProperty("reconciliation");
        Assert.Equal("Reconciled", reconciliation.GetProperty("status").GetString()); Assert.Equal(10m, reconciliation.GetProperty("allocatedQuantity").GetDecimal()); Assert.Equal(0m, reconciliation.GetProperty("remainingQuantity").GetDecimal());
        var contributions = lot.GetProperty("contributions");
        Assert.Equal([alpha.ProducerName, beta.ProducerName], contributions.EnumerateArray().Select(x => x.GetProperty("producerName").GetString()!).ToArray());
        var alphaPlots = contributions[0].GetProperty("plots");
        Assert.StartsWith("Plot-", alphaPlots[0].GetProperty("plotName").GetString()); Assert.StartsWith("Zeta-", alphaPlots[1].GetProperty("plotName").GetString());
        Assert.Equal("2024-02-01", alphaPlots[0].GetProperty("productionStartDate").GetString()); Assert.Equal("2024-02-01", alphaPlots[0].GetProperty("productionEndDate").GetString());
        Assert.Equal(JsonValueKind.Null, alphaPlots[1].GetProperty("productionStartDate").ValueKind);
        Assert.Equal("2024-06-30", contributions[1].GetProperty("plots")[0].GetProperty("productionEndDate").GetString());
        Assert.Equal(JsonValueKind.Number, contributions[0].GetProperty("quantity").ValueKind);
        Assert.Equal("Polygon", alphaPlots[0].GetProperty("geometryType").GetString()); Assert.Equal("BR", alphaPlots[0].GetProperty("countryCode").GetString());
        Assert.False(alphaPlots[0].TryGetProperty("geometry", out _), "Dossier DTOs never carry geometry.");
    }

    // ---- C. revision pinning ------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Lot_pins_the_exact_revision_and_later_revisions_do_not_rewrite_it()
    {
        var (a, orgA, _, _) = await Tenants(); var ids = await NewCase(a); var source = await NewSource(a);
        var reference = Unique("LOT");
        await Save(a, ids.CaseId, Lot(reference, 10, Single(source, 10)));
        var rev2 = await NewRevision(a, source.Plot.Id, Bigger);

        var link = (await Dossier(a, ids.CaseId)).GetProperty("currentLot").GetProperty("contributions")[0].GetProperty("plots")[0];
        Assert.Equal(source.Plot.Revision1, link.GetProperty("plotRevisionId").GetGuid()); Assert.Equal(1, link.GetProperty("revisionNumber").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, link.GetProperty("revisionSupersededAtUtc").ValueKind);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await using var db = TenantDb(scope, orgA);
            Assert.Equal(source.Plot.Revision1, (await db.ContributionPlots.SingleAsync(x => x.ProductionPlotId == source.Plot.Id)).PlotRevisionId);
        }
        // Explicitly pinning an already-superseded revision is allowed (here as a quantity-neutral second version with a note-free change of quantity).
        var repinned = await Save(a, ids.CaseId, Lot(reference, 12, Contribution(source.ProducerId, 12, Link(source.Plot.Id, source.Plot.Revision1))));
        Assert.Equal(2, LotNumber(repinned));
        // Pinning the new revision is a different (new) attribution and keeps the earlier version pointing at revision 1.
        var moved = await Save(a, ids.CaseId, Lot(reference, 12, Contribution(source.ProducerId, 12, Link(source.Plot.Id, rev2))));
        Assert.Equal(3, LotNumber(moved));
        var movedLink = moved.GetProperty("currentLot").GetProperty("contributions")[0].GetProperty("plots")[0];
        Assert.Equal(2, movedLink.GetProperty("revisionNumber").GetInt32()); Assert.Equal(JsonValueKind.Null, movedLink.GetProperty("revisionSupersededAtUtc").ValueKind);
        var history = await History(a, ids.CaseId);
        Assert.Equal(source.Plot.Revision1, history[1].GetProperty("contributions")[0].GetProperty("plots")[0].GetProperty("plotRevisionId").GetGuid());
        Assert.Equal(rev2, history[0].GetProperty("contributions")[0].GetProperty("plots")[0].GetProperty("plotRevisionId").GetGuid());
    }

    // ---- D. active / carry-over ---------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task New_attribution_requires_active_producer_and_plot()
    {
        var (a, _, _, _) = await Tenants(); var ids = await NewCase(a);
        var inactiveProducer = await NewSource(a); await SetProducerActive(a, inactiveProducer.ProducerId, inactiveProducer.ProducerName, false);
        var problem = await Problem(await Put(a, ids.CaseId, Lot(Unique("LOT"), 10, Single(inactiveProducer, 10))), HttpStatusCode.Conflict);
        Assert.Contains("Producer is inactive", problem.GetProperty("detail").GetString());
        var inactivePlot = await NewSource(a); await SetPlotActive(a, inactivePlot.Plot.Id, inactivePlot.PlotName, false);
        Assert.Contains("Production plot is inactive", (await Problem(await Put(a, ids.CaseId, Lot(Unique("LOT"), 10, Single(inactivePlot, 10))), HttpStatusCode.Conflict)).GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await a.GetAsync($"/api/eudr-cases/{ids.CaseId}/dossier")).StatusCode);
    }

    [Fact]
    public async Task Carried_over_sources_stay_usable_after_deactivation_and_may_be_corrected()
    {
        var (a, _, _, _) = await Tenants(); var ids = await NewCase(a); var source = await NewSource(a); var reference = Unique("LOT");
        var rev2 = await NewRevision(a, source.Plot.Id, Bigger);
        var other = await NewPlot(a, source.ProducerId); // Registered while the producer is still active.
        await Save(a, ids.CaseId, Lot(reference, 10, Single(source, 10, "2024-01-01", "2024-03-31")));
        await SetPlotActive(a, source.Plot.Id, source.PlotName, false); await SetProducerActive(a, source.ProducerId, source.ProducerName, false);

        // Quantity correction on a carried-over contribution, and a production-period correction on a carried-over link.
        var corrected = await Save(a, ids.CaseId, Lot(reference, 12, Single(source, 12, "2024-01-01", "2024-03-31")));
        Assert.Equal(2, LotNumber(corrected));
        var period = await Save(a, ids.CaseId, Lot(reference, 12, Single(source, 12, "2024-01-15", "2024-04-30")));
        Assert.Equal(3, LotNumber(period));
        var link = period.GetProperty("currentLot").GetProperty("contributions")[0];
        Assert.False(link.GetProperty("producerIsActive").GetBoolean()); Assert.False(link.GetProperty("plots")[0].GetProperty("plotIsActive").GetBoolean());
        // Switching the inactive plot to another revision is a new attribution.
        await Problem(await Put(a, ids.CaseId, Lot(reference, 12, Contribution(source.ProducerId, 12, Link(source.Plot.Id, rev2)))), HttpStatusCode.Conflict);
        // A new plot under a carried-over (now inactive) producer is also new attribution.
        await Problem(await Put(a, ids.CaseId, Lot(reference, 12, Contribution(source.ProducerId, 12, Link(source.Plot.Id, source.Plot.Revision1, "2024-01-15", "2024-04-30"), Link(other.Id, other.Revision1)))), HttpStatusCode.Conflict);
        Assert.Equal(3, LotNumber(await Dossier(a, ids.CaseId)));
        // Dropping a carried-over source is allowed.
        Assert.Equal(4, LotNumber(await Save(a, ids.CaseId, Lot(reference, 12))));
    }

    // ---- E. reconciliation --------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Reconciliation_is_derived_under_allocation_is_stored_and_over_allocation_is_rejected()
    {
        var (a, _, _, _) = await Tenants(); var ids = await NewCase(a); var s1 = await NewSource(a); var s2 = await NewSource(a); var reference = Unique("LOT");
        var none = (await Save(a, ids.CaseId, Lot(reference, 10))).GetProperty("currentLot").GetProperty("reconciliation");
        Assert.Equal("UnderAllocated", none.GetProperty("status").GetString()); Assert.Equal(0m, none.GetProperty("allocatedQuantity").GetDecimal()); Assert.Equal(10m, none.GetProperty("remainingQuantity").GetDecimal());
        var partial = (await Save(a, ids.CaseId, Lot(reference, 10, Single(s1, 6.25m)))).GetProperty("currentLot").GetProperty("reconciliation");
        Assert.Equal("UnderAllocated", partial.GetProperty("status").GetString()); Assert.Equal(3.75m, partial.GetProperty("remainingQuantity").GetDecimal());
        var exact = (await Save(a, ids.CaseId, Lot(reference, 10, Single(s1, 6.25m), Single(s2, 3.75m)))).GetProperty("currentLot");
        Assert.Equal("Reconciled", exact.GetProperty("reconciliation").GetProperty("status").GetString()); Assert.Equal(0m, exact.GetProperty("reconciliation").GetProperty("remainingQuantity").GetDecimal());
        var versions = (await History(a, ids.CaseId)).GetArrayLength();
        await Problem(await Put(a, ids.CaseId, Lot(reference, 10, Single(s1, 6.25m), Single(s2, 3.750001m))), HttpStatusCode.BadRequest);
        Assert.Equal(versions, (await History(a, ids.CaseId)).GetArrayLength());
        Assert.Equal(3, LotNumber(await Dossier(a, ids.CaseId)));
        // The lot quantity is independent of the shipment quantity (10 Kg here).
        Assert.Equal("Reconciled", (await Save(a, ids.CaseId, Lot(reference, 25, Single(s1, 25)))).GetProperty("currentLot").GetProperty("reconciliation").GetProperty("status").GetString());
    }

    // ---- F. units / periods -------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Units_and_production_periods_are_validated()
    {
        var (a, _, _, _) = await Tenants(); var ids = await NewCase(a); var s = await NewSource(a); var reference = Unique("LOT");
        await Problem(await Put(a, ids.CaseId, Lot(reference, 10, new { producerId = s.ProducerId, quantity = 10m, unit = "Ton", plots = Array.Empty<object>() })), HttpStatusCode.BadRequest);
        await Problem(await Put(a, ids.CaseId, new { lotReference = reference, quantity = 10, unit = "Other", note = (string?)null, contributions = Array.Empty<object>() }), HttpStatusCode.BadRequest);
        await Problem(await Put(a, ids.CaseId, Lot(reference, 10, new { producerId = s.ProducerId, quantity = 10m, unit = "Other", plots = Array.Empty<object>() })), HttpStatusCode.BadRequest);
        await Problem(await Put(a, ids.CaseId, Lot(reference, 10, Single(s, 10, "2024-01-01", null))), HttpStatusCode.BadRequest);
        await Problem(await Put(a, ids.CaseId, Lot(reference, 10, Single(s, 10, null, "2024-01-01"))), HttpStatusCode.BadRequest);
        await Problem(await Put(a, ids.CaseId, Lot(reference, 10, Single(s, 10, "2024-02-01", "2024-01-01"))), HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.NoContent, (await a.GetAsync($"/api/eudr-cases/{ids.CaseId}/dossier")).StatusCode);
        var equal = await Save(a, ids.CaseId, Lot(reference, 10, Single(s, 10, "2024-01-01", "2024-01-01")));
        Assert.Equal("2024-01-01", equal.GetProperty("currentLot").GetProperty("contributions")[0].GetProperty("plots")[0].GetProperty("productionEndDate").GetString());
        await Save(a, ids.CaseId, Lot(reference, 10, Single(s, 10)));
    }

    // ---- G. wrong identifiers -----------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Unknown_mismatched_and_duplicate_body_ids_are_rejected_with_400()
    {
        var (a, _, _, _) = await Tenants(); var ids = await NewCase(a); var s1 = await NewSource(a); var s2 = await NewSource(a); var reference = Unique("LOT");
        var secondPlotOfS1 = await NewPlot(a, s1.ProducerId);
        // Plot owned by another producer.
        Assert.Contains("does not belong", (await Problem(await Put(a, ids.CaseId, Lot(reference, 10, Contribution(s1.ProducerId, 10, Link(s2.Plot.Id, s2.Plot.Revision1)))), HttpStatusCode.BadRequest)).GetProperty("detail").GetString());
        // Revision of another plot.
        await Problem(await Put(a, ids.CaseId, Lot(reference, 10, Contribution(s1.ProducerId, 10, Link(s1.Plot.Id, s2.Plot.Revision1)))), HttpStatusCode.BadRequest);
        // Unknown producer / plot / revision.
        Assert.Contains("Producer was not found", (await Problem(await Put(a, ids.CaseId, Lot(reference, 10, Contribution(Guid.NewGuid(), 10))), HttpStatusCode.BadRequest)).GetProperty("detail").GetString());
        await Problem(await Put(a, ids.CaseId, Lot(reference, 10, Contribution(s1.ProducerId, 10, Link(Guid.NewGuid(), s1.Plot.Revision1)))), HttpStatusCode.BadRequest);
        await Problem(await Put(a, ids.CaseId, Lot(reference, 10, Contribution(s1.ProducerId, 10, Link(s1.Plot.Id, Guid.NewGuid())))), HttpStatusCode.BadRequest);
        // Duplicates.
        await Problem(await Put(a, ids.CaseId, Lot(reference, 10, Single(s1, 5), Single(s1, 5))), HttpStatusCode.BadRequest);
        await Problem(await Put(a, ids.CaseId, Lot(reference, 10, Contribution(s1.ProducerId, 10, Link(s1.Plot.Id, s1.Plot.Revision1), Link(s1.Plot.Id, s1.Plot.Revision1)))), HttpStatusCode.BadRequest);
        // A well-formed lot over two plots of the same producer works.
        Assert.Equal(1, LotNumber(await Save(a, ids.CaseId, Lot(reference, 10, Contribution(s1.ProducerId, 10, Link(s1.Plot.Id, s1.Plot.Revision1), Link(secondPlotOfS1.Id, secondPlotOfS1.Revision1))))));
    }

    // ---- H. tenant isolation ------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Tenants_cannot_see_or_reference_each_others_dossiers_and_cases_stay_isolated()
    {
        var (a, _, b, _) = await Tenants(); var caseA = await NewCase(a); var otherCaseA = await NewCase(a); var caseB = await NewCase(b);
        var sourceA = await NewSource(a); var reference = Unique("LOT");
        await Save(a, caseA.CaseId, Lot(reference, 10, Single(sourceA, 10)));

        await Problem(await b.GetAsync($"/api/eudr-cases/{caseA.CaseId}/dossier"), HttpStatusCode.NotFound);
        await Problem(await b.GetAsync($"/api/eudr-cases/{caseA.CaseId}/dossier/lot-history"), HttpStatusCode.NotFound);
        await Problem(await Put(b, caseA.CaseId, Lot(Unique("LOT"), 10)), HttpStatusCode.NotFound);
        // B's own case referencing A's producer / plot / revision ids: non-disclosing 400.
        var foreign = await Problem(await Put(b, caseB.CaseId, Lot(Unique("LOT"), 10, Single(sourceA, 10))), HttpStatusCode.BadRequest);
        Assert.Contains("not found in your organization", foreign.GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await b.GetAsync($"/api/eudr-cases/{caseB.CaseId}/dossier")).StatusCode);
        // Another case of the same tenant has no dossier of its own.
        Assert.Equal(HttpStatusCode.NoContent, (await a.GetAsync($"/api/eudr-cases/{otherCaseA.CaseId}/dossier")).StatusCode);
        Assert.Equal(0, (await History(a, otherCaseA.CaseId)).GetArrayLength());
        Assert.Equal(1, (await History(a, caseA.CaseId)).GetArrayLength());
    }

    // ---- I. route before body -----------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Route_case_is_resolved_before_the_body_is_validated()
    {
        var (a, _, b, _) = await Tenants(); var ids = await NewCase(a); var s = await NewSource(a);
        var invalid = new { lotReference = "", quantity = -1, unit = "Other", note = (string?)null, contributions = new[] { Single(s, 1, "2024-02-01", "2024-01-01") } };
        await Problem(await Put(a, ids.CaseId, invalid), HttpStatusCode.BadRequest);
        await Problem(await Put(b, ids.CaseId, invalid), HttpStatusCode.NotFound);
        await Problem(await Put(a, Guid.NewGuid(), invalid), HttpStatusCode.NotFound);
        // Terminal case: EnsureEditable (409) runs before body validation (400).
        await Save(a, ids.CaseId, Lot(Unique("LOT"), 10, Single(s, 10)));
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync("""UPDATE "Cases" SET "Status" = {0} WHERE "Id" = {1}""", (int)EudrCaseStatus.Completed, ids.CaseId);
        await Problem(await Put(a, ids.CaseId, invalid), HttpStatusCode.Conflict);
    }

    // ---- J. lot reference ---------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Lot_reference_is_unique_among_current_lots_only_and_is_not_a_lot_identity()
    {
        var (a, _, b, _) = await Tenants(); var case1 = await NewCase(a); var case2 = await NewCase(a); var caseB = await NewCase(b);
        var reference = Unique("REF"); var renamed = Unique("REF");
        var first = await Save(a, case1.CaseId, Lot(reference, 10));
        var conflict = await Problem(await Put(a, case2.CaseId, Lot(reference, 5)), HttpStatusCode.Conflict);
        Assert.Contains("Lot reference is already used by another case", conflict.GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await a.GetAsync($"/api/eudr-cases/{case2.CaseId}/dossier")).StatusCode);
        // The same case keeps its reference on a new version.
        Assert.Equal(2, LotNumber(await Save(a, case1.CaseId, Lot(reference, 11))));
        // A trimmed duplicate still conflicts (exact comparison on the trimmed value).
        await Problem(await Put(a, case2.CaseId, Lot("  " + reference + "  ", 5)), HttpStatusCode.Conflict);
        // Another organization may use the same value.
        Assert.Equal(1, LotNumber(await Save(b, caseB.CaseId, Lot(reference, 3))));
        // Once case 1 no longer uses the reference, case 2 may; case 1's history still shows it and its dossier id is stable.
        var changed = await Save(a, case1.CaseId, Lot(renamed, 11));
        Assert.Equal(3, LotNumber(changed)); Assert.Equal(first.GetProperty("id").GetGuid(), changed.GetProperty("id").GetGuid());
        Assert.Equal(1, LotNumber(await Save(a, case2.CaseId, Lot(reference, 5))));
        Assert.Equal(reference, (await History(a, case1.CaseId))[1].GetProperty("lotReference").GetString());
        Assert.NotEqual((await Dossier(a, case2.CaseId)).GetProperty("id").GetGuid(), changed.GetProperty("id").GetGuid());
        // Case 1 can no longer take the reference back while case 2 holds it.
        await Problem(await Put(a, case1.CaseId, Lot(reference, 11)), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Two_cases_racing_for_the_same_reference_yield_one_success_and_one_conflict()
    {
        var (a, _, _, _) = await Tenants(); var case1 = await NewCase(a); var case2 = await NewCase(a); var reference = Unique("RACE");
        var responses = await Task.WhenAll(Put(a, case1.CaseId, Lot(reference, 1)), Put(a, case2.CaseId, Lot(reference, 1)));
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(x => x.StatusCode).Order().ToArray());
    }

    // ---- K. idempotency -----------------------------------------------------------------------------------------------------------------
    private async Task<int> LotSavedAudits(Guid orgId, Guid caseId)
    { await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, orgId); return await db.AuditEvents.CountAsync(x => x.EudrCaseId == caseId && x.EventType == "CaseLotSaved"); }

    [Fact]
    public async Task Identical_put_is_idempotent_regardless_of_ordering_and_decimal_form()
    {
        var (a, orgA, _, _) = await Tenants(); var ids = await NewCase(a); var s1 = await NewSource(a); var s2 = await NewSource(a);
        var extra = await NewPlot(a, s1.ProducerId); var reference = Unique("LOT");
        object Ordered(bool reversed)
        {
            var c1 = reversed ? Contribution(s1.ProducerId, 6, Link(extra.Id, extra.Revision1), Link(s1.Plot.Id, s1.Plot.Revision1, "2024-01-01", "2024-02-01"))
                              : Contribution(s1.ProducerId, 6, Link(s1.Plot.Id, s1.Plot.Revision1, "2024-01-01", "2024-02-01"), Link(extra.Id, extra.Revision1));
            var c2 = Single(s2, 4);
            return reversed ? Lot(reference, 10, c2, c1) : Lot(reference, 10, c1, c2);
        }
        var first = await Save(a, ids.CaseId, Ordered(false));
        var lotId = LotId(first);
        Assert.Equal(1, await LotSavedAudits(orgA, ids.CaseId));
        Assert.Equal(lotId, LotId(await Save(a, ids.CaseId, Ordered(false))));
        Assert.Equal(lotId, LotId(await Save(a, ids.CaseId, Ordered(true))));
        // 10.0 / 6.000000 / padded reference are the same facts.
        Assert.Equal(lotId, LotId(await Json(await Put(a, ids.CaseId, JsonDocument.Parse($$"""
            {"lotReference":" {{reference}} ","quantity":10.000000,"unit":"Kg","note":"  ","contributions":[
              {"producerId":"{{s2.ProducerId}}","quantity":4.0,"unit":"Kg","plots":[{"productionPlotId":"{{s2.Plot.Id}}","plotRevisionId":"{{s2.Plot.Revision1}}","productionStartDate":null,"productionEndDate":null}]},
              {"producerId":"{{s1.ProducerId}}","quantity":6.000000,"unit":"Kg","plots":[{"productionPlotId":"{{extra.Id}}","plotRevisionId":"{{extra.Revision1}}"},{"productionPlotId":"{{s1.Plot.Id}}","plotRevisionId":"{{s1.Plot.Revision1}}","productionStartDate":"2024-01-01","productionEndDate":"2024-02-01"}]}]}
            """).RootElement), HttpStatusCode.OK)));
        Assert.Equal(1, await LotSavedAudits(orgA, ids.CaseId)); Assert.Equal(1, (await History(a, ids.CaseId)).GetArrayLength());
        // A changed production period is a new version.
        var changed = await Save(a, ids.CaseId, Lot(reference, 10,
            Contribution(s1.ProducerId, 6, Link(s1.Plot.Id, s1.Plot.Revision1, "2024-01-01", "2024-03-01"), Link(extra.Id, extra.Revision1)), Single(s2, 4)));
        Assert.Equal(2, LotNumber(changed)); Assert.Equal(2, await LotSavedAudits(orgA, ids.CaseId));
    }

    // ---- L. shipment lock ---------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Shipment_cannot_be_edited_once_a_dossier_exists_but_can_still_be_archived()
    {
        var (a, _, _, _) = await Tenants(); var ids = await NewCase(a);
        object Body(decimal quantity) => new { referenceNumber = ids.Reference, supplierId = ids.SupplierId, productId = ids.ProductId, quantity, unit = "Kg" };
        await Status(a.PutAsJsonAsync($"/api/shipments/{ids.ShipmentId}", Body(11)), HttpStatusCode.OK);
        await Save(a, ids.CaseId, Lot(Unique("LOT"), 11));
        var conflict = await Problem(await a.PutAsJsonAsync($"/api/shipments/{ids.ShipmentId}", Body(99)), HttpStatusCode.Conflict);
        Assert.Contains("case dossier lot", conflict.GetProperty("detail").GetString());
        Assert.Equal(11m, (await Json(await a.GetAsync($"/api/shipments/{ids.ShipmentId}"))).GetProperty("shipment").GetProperty("quantity").GetDecimal());
        Assert.Equal(11m, (await Dossier(a, ids.CaseId)).GetProperty("shipmentQuantity").GetDecimal());
        Assert.Equal("Draft", (await Json(await a.GetAsync($"/api/eudr-cases/{ids.CaseId}"))).GetProperty("status").GetString());
        await Status(a.DeleteAsync($"/api/shipments/{ids.ShipmentId}"), HttpStatusCode.NoContent);
        Assert.Equal("Archived", (await Json(await a.GetAsync($"/api/eudr-cases/{ids.CaseId}"))).GetProperty("status").GetString());
    }

    // ---- M. lifecycle -------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Saving_a_lot_does_not_touch_requirements_or_status_and_terminal_cases_are_read_only()
    {
        var (a, _, _, _) = await Tenants(); var ids = await NewCase(a); var s = await NewSource(a);
        var before = await Json(await a.GetAsync($"/api/eudr-cases/{ids.CaseId}"));
        await Save(a, ids.CaseId, Lot(Unique("LOT"), 10, Single(s, 10)));
        var after = await Json(await a.GetAsync($"/api/eudr-cases/{ids.CaseId}"));
        Assert.Equal(before.GetProperty("status").GetString(), after.GetProperty("status").GetString());
        Assert.Equal(before.GetProperty("requirements").GetRawText(), after.GetProperty("requirements").GetRawText());
        Assert.All(after.GetProperty("requirements").EnumerateArray(), r => Assert.Equal("Missing", r.GetProperty("status").GetString()));
        await Problem(await a.PostAsync($"/api/eudr-cases/{ids.CaseId}/complete", null), HttpStatusCode.Conflict);

        foreach (var status in new[] { EudrCaseStatus.Completed, EudrCaseStatus.Archived })
        {
            await using (var scope = factory.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync("""UPDATE "Cases" SET "Status" = {0} WHERE "Id" = {1}""", (int)status, ids.CaseId);
            await Problem(await Put(a, ids.CaseId, Lot(Unique("LOT"), 12, Single(s, 12))), HttpStatusCode.Conflict);
            Assert.Equal(1, LotNumber(await Dossier(a, ids.CaseId))); Assert.Equal(1, (await History(a, ids.CaseId)).GetArrayLength());
            Assert.Equal(HttpStatusCode.OK, (await a.GetAsync($"/api/eudr-cases/{ids.CaseId}/export")).StatusCode);
            Assert.Equal(status.ToString(), (await Json(await a.GetAsync($"/api/eudr-cases/{ids.CaseId}"))).GetProperty("status").GetString());
        }
    }

    // ---- N. concurrency -----------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Parallel_material_saves_get_sequential_numbers_and_leave_one_current_lot()
    {
        var (a, orgA, _, _) = await Tenants(); var ids = await NewCase(a); var reference = Unique("LOT");
        var responses = await Task.WhenAll(Enumerable.Range(1, 4).Select(i => Put(a, ids.CaseId, Lot(reference, i))));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, orgA);
        var lots = await db.CaseLots.Where(x => db.CaseDossiers.Any(d => d.Id == x.CaseDossierId && d.EudrCaseId == ids.CaseId)).OrderBy(x => x.Number).ToListAsync();
        Assert.Equal([1, 2, 3, 4], lots.Select(x => x.Number).ToArray());
        Assert.Single(lots, x => x.SupersededAtUtc == null);
        Assert.Equal(1, await db.CaseDossiers.CountAsync(x => x.EudrCaseId == ids.CaseId));
    }

    [Fact]
    public async Task A_deactivation_in_flight_is_serialized_before_a_new_attribution_is_admitted()
    {
        var (a, orgA, _, _) = await Tenants(); var ids = await NewCase(a); var s = await NewSource(a);
        await using var scope = factory.Services.CreateAsyncScope();
        await using var connection = new NpgsqlConnection(scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetConnectionString());
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        // An uncommitted deactivation holds the plot row: the PUT must wait on its FOR SHARE lock and then see the committed inactive state.
        await using (var update = new NpgsqlCommand("""UPDATE "ProductionPlots" SET "IsActive" = false WHERE "Id" = @id""", connection, tx))
        { update.Parameters.AddWithValue("id", s.Plot.Id); await update.ExecuteNonQueryAsync(); }
        var put = Put(a, ids.CaseId, Lot(Unique("LOT"), 10, Single(s, 10)));
        await Task.Delay(1500);
        Assert.False(put.IsCompleted, "The attribution must wait for the in-flight deactivation.");
        await tx.CommitAsync();
        await Problem(await put, HttpStatusCode.Conflict);
        await using var db = TenantDb(scope, orgA);
        Assert.False(await db.ContributionPlots.AnyAsync(x => x.ProductionPlotId == s.Plot.Id));
        Assert.Equal(HttpStatusCode.NoContent, (await a.GetAsync($"/api/eudr-cases/{ids.CaseId}/dossier")).StatusCode);
    }

    [Fact]
    public async Task A_racing_deactivation_never_leaves_an_attribution_that_was_inactive_when_decided()
    {
        var (a, orgA, _, _) = await Tenants();
        for (var i = 0; i < 3; i++)
        {
            var ids = await NewCase(a); var s = await NewSource(a);
            var results = await Task.WhenAll(Put(a, ids.CaseId, Lot(Unique("LOT"), 10, Single(s, 10))), a.PutAsJsonAsync($"/api/production-plots/{s.Plot.Id}", new { name = s.PlotName, externalReference = (string?)null, notes = (string?)null, isActive = false }));
            Assert.Equal(HttpStatusCode.OK, results[1].StatusCode);
            Assert.Contains(results[0].StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
            await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, orgA);
            Assert.Equal(results[0].StatusCode == HttpStatusCode.OK, await db.ContributionPlots.AnyAsync(x => x.ProductionPlotId == s.Plot.Id));
        }
    }

    // ---- O. export ----------------------------------------------------------------------------------------------------------------------
    private static async Task<(JsonElement Manifest, Dictionary<string, string> Files, string SummaryHeader)> Export(HttpClient c, Guid caseId)
    {
        var response = await c.GetAsync($"/api/eudr-cases/{caseId}/export"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var files = new Dictionary<string, string>();
        foreach (var entry in zip.Entries) { using var reader = new StreamReader(entry.Open()); files[entry.FullName[(entry.FullName.IndexOf('/') + 1)..]] = await reader.ReadToEndAsync(); }
        return (JsonDocument.Parse(files["manifest.json"]).RootElement.Clone(), files, files["summary.csv"].TrimStart('﻿').Split('\n')[0].Trim());
    }

    [Fact]
    public async Task Export_carries_the_current_dossier_and_the_pinned_geometries_only()
    {
        var (a, _, _, _) = await Tenants(); var ids = await NewCase(a); var reference = Unique("LOT");
        var none = await Export(a, ids.CaseId);
        Assert.Equal(JsonValueKind.Null, none.Manifest.GetProperty("dossier").ValueKind);
        Assert.Equal(0, none.Manifest.GetProperty("retainedNotIncluded").GetProperty("supersededLotVersions").GetInt32());
        Assert.DoesNotContain(none.Files.Keys, k => k.StartsWith("plots/"));
        var scopeText = "Current working evidence at export time. This is not a reviewed dossier snapshot; withdrawn or superseded versions are retained in Provena and are not included.";
        Assert.Equal(scopeText, none.Manifest.GetProperty("evidenceScope").GetString());
        Assert.Equal("referenceNumber,supplier,product,cnCode,quantity,unit,status", none.SummaryHeader);

        var s1 = await NewSource(a); var s2 = await NewSource(a, geometry: Bigger);
        await Save(a, ids.CaseId, Lot(reference, 10, Single(s1, 10, "2024-01-01", "2024-02-01")));
        var rev2 = await NewRevision(a, s2.Plot.Id, """{"type":"Polygon","coordinates":[[[0,0],[3,0],[3,3],[0,3],[0,0]]]}""");
        await Save(a, ids.CaseId, Lot(reference, 10, Single(s1, 6, "2024-01-01", "2024-02-01"), Contribution(s2.ProducerId, 4, Link(s2.Plot.Id, rev2, "2024-05-01", "2024-05-31"))));

        var export = await Export(a, ids.CaseId);
        var dossier = export.Manifest.GetProperty("dossier");
        Assert.Equal(2, dossier.GetProperty("currentLot").GetProperty("number").GetInt32());
        Assert.Equal("2024-05-31", dossier.GetProperty("currentLot").GetProperty("contributions").EnumerateArray().SelectMany(x => x.GetProperty("plots").EnumerateArray()).Single(x => x.GetProperty("plotRevisionId").GetGuid() == rev2).GetProperty("productionEndDate").GetString());
        Assert.Equal("Current operator dossier lot at export time; not a reviewed dossier snapshot and not a completeness or compliance determination.", export.Manifest.GetProperty("dossierScope").GetString());
        Assert.Equal(1, export.Manifest.GetProperty("retainedNotIncluded").GetProperty("supersededLotVersions").GetInt32());
        Assert.Equal(scopeText, export.Manifest.GetProperty("evidenceScope").GetString()); Assert.Equal("referenceNumber,supplier,product,cnCode,quantity,unit,status", export.SummaryHeader);
        AssertNoOrganizationId(dossier);
        var plotFiles = export.Files.Keys.Where(k => k.StartsWith("plots/")).Order().ToArray();
        Assert.Equal(new[] { $"plots/{s1.Plot.Revision1:N}.geojson", $"plots/{rev2:N}.geojson" }.Order().ToArray(), plotFiles);
        var stored = await Json(await a.GetAsync($"/api/production-plots/{s2.Plot.Id}/revisions/{rev2}"));
        Assert.Equal(stored.GetProperty("geometry").GetRawText().Replace(" ", ""), JsonDocument.Parse(export.Files[$"plots/{rev2:N}.geojson"]).RootElement.GetRawText().Replace(" ", ""));
        // Pinned revision 1 of plot 2 was never used, and superseded lot version 1 contributes no files of its own.
        Assert.DoesNotContain($"plots/{s2.Plot.Revision1:N}.geojson", export.Files.Keys);
    }

    // ---- P. regression ------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Geolocation_and_evidence_history_are_unaffected_and_the_audit_event_carries_ids_only()
    {
        var (a, _, _, _) = await Tenants(); var ids = await NewCase(a); var s = await NewSource(a);
        using (var upload = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(GeoV1)) { Headers = { ContentType = new MediaTypeHeaderValue("application/geo+json") } }, "file", "plot.geojson" } })
            await Json(await a.PostAsync($"/api/eudr-cases/{ids.CaseId}/geolocation", upload));
        var geo = await Json(await a.GetAsync($"/api/eudr-cases/{ids.CaseId}/geolocation")); var historyBefore = (await Json(await a.GetAsync($"/api/eudr-cases/{ids.CaseId}/evidence-history"))).GetRawText();
        var saved = await Save(a, ids.CaseId, Lot(Unique("LOT"), 10, Single(s, 10, "2024-01-01", "2024-02-01")));
        Assert.Equal(geo.GetRawText(), (await Json(await a.GetAsync($"/api/eudr-cases/{ids.CaseId}/geolocation"))).GetRawText());
        Assert.Equal(historyBefore, (await Json(await a.GetAsync($"/api/eudr-cases/{ids.CaseId}/evidence-history"))).GetRawText());

        var audit = (await Json(await a.GetAsync($"/api/eudr-cases/{ids.CaseId}"))).GetProperty("auditEvents").EnumerateArray().Single(x => x.GetProperty("eventType").GetString() == "CaseLotSaved");
        var description = audit.GetProperty("description").GetString()!;
        Assert.Matches(new Regex(@"^Case lot version [0-9a-f-]{36} \(number 1\) saved\.$"), description);
        Assert.Contains(LotId(saved).ToString(), description);
        Assert.DoesNotContain("coordinates", description, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("2024-", description);
        var second = await Save(a, ids.CaseId, Lot(Unique("LOT"), 9, Single(s, 9)));
        var descriptions = (await Json(await a.GetAsync($"/api/eudr-cases/{ids.CaseId}"))).GetProperty("auditEvents").EnumerateArray().Where(x => x.GetProperty("eventType").GetString() == "CaseLotSaved").Select(x => x.GetProperty("description").GetString()!).ToList();
        Assert.Contains(descriptions, d => d.Contains($"superseded {LotId(saved)}") && d.Contains(LotId(second).ToString()));
    }
}
