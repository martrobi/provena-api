using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Provena.Api.Auth;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
using Provena.Api.Services;
using Xunit;
using static Provena.Api.IntegrationTests.CompletenessReadModelTests;
namespace Provena.Api.IntegrationTests;

// Source Quantity Allocation v1 (completeness policy v3, fingerprint v2, snapshot schema v2) over real HTTP and PostgreSQL: the allocation written inside PUT dossier/lot, the silent-clear
// protection, the derived SourceAllocation, the positive-review gate, retention in reviews and exports, and schema v1 / fingerprint v1 compatibility.
// Each scenario provisions its own organization through AuthService; the registering user is the organization's Owner.
[Collection("Api")]
public sealed class SourceAllocationTests(ApiFactory factory)
{
    private const string Password = "Test-Only-Password-928!";
    private const string Square2 = """{"type":"Polygon","coordinates":[[[0,0],[2,0],[2,2],[0,2],[0,0]]]}""";
    private sealed record Owner(HttpClient Client, Guid Org, Guid UserId);

    private async Task<Owner> NewOwner()
    {
        var name = "Alloc-" + Guid.NewGuid().ToString("N")[..8];
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

    // ---- fixtures -----------------------------------------------------------------------------------------------------------------------
    private sealed record Src(Guid ProducerId, Guid PlotId, Guid RevisionId);
    // A further plot of an existing producer (or of a new producer when none is given).
    private static async Task<Src> NewSource(HttpClient c, Guid? producer = null)
    {
        var p = producer ?? (await Json(await c.PostAsJsonAsync("/api/producers", new { name = "Producer " + Guid.NewGuid().ToString("N")[..8], countryCode = "BR" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var created = await Json(await c.PostAsync("/api/production-plots", Raw($$"""{"producerId":"{{p}}","name":"Plot {{Guid.NewGuid().ToString("N")[..8]}}","initialRevision":{"countryCode":"BR","geometry":{{Square2}},"areaHectares":1.5} }""")), HttpStatusCode.Created);
        return new(p, created.GetProperty("id").GetGuid(), created.GetProperty("currentRevision").GetProperty("id").GetGuid());
    }
    private static StringContent Raw(string json) => new(json, System.Text.Encoding.UTF8, "application/json");
    private static object Link(Guid plot, Guid revision, decimal? allocated, bool omit = false) => omit
        ? new { productionPlotId = plot, plotRevisionId = revision, productionStartDate = "2024-01-01", productionEndDate = "2024-12-31" }
        : new { productionPlotId = plot, plotRevisionId = revision, productionStartDate = "2024-01-01", productionEndDate = "2024-12-31", allocatedQuantity = allocated };
    private static object Link(Src s, decimal? allocated) => Link(s.PlotId, s.RevisionId, allocated);
    private static object Contribution(Guid producer, decimal quantity, params object[] plots) => new { producerId = producer, quantity, unit = "Kg", plots };
    private static async Task<JsonElement> Dossier(HttpClient c, Guid caseId) => await Json(await c.GetAsync($"/api/eudr-cases/{caseId}/dossier"), HttpStatusCode.OK);
    private static string Reference(JsonElement dossier) => dossier.GetProperty("currentLot").GetProperty("lotReference").GetString()!;
    private static Guid LotId(JsonElement dossier) => dossier.GetProperty("currentLot").GetProperty("id").GetGuid();
    private static int LotNumber(JsonElement dossier) => dossier.GetProperty("currentLot").GetProperty("number").GetInt32();
    private static JsonElement Allocation(JsonElement dossier) => dossier.GetProperty("currentLot").GetProperty("sourceAllocation");
    private static string AllocationStatus(JsonElement dossier) => Allocation(dossier).GetProperty("status").GetString()!;
    private static JsonElement PlotsOf(JsonElement dossier, int contribution = 0) => dossier.GetProperty("currentLot").GetProperty("contributions")[contribution].GetProperty("plots");
    private static async Task<int> HistoryCount(HttpClient c, Guid caseId) => (await Json(await c.GetAsync($"/api/eudr-cases/{caseId}/dossier/lot-history"), HttpStatusCode.OK)).GetArrayLength();

    // PUT of the case's lot (reference taken from the loaded dossier, 10 Kg like the shipment) with the given contributions; expected id defaults to the current one.
    private static async Task<HttpResponseMessage> PutLot(World w, object[] contributions, bool? clear = null, Guid? expected = null, bool useExpected = false, decimal quantity = 10m)
    {
        var current = await Dossier(w.C, w.CaseId);
        var node = JsonSerializer.SerializeToNode(new { lotReference = Reference(current), quantity, unit = "Kg", note = (string?)null, contributions }, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        if (clear != null) node["clearSourceAllocations"] = clear;
        node["expectedCurrentLotId"] = useExpected ? expected : LotId(current);
        return await w.C.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/dossier/lot", node);
    }
    private static async Task<JsonElement> Save(World w, params object[] contributions) => await Json(await PutLot(w, contributions), HttpStatusCode.OK);
    private static async Task<JsonElement> Rejected(Task<HttpResponseMessage> request, HttpStatusCode status)
    {
        var response = await request; var json = await Json(response, status);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return json;
    }
    private static string? ReasonOf(JsonElement problem) => problem.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("reason", out var r) ? r.GetString() : null;
    private static string ItemStatus(JsonElement completeness, string code) => completeness.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("code").GetString() == code).GetProperty("status").GetString()!;
    private static Task<HttpResponseMessage> Review(HttpClient c, Guid caseId, string decision, JsonElement state) => c.PostAsJsonAsync($"/api/eudr-cases/{caseId}/reviews", new
    {
        decision, rationale = "Reviewed against the recorded facts.", expectedFingerprint = Fingerprint(state),
        expectedLatestReviewId = state.GetProperty("latestReview").ValueKind == JsonValueKind.Null ? (Guid?)null : state.GetProperty("latestReview").GetProperty("reviewId").GetGuid(),
    });
    private static async Task<Dictionary<string, string>> ReadZip(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var files = new Dictionary<string, string>();
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".json"))) files[entry.FullName.Split('/').Last()] = await new StreamReader(entry.Open()).ReadToEndAsync();
        return files;
    }
    private static async Task<JsonElement> ReviewJson(World w, Guid reviewId) { using var doc = JsonDocument.Parse((await ReadZip(await w.C.GetAsync($"/api/eudr-cases/{w.CaseId}/reviews/{reviewId}/export")))["review.json"]); return doc.RootElement.Clone(); }

    // ---- storage, versioning and the derived read model -------------------------------------------------------------------------------------
    [Fact]
    public async Task The_first_allocation_is_a_new_immutable_lot_version_and_the_legacy_version_stays_not_recorded()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org);
        var legacy = await Dossier(o.Client, w.CaseId);
        Assert.Equal("NotRecorded", AllocationStatus(legacy)); Assert.Equal(JsonValueKind.Null, PlotsOf(legacy)[0].GetProperty("allocatedQuantity").ValueKind);
        Assert.Equal(0m, Allocation(legacy).GetProperty("allocatedQuantity").GetDecimal()); Assert.Equal(10m, Allocation(legacy).GetProperty("unallocatedQuantity").GetDecimal());

        var v2 = await Save(w, Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, 10m)));
        Assert.NotEqual(LotId(legacy), LotId(v2)); Assert.Equal(LotNumber(legacy) + 1, LotNumber(v2));
        Assert.Equal("Matched", AllocationStatus(v2)); Assert.Equal(10m, PlotsOf(v2)[0].GetProperty("allocatedQuantity").GetDecimal());
        Assert.Equal(["status", "lotQuantity", "unit", "allocatedQuantity", "unallocatedQuantity", "contributions"], Allocation(v2).EnumerateObject().Select(p => p.Name).ToArray());
        var contribution = Allocation(v2).GetProperty("contributions")[0];
        Assert.Equal(["producerId", "contributionQuantity", "allocatedQuantity", "unallocatedQuantity", "status"], contribution.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(w.ProducerId, contribution.GetProperty("producerId").GetGuid()); Assert.Equal("Matched", contribution.GetProperty("status").GetString());
        Assert.DoesNotContain("allocatedQuantity", Allocation(v2).GetProperty("contributions")[0].GetProperty("producerId").GetRawText());
        AssertNoOrganizationId(v2);

        var history = await Json(await o.Client.GetAsync($"/api/eudr-cases/{w.CaseId}/dossier/lot-history"), HttpStatusCode.OK);
        Assert.Equal(2, history.GetArrayLength());
        var retained = history.EnumerateArray().Single(x => x.GetProperty("number").GetInt32() == LotNumber(legacy));
        Assert.Equal("NotRecorded", retained.GetProperty("sourceAllocation").GetProperty("status").GetString()); // the old version is not rewritten
        Assert.Equal(JsonValueKind.Null, retained.GetProperty("contributions")[0].GetProperty("plots")[0].GetProperty("allocatedQuantity").ValueKind);
        Assert.Equal("Matched", history.EnumerateArray().Single(x => x.GetProperty("number").GetInt32() == LotNumber(v2)).GetProperty("sourceAllocation").GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_allocation_only_change_creates_a_version_and_an_identical_resubmission_is_a_no_op_at_any_scale()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org);
        var v2 = await Save(w, Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, 10m)));
        var same = await Save(w, Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, 10.000000m)));
        Assert.Equal(LotId(v2), LotId(same)); Assert.Equal(2, await HistoryCount(o.Client, w.CaseId)); // 10 == 10.000000: nothing written

        var v3 = await Save(w, Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, 9.5m))); // only the allocation differs
        Assert.Equal(LotNumber(v2) + 1, LotNumber(v3)); Assert.Equal("UnderAllocated", AllocationStatus(v3));
        Assert.Equal(0.5m, Allocation(v3).GetProperty("unallocatedQuantity").GetDecimal()); Assert.Equal(3, await HistoryCount(o.Client, w.CaseId));
        Assert.Equal(10m, (await Query(o.Org, db => db.ContributionPlots.Where(x => db.LotContributions.Any(c => c.Id == x.LotContributionId && c.CaseLotId == LotId(v2))).Select(x => x.AllocatedQuantity).SingleAsync())));
    }

    [Fact]
    public async Task One_producer_with_several_plots_and_several_producers_reconcile_per_contribution_and_for_the_lot()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org);
        var second = await NewSource(o.Client, w.ProducerId); var other = await NewSource(o.Client);

        var oneProducer = await Save(w, Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, 6m), Link(second, 4m)));
        Assert.Equal("Matched", AllocationStatus(oneProducer)); Assert.Equal(2, PlotsOf(oneProducer).GetArrayLength());

        var matched = await Save(w, Contribution(w.ProducerId, 6m, Link(w.PlotId, w.RevisionId, 2m), Link(second, 4m)), Contribution(other.ProducerId, 4m, Link(other, 4m)));
        Assert.Equal("Matched", AllocationStatus(matched)); Assert.Equal(10m, Allocation(matched).GetProperty("allocatedQuantity").GetDecimal());

        var under = await Save(w, Contribution(w.ProducerId, 6m, Link(w.PlotId, w.RevisionId, 2m), Link(second, 4m)), Contribution(other.ProducerId, 4m, Link(other, 3m)));
        Assert.Equal("UnderAllocated", AllocationStatus(under)); Assert.Equal(1m, Allocation(under).GetProperty("unallocatedQuantity").GetDecimal());
        var statuses = Allocation(under).GetProperty("contributions").EnumerateArray().ToDictionary(c => c.GetProperty("producerId").GetGuid(), c => c.GetProperty("status").GetString());
        Assert.Equal("Matched", statuses[w.ProducerId]); Assert.Equal("UnderAllocated", statuses[other.ProducerId]);

        // Contributions below the lot while each contribution is fully allocated: the lot is still under-allocated (and LOT_RECONCILED reports the contribution gap separately).
        var belowLot = await Save(w, Contribution(w.ProducerId, 5m, Link(w.PlotId, w.RevisionId, 5m)), Contribution(other.ProducerId, 3m, Link(other, 3m)));
        Assert.Equal("UnderAllocated", AllocationStatus(belowLot)); Assert.Equal(2m, Allocation(belowLot).GetProperty("unallocatedQuantity").GetDecimal());
        Assert.All(Allocation(belowLot).GetProperty("contributions").EnumerateArray(), c => Assert.Equal("Matched", c.GetProperty("status").GetString()));

        // A contribution without plots next to an allocated one is MissingData (storable); with nothing allocated anywhere it stays NotRecorded.
        var missing = await Save(w, Contribution(w.ProducerId, 6m, Link(w.PlotId, w.RevisionId, 6m)), Contribution(other.ProducerId, 4m));
        Assert.Equal("MissingData", AllocationStatus(missing));
        var none = await Json(await PutLot(w, [Contribution(w.ProducerId, 6m, Link(w.PlotId, w.RevisionId, null)), Contribution(other.ProducerId, 4m)], clear: true), HttpStatusCode.OK); // recorded values are only dropped explicitly
        Assert.Equal("NotRecorded", AllocationStatus(none));
    }

    // ---- write validation -------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Invalid_allocations_are_rejected_without_writing_a_version()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org); var second = await NewSource(o.Client, w.ProducerId);
        var before = LotId(await Dossier(o.Client, w.CaseId));
        foreach (var bad in new decimal[] { 0m, -1m, 1.0000001m, 100000000000000m })
        {
            var problem = await Rejected(PutLot(w, [Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, bad))]), HttpStatusCode.BadRequest);
            Assert.Contains("allocatedQuantity", problem.GetProperty("errors").GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        var partial = await Rejected(PutLot(w, [Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, 6m), Link(second, null))]), HttpStatusCode.BadRequest);
        Assert.Contains("Every plot link must carry an allocated quantity", partial.GetRawText());
        var over = await Rejected(PutLot(w, [Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, 6m), Link(second, 5m))]), HttpStatusCode.BadRequest);
        Assert.Equal("PlotAllocationExceedsContribution", ReasonOf(over)); Assert.Equal(w.ProducerId, over.GetProperty("errors").GetProperty("producerId").GetGuid());
        Assert.Equal(before, LotId(await Dossier(o.Client, w.CaseId))); Assert.Equal(1, await HistoryCount(o.Client, w.CaseId));
    }

    // ---- silent clear protection ------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Recorded_allocations_are_never_erased_by_omission_and_only_an_explicit_clear_creates_a_not_recorded_version()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org);
        var recorded = await Save(w, Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, 10m)));
        var versions = await HistoryCount(o.Client, w.CaseId);

        // An old client that omits the field, or sends null, must not clear: 400 with the typed reason, nothing written.
        var omitted = await Rejected(PutLot(w, [Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, null, omit: true))]), HttpStatusCode.BadRequest);
        Assert.Equal("SourceAllocationsNotSupplied", ReasonOf(omitted));
        Assert.Equal("SourceAllocationsNotSupplied", ReasonOf(await Rejected(PutLot(w, [Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, null))], clear: false), HttpStatusCode.BadRequest)));
        // An otherwise different lot sent without allocations is refused the same way (no silent preservation either).
        Assert.Equal("SourceAllocationsNotSupplied", ReasonOf(await Rejected(PutLot(w, [Contribution(w.ProducerId, 9m, Link(w.PlotId, w.RevisionId, null))], quantity: 9m), HttpStatusCode.BadRequest)));
        Assert.Equal(LotId(recorded), LotId(await Dossier(o.Client, w.CaseId))); Assert.Equal(versions, await HistoryCount(o.Client, w.CaseId));

        // A clear that also carries values is invalid.
        var contradictory = await Rejected(PutLot(w, [Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, 10m))], clear: true), HttpStatusCode.BadRequest);
        Assert.Contains("clearSourceAllocations", contradictory.GetRawText());

        // The explicit clear is a normal new immutable version with the allocation NotRecorded; the recorded version stays in the history.
        var cleared = await Json(await PutLot(w, [Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, null))], clear: true), HttpStatusCode.OK);
        Assert.Equal("NotRecorded", AllocationStatus(cleared)); Assert.Equal(LotNumber(recorded) + 1, LotNumber(cleared)); Assert.Equal(versions + 1, await HistoryCount(o.Client, w.CaseId));
        var history = await Json(await o.Client.GetAsync($"/api/eudr-cases/{w.CaseId}/dossier/lot-history"), HttpStatusCode.OK);
        Assert.Equal(10m, history.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == LotId(recorded)).GetProperty("contributions")[0].GetProperty("plots")[0].GetProperty("allocatedQuantity").GetDecimal());

        // With nothing recorded the clear flag is meaningless (400), while omitting the field is a plain idempotent save.
        Assert.Equal("ClearSourceAllocationsNotApplicable", ReasonOf(await Rejected(PutLot(w, [Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, null))], clear: true), HttpStatusCode.BadRequest)));
        var same = await Json(await PutLot(w, [Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, null, omit: true))]), HttpStatusCode.OK);
        Assert.Equal(LotId(cleared), LotId(same));
    }

    [Fact]
    public async Task A_clear_flag_is_refused_when_no_lot_exists_yet()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org, new Opt(Lot: false));
        var node = JsonSerializer.SerializeToNode(new { lotReference = "LOT-" + Guid.NewGuid().ToString("N")[..8], quantity = 10m, unit = "Kg", note = (string?)null, contributions = Array.Empty<object>(), expectedCurrentLotId = (Guid?)null, clearSourceAllocations = true }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var problem = await Rejected(o.Client.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/dossier/lot", node), HttpStatusCode.BadRequest);
        Assert.Equal("ClearSourceAllocationsNotApplicable", ReasonOf(problem));
        Assert.Equal(HttpStatusCode.NoContent, (await o.Client.GetAsync($"/api/eudr-cases/{w.CaseId}/dossier")).StatusCode);
    }

    // ---- concurrency ------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_stale_expected_lot_id_conflicts_before_the_allocation_rules_and_concurrent_writers_serialize()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org);
        var v2 = await Save(w, Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, 10m)));
        var staleId = LotId(v2);
        var v3 = await Save(w, Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, 9m)));

        // The stale id wins over the silent-clear rule: it is the only optimistic-concurrency conflict.
        var stale = await Rejected(PutLot(w, [Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, null, omit: true))], expected: staleId, useExpected: true), HttpStatusCode.Conflict);
        Assert.Equal("LotHistoryChanged", ReasonOf(stale)); Assert.Equal(LotId(v3), stale.GetProperty("errors").GetProperty("currentLotId").GetGuid());
        Assert.Equal("LotHistoryChanged", ReasonOf(await Rejected(PutLot(w, [Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, 9m))], expected: staleId, useExpected: true), HttpStatusCode.Conflict))); // identical content, stale id
        Assert.Equal(LotId(v3), LotId(await Dossier(o.Client, w.CaseId)));

        var current = LotId(v3);
        var responses = await Task.WhenAll(Enumerable.Range(1, 5).Select(i => PutLot(w, [Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, 1m * i))], expected: current, useExpected: true)));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        foreach (var loser in responses.Where(r => r.StatusCode != HttpStatusCode.OK)) Assert.Equal("LotHistoryChanged", ReasonOf(await Json(loser, HttpStatusCode.Conflict)));
        Assert.Equal(1, (await Query(o.Org, db => db.CaseLots.CountAsync(x => x.SupersededAtUtc == null && db.CaseDossiers.Any(d => d.Id == x.CaseDossierId && d.EudrCaseId == w.CaseId)))));
        Assert.Equal(LotNumber(v3) + 1, LotNumber(await Dossier(o.Client, w.CaseId)));
    }

    // ---- policy v3 and the review gate ------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Policy_v3_reports_the_allocation_item_and_a_recorded_allocation_must_reconcile_before_acceptance()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org); var second = await NewSource(o.Client, w.ProducerId);
        var notRecorded = await Completeness(o.Client, w.CaseId);
        Assert.Equal("3", notRecorded.GetProperty("policy").GetProperty("version").GetString()); Assert.Equal("NotApplicable", ItemStatus(notRecorded, "SOURCE_ALLOCATION_RECONCILED"));
        var item = notRecorded.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("code").GetString() == "SOURCE_ALLOCATION_RECONCILED");
        Assert.Equal("Quantity", item.GetProperty("group").GetString()); Assert.Equal("CaseLot", item.GetProperty("subjectType").GetString());

        await Save(w, Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, 6m), Link(second, 3m))); // under-allocated draft
        var under = await Completeness(o.Client, w.CaseId);
        Assert.Equal("Invalid", ItemStatus(under, "SOURCE_ALLOCATION_RECONCILED")); Assert.Equal("Incomplete", under.GetProperty("dataCoverage").GetString());
        Assert.Equal("Matched", under.GetProperty("quantityReconciliation").GetProperty("outcome").GetString()); // Quantity Reconciliation is a different question and unchanged
        var blocked = await Rejected(Review(o.Client, w.CaseId, "AcceptedAsCompleteUnderPolicy", under), HttpStatusCode.Conflict);
        Assert.Contains("SOURCE_ALLOCATION_RECONCILED", blocked.GetProperty("errors").GetProperty("failingItems").EnumerateArray().Select(i => i.GetProperty("code").GetString()));
        Assert.Equal(HttpStatusCode.Created, (await Review(o.Client, w.CaseId, "ReturnedForCorrection", under)).StatusCode); // no extra semantics for the other decisions

        await Save(w, Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, 6m), Link(second, 4m)));
        var matched = await Completeness(o.Client, w.CaseId);
        Assert.Equal("Satisfied", ItemStatus(matched, "SOURCE_ALLOCATION_RECONCILED")); Assert.Equal("Stale", matched.GetProperty("reviewFreshness").GetString());
        Assert.Equal(HttpStatusCode.Created, (await Review(o.Client, w.CaseId, "AcceptedAsCompleteUnderPolicy", matched)).StatusCode);

        // Not recorded never blocks acceptance (allocation is optional in v1).
        var o2 = await NewOwner(); var w2 = await Build(o2.Client, o2.Org);
        Assert.Equal(HttpStatusCode.Created, (await Review(o2.Client, w2.CaseId, "AcceptedAsCompleteUnderPolicy", await Completeness(o2.Client, w2.CaseId))).StatusCode);
    }

    [Fact]
    public async Task A_contribution_without_plots_next_to_recorded_allocations_is_missing_data_and_blocks_acceptance()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org); var other = await NewSource(o.Client);
        await Save(w, Contribution(w.ProducerId, 6m, Link(w.PlotId, w.RevisionId, 6m)), Contribution(other.ProducerId, 4m));
        var state = await Completeness(o.Client, w.CaseId);
        Assert.Equal("Missing", ItemStatus(state, "SOURCE_ALLOCATION_RECONCILED"));
        Assert.Equal(HttpStatusCode.Conflict, (await Review(o.Client, w.CaseId, "AcceptedAsCompleteUnderPolicy", state)).StatusCode);
    }

    // ---- fingerprint v2, snapshot v2, reviewed export ---------------------------------------------------------------------------------------
    [Fact]
    public async Task A_new_review_uses_fingerprint_and_snapshot_v2_and_the_reviewed_export_stays_frozen_after_later_allocation_changes()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org);
        await Save(w, Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, 10m)));
        var created = await Json(await Review(o.Client, w.CaseId, "AcceptedAsCompleteUnderPolicy", await Completeness(o.Client, w.CaseId)), HttpStatusCode.Created);
        var reviewId = created.GetProperty("reviewId").GetGuid();
        Assert.Equal(2, created.GetProperty("fingerprintVersion").GetInt32()); Assert.Equal(2, created.GetProperty("snapshotSchemaVersion").GetInt32());
        var detail = await Json(await o.Client.GetAsync($"/api/eudr-cases/{w.CaseId}/reviews/{reviewId}"), HttpStatusCode.OK);
        var snapshotPlot = detail.GetProperty("snapshot").GetProperty("facts").GetProperty("lot").GetProperty("contributions")[0].GetProperty("plots")[0];
        Assert.Equal(10m, snapshotPlot.GetProperty("allocatedQuantity").GetDecimal()); Assert.Equal(2, detail.GetProperty("snapshot").GetProperty("schemaVersion").GetInt32());
        Assert.Equal(["schemaVersion", "facts", "context", "completeness"], detail.GetProperty("snapshot").EnumerateObject().Select(p => p.Name).ToArray()); // no "$type" member
        Assert.Equal(["dossierId", "id", "number", "lotReference", "quantity", "unit", "note", "reconciliation", "contributions"], detail.GetProperty("snapshot").GetProperty("facts").GetProperty("lot").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(["plotId", "revisionId", "revisionNumber", "countryCode", "geometryType", "areaHectares", "geometrySha256", "productionStartDate", "productionEndDate", "allocatedQuantity"], snapshotPlot.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("Current", (await Completeness(o.Client, w.CaseId)).GetProperty("reviewFreshness").GetString());

        var exported = await ReviewJson(w, reviewId);
        Assert.Equal("Matched", exported.GetProperty("sourceAllocation").GetProperty("status").GetString()); Assert.Equal(10m, exported.GetProperty("sourceAllocation").GetProperty("allocatedQuantity").GetDecimal());
        Assert.Contains("not a legal net-mass", exported.GetProperty("sourceAllocationScope").GetString());
        Assert.Equal(10m, exported.GetProperty("snapshot").GetProperty("facts").GetProperty("lot").GetProperty("contributions")[0].GetProperty("plots")[0].GetProperty("allocatedQuantity").GetDecimal());

        // A later allocation change stales the review (fingerprint v2 + a new lot version) and neither the stored review nor its export moves.
        await Save(w, Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, 4m)));
        var stale = await Completeness(o.Client, w.CaseId);
        Assert.Equal("Stale", stale.GetProperty("reviewFreshness").GetString()); Assert.Equal(["Lot"], stale.GetProperty("latestReview").GetProperty("changedAreas").EnumerateArray().Select(x => x.GetString()!).ToArray());
        var after = await ReviewJson(w, reviewId);
        Assert.Equal("Matched", after.GetProperty("sourceAllocation").GetProperty("status").GetString());
        Assert.Equal(JsonSerializer.Serialize(exported.GetProperty("sourceAllocation")), JsonSerializer.Serialize(after.GetProperty("sourceAllocation")));
        Assert.Equal(10m, after.GetProperty("snapshot").GetProperty("facts").GetProperty("lot").GetProperty("contributions")[0].GetProperty("plots")[0].GetProperty("allocatedQuantity").GetDecimal());

        // Working export: current allocation under the dossier only, no top-level duplicate, CSV unchanged.
        var files = await ReadZip(await o.Client.GetAsync($"/api/eudr-cases/{w.CaseId}/export"));
        using var manifest = JsonDocument.Parse(files["manifest.json"]);
        var currentLot = manifest.RootElement.GetProperty("dossier").GetProperty("currentLot");
        Assert.Equal("UnderAllocated", currentLot.GetProperty("sourceAllocation").GetProperty("status").GetString()); Assert.Equal(4m, currentLot.GetProperty("contributions")[0].GetProperty("plots")[0].GetProperty("allocatedQuantity").GetDecimal());
        Assert.False(manifest.RootElement.TryGetProperty("sourceAllocation", out _)); Assert.False(manifest.RootElement.TryGetProperty("sourceAllocationScope", out _));
    }

    [Fact]
    public async Task A_schema_v1_review_with_fingerprint_v1_stays_readable_current_and_goes_stale_through_the_new_lot_version()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org);
        var created = await Json(await Review(o.Client, w.CaseId, "AcceptedAsCompleteUnderPolicy", await Completeness(o.Client, w.CaseId)), HttpStatusCode.Created);
        var reviewId = created.GetProperty("reviewId").GetGuid();

        // Rewrite the stored revision into what a pre-allocation review looked like: schema v1 JSON without any allocation member and the fingerprint-v1 value (raw SQL bypasses the app-level guard).
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await using var db = TenantDb(scope, o.Org);
            var revision = await db.CaseRevisions.AsNoTracking().SingleAsync(x => x.EudrCaseId == w.CaseId);
            var json = JsonNode.Parse(revision.SnapshotJson)!.AsObject(); json["schemaVersion"] = 1;
            foreach (var plot in json["facts"]!["lot"]!["contributions"]!.AsArray().SelectMany(c => c!["plots"]!.AsArray())) plot!.AsObject().Remove("allocatedQuantity");
            var facts = await new DossierFactsBuilder(db).Build(await db.Cases.SingleAsync(x => x.Id == w.CaseId), CancellationToken.None);
            var v1 = DossierFingerprint.Compute(facts, 1);
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "CaseRevisions" SET "SnapshotSchemaVersion" = 1, "SnapshotJson" = {json.ToJsonString()}::jsonb, "FingerprintVersion" = 1, "Fingerprint" = {v1.Fingerprint}, "SectionHashes" = {ReviewSnapshot.SerializeHashes(v1.SectionHashes)}::jsonb WHERE "Id" = {revision.Id}""");
        }
        var legacy = await Json(await o.Client.GetAsync($"/api/eudr-cases/{w.CaseId}/reviews/{reviewId}"), HttpStatusCode.OK);
        Assert.Equal(1, legacy.GetProperty("snapshotSchemaVersion").GetInt32()); Assert.Equal(1, legacy.GetProperty("fingerprintVersion").GetInt32());
        Assert.False(legacy.GetProperty("snapshot").GetProperty("facts").GetProperty("lot").GetProperty("contributions")[0].GetProperty("plots")[0].TryGetProperty("allocatedQuantity", out _)); // the stored v1 shape, unchanged
        Assert.Equal(1, legacy.GetProperty("snapshot").GetProperty("schemaVersion").GetInt32());
        Assert.Equal(["schemaVersion", "facts", "context", "completeness"], legacy.GetProperty("snapshot").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(["plotId", "revisionId", "revisionNumber", "countryCode", "geometryType", "areaHectares", "geometrySha256", "productionStartDate", "productionEndDate"], legacy.GetProperty("snapshot").GetProperty("facts").GetProperty("lot").GetProperty("contributions")[0].GetProperty("plots")[0].EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("Current", (await Completeness(o.Client, w.CaseId)).GetProperty("reviewFreshness").GetString());
        var export = await ReviewJson(w, reviewId);
        Assert.Equal("NotRecorded", export.GetProperty("sourceAllocation").GetProperty("status").GetString()); Assert.Equal(1, export.GetProperty("snapshotSchemaVersion").GetInt32());
        Assert.Equal(["BR"], export.GetProperty("productionCoverage").GetProperty("countries").EnumerateArray().Select(x => x.GetString()!).ToArray());

        // Fingerprint v1 ignores the allocation value, but the allocation write is a new lot version, so the v1 review still goes Stale on the Lot section.
        await Save(w, Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, 10m)));
        var stale = await Completeness(o.Client, w.CaseId);
        Assert.Equal("Stale", stale.GetProperty("reviewFreshness").GetString()); Assert.Equal(["Lot"], stale.GetProperty("latestReview").GetProperty("changedAreas").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal(2, stale.GetProperty("fingerprintVersion").GetInt32()); // a new review would use version 2
    }

    [Fact]
    public async Task The_openapi_document_describes_the_versioned_snapshot_as_a_union_of_both_shapes_without_a_misleading_discriminator()
    {
        using var anonymous = factory.CreateClient();
        var schemas = (await Json(await anonymous.GetAsync("/openapi/v1.json"), HttpStatusCode.OK)).GetProperty("components").GetProperty("schemas");
        Assert.Equal("#/components/schemas/IReviewSnapshot", schemas.GetProperty("ReviewDetailDto").GetProperty("properties").GetProperty("snapshot").GetProperty("$ref").GetString());
        var union = schemas.GetProperty("IReviewSnapshot");
        var shapes = union.GetProperty("anyOf").EnumerateArray().Select(x => schemas.GetProperty(x.GetProperty("$ref").GetString()!.Split('/').Last())).ToList();
        Assert.Equal(2, shapes.Count);
        Assert.False(union.TryGetProperty("discriminator", out _)); // the wire has no $type member
        Assert.Contains("schemaVersion", union.GetProperty("description").GetString());
        var lotRefs = shapes.Select(s => s.GetProperty("properties").GetProperty("facts").GetProperty("$ref").GetString()!.Split('/').Last()).ToArray();
        Assert.Equal(["SnapshotFactsV1", "SnapshotFactsV2"], lotRefs);
        Assert.All(shapes, s => Assert.Equal(["schemaVersion", "facts", "context", "completeness"], s.GetProperty("required").EnumerateArray().Select(x => x.GetString()!).ToArray()));
        Assert.False(schemas.GetProperty("SnapshotPlotLinkV1").GetProperty("properties").TryGetProperty("allocatedQuantity", out _));
        Assert.True(schemas.GetProperty("SnapshotPlotLinkV2").GetProperty("properties").TryGetProperty("allocatedQuantity", out _));
        Assert.DoesNotContain("organizationId", union.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    // ---- tenant isolation and persistence guards ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task Another_organization_cannot_write_read_or_attribute_allocations_of_a_foreign_case_or_plot()
    {
        var a = await NewOwner(); var wa = await Build(a.Client, a.Org); await Save(wa, Contribution(wa.ProducerId, 10m, Link(wa.PlotId, wa.RevisionId, 10m)));
        var b = await NewOwner(); var wb = await Build(b.Client, b.Org);
        var foreign = JsonSerializer.SerializeToNode(new { lotReference = "LOT-X", quantity = 10m, unit = "Kg", note = (string?)null, contributions = new[] { Contribution(wa.ProducerId, 10m, Link(wa.PlotId, wa.RevisionId, 10m)) }, expectedCurrentLotId = (Guid?)null }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PutAsJsonAsync($"/api/eudr-cases/{wa.CaseId}/dossier/lot", foreign)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync($"/api/eudr-cases/{wa.CaseId}/dossier")).StatusCode);
        var ownCase = await Rejected(PutLot(wb, [Contribution(wa.ProducerId, 10m, Link(wa.PlotId, wa.RevisionId, 10m))]), HttpStatusCode.BadRequest); // A's producer and plot do not exist for B
        Assert.Contains("not found in your organization", ownCase.GetProperty("detail").GetString());
        Assert.Equal("NotRecorded", AllocationStatus(await Dossier(b.Client, wb.CaseId))); Assert.Equal("Matched", AllocationStatus(await Dossier(a.Client, wa.CaseId)));
    }

    [Fact]
    public async Task The_persistence_guard_keeps_allocated_plot_links_immutable()
    {
        var o = await NewOwner(); var w = await Build(o.Client, o.Org);
        await Save(w, Contribution(w.ProducerId, 10m, Link(w.PlotId, w.RevisionId, 10m)));
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, o.Org);
        var link = await db.ContributionPlots.SingleAsync(x => x.AllocatedQuantity != null);
        link.AllocatedQuantity = 1m;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.ContributionPlots.Remove(await db.ContributionPlots.SingleAsync(x => x.AllocatedQuantity != null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(10m, (await db.ContributionPlots.SingleAsync(x => x.AllocatedQuantity != null)).AllocatedQuantity);
    }
}
