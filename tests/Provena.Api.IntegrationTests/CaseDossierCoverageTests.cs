using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Provena.Api.Data;
using Provena.Api.Domain;
using Xunit;
namespace Provena.Api.IntegrationTests;

// Production Coverage & Lot Concurrency v1: the derived coverage on the dossier read model and the working export, the submission-country advisory (evidence inconsistency
// information only) and the expectedCurrentLotId optimistic-concurrency contract of PUT dossier/lot. No schema change: the derivation reads rows that already exist.
public sealed partial class CaseDossierTests
{
    private const string ReasonChanged = "LotHistoryChanged";

    private static async Task<Plot> NewPlotIn(HttpClient c, Guid producerId, string country)
    {
        var body = $$"""{"producerId":"{{producerId}}","name":"{{Unique("Plot")}}","externalReference":null,"notes":null,"initialRevision":{"countryCode":"{{country}}","geometry":{{Square}},"areaHectares":1.5,"note":null} }""";
        var json = await Json(await c.PostAsync("/api/production-plots", Raw(body)), HttpStatusCode.Created);
        return new(json.GetProperty("id").GetGuid(), json.GetProperty("currentRevision").GetProperty("id").GetGuid());
    }
    private static async Task<Guid> NewRevisionIn(HttpClient c, Guid plotId, string country, string geometry)
    {
        var body = $$"""{"countryCode":"{{country}}","geometry":{{geometry}},"areaHectares":null,"note":null}""";
        return (await Json(await c.PostAsync($"/api/production-plots/{plotId}/revisions", Raw(body)), HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }
    private static async Task<JsonElement> Reason(HttpResponseMessage response) => (await Problem(response, HttpStatusCode.Conflict)).GetProperty("errors");
    private static async Task<JsonElement> CompletenessOf(HttpClient c, Guid caseId) => await Json(await c.GetAsync($"/api/eudr-cases/{caseId}/completeness"), HttpStatusCode.OK);
    private static List<string> Countries(JsonElement coverage) => coverage.GetProperty("countries").EnumerateArray().Select(x => x.GetString()!).ToList();

    private async Task AddSubmission(Guid org, Guid caseId, string? country)
    {
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org);
        db.Submissions.Add(new SupplierSubmission { OrganizationId = org, EudrCaseId = caseId, CountryOfProductionCode = country, SubmittedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }
    private async Task<int> LotAuditEvents(Guid org, Guid caseId)
    {
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org);
        return await db.AuditEvents.CountAsync(x => x.EudrCaseId == caseId && x.EventType == "CaseLotSaved");
    }

    // ---- production coverage ------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Coverage_is_derived_from_multiple_countries_producers_plots_and_missing_periods()
    {
        var (a, _, _, _) = await Tenants(); var ids = await NewCase(a);
        var p1 = await NewProducer(a); var p2 = await NewProducer(a);
        var br = await NewPlotIn(a, p1, "BR"); var co = await NewPlotIn(a, p1, "CO"); var br2 = await NewPlotIn(a, p2, "BR");
        await Save(a, ids.CaseId, Lot(Unique("LOT"), 10,
            Contribution(p1, 6, Link(br.Id, br.Revision1, "2024-03-01", "2024-04-30"), Link(co.Id, co.Revision1, "2024-01-15", "2024-02-28")),
            Contribution(p2, 4, Link(br2.Id, br2.Revision1))));
        var coverage = (await Dossier(a, ids.CaseId)).GetProperty("productionCoverage");
        Assert.Equal(["BR", "CO"], Countries(coverage));
        Assert.Equal("2024-01-15", coverage.GetProperty("periodFrom").GetString()); Assert.Equal("2024-04-30", coverage.GetProperty("periodTo").GetString());
        Assert.Equal(1, coverage.GetProperty("sourcesWithoutPeriod").GetInt32()); Assert.Equal(2, coverage.GetProperty("producerCount").GetInt32()); Assert.Equal(3, coverage.GetProperty("plotCount").GetInt32());
        Assert.Equal(["countries", "periodFrom", "periodTo", "sourcesWithoutPeriod", "producerCount", "plotCount"], coverage.EnumerateObject().Select(p => p.Name).ToArray());
        AssertNoOrganizationId(coverage);
    }

    [Fact]
    public async Task Coverage_of_a_single_date_source_has_equal_endpoints_and_no_period_means_null_bounds()
    {
        var (a, _, _, _) = await Tenants(); var ids = await NewCase(a); var s = await NewSource(a);
        await Save(a, ids.CaseId, Lot(Unique("LOT"), 10, Single(s, 10, "2024-05-05", "2024-05-05")));
        var single = (await Dossier(a, ids.CaseId)).GetProperty("productionCoverage");
        Assert.Equal("2024-05-05", single.GetProperty("periodFrom").GetString()); Assert.Equal("2024-05-05", single.GetProperty("periodTo").GetString()); Assert.Equal(0, single.GetProperty("sourcesWithoutPeriod").GetInt32());

        var ids2 = await NewCase(a); var s2 = await NewSource(a);
        await Save(a, ids2.CaseId, Lot(Unique("LOT"), 10, Single(s2, 10)));
        var open = (await Dossier(a, ids2.CaseId)).GetProperty("productionCoverage");
        Assert.Equal(JsonValueKind.Null, open.GetProperty("periodFrom").ValueKind); Assert.Equal(JsonValueKind.Null, open.GetProperty("periodTo").ValueKind);
        Assert.Equal(1, open.GetProperty("sourcesWithoutPeriod").GetInt32()); Assert.Equal(1, open.GetProperty("producerCount").GetInt32()); Assert.Equal(1, open.GetProperty("plotCount").GetInt32());
    }

    [Fact]
    public async Task A_later_plot_revision_does_not_rewrite_the_pinned_coverage_until_a_new_lot_version_adopts_it()
    {
        var (a, _, _, _) = await Tenants(); var ids = await NewCase(a); var producer = await NewProducer(a); var plot = await NewPlotIn(a, producer, "BR"); var reference = Unique("LOT");
        await Save(a, ids.CaseId, Lot(reference, 10, Contribution(producer, 10, Link(plot.Id, plot.Revision1, "2024-01-01", "2024-02-01"))));
        var rev2 = await NewRevisionIn(a, plot.Id, "CO", Bigger);
        Assert.Equal(["BR"], Countries((await Dossier(a, ids.CaseId)).GetProperty("productionCoverage"))); // still the pinned revision 1
        await Save(a, ids.CaseId, Lot(reference, 10, Contribution(producer, 10, Link(plot.Id, rev2, "2024-01-01", "2024-02-01"))));
        Assert.Equal(["CO"], Countries((await Dossier(a, ids.CaseId)).GetProperty("productionCoverage")));
    }

    [Fact]
    public async Task The_working_export_carries_the_coverage_only_under_the_dossier_and_has_none_without_a_dossier()
    {
        var (a, _, _, _) = await Tenants(); var empty = await NewCase(a);
        var none = await Export(a, empty.CaseId);
        Assert.Equal(JsonValueKind.Null, none.Manifest.GetProperty("dossier").ValueKind);
        Assert.False(none.Manifest.TryGetProperty("productionCoverage", out _)); Assert.False(none.Manifest.TryGetProperty("productionCoverageScope", out _)); // only dossier.productionCoverage exists
        Assert.Equal("referenceNumber,supplier,product,cnCode,quantity,unit,status", none.SummaryHeader); // the CSV is unchanged

        var ids = await NewCase(a); var s = await NewSource(a);
        await Save(a, ids.CaseId, Lot(Unique("LOT"), 10, Single(s, 10, "2024-01-01", "2024-02-01")));
        var export = await Export(a, ids.CaseId);
        var coverage = export.Manifest.GetProperty("dossier").GetProperty("productionCoverage");
        Assert.Equal(["BR"], Countries(coverage)); Assert.Equal(1, coverage.GetProperty("plotCount").GetInt32());
        Assert.False(export.Manifest.TryGetProperty("productionCoverage", out _)); Assert.False(export.Manifest.TryGetProperty("productionCoverageScope", out _));
        Assert.Equal("referenceNumber,supplier,product,cnCode,quantity,unit,status", export.SummaryHeader);
    }

    // ---- submission country advisory ------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_submission_country_advisory_is_emitted_only_when_comparable_and_different_and_never_changes_completeness()
    {
        var (a, orgA, _, _) = await Tenants();
        async Task<(Guid CaseId, JsonElement Completeness)> Case(string? submissionCountry, bool withSubmission = true)
        {
            var ids = await NewCase(a); var s = await NewSource(a);
            await Save(a, ids.CaseId, Lot(Unique("LOT"), 10, Single(s, 10, "2024-01-01", "2024-02-01")));
            if (withSubmission) await AddSubmission(orgA, ids.CaseId, submissionCountry);
            return (ids.CaseId, await CompletenessOf(a, ids.CaseId));
        }
        const string Code = "PRODUCTION_COUNTRY_DIFFERS_FROM_SUBMISSION";
        static List<JsonElement> Advisories(JsonElement c) => c.GetProperty("advisories").EnumerateArray().Where(x => x.GetProperty("code").GetString() == Code).ToList();

        var consistent = await Case("BR"); var differs = await Case("CO"); var blank = await Case(null); var absent = await Case(null, withSubmission: false);
        Assert.Empty(Advisories(consistent.Completeness)); Assert.Empty(Advisories(blank.Completeness)); Assert.Empty(Advisories(absent.Completeness));
        var advisory = Assert.Single(Advisories(differs.Completeness));
        Assert.Equal("Production", advisory.GetProperty("area").GetString());
        var message = advisory.GetProperty("message").GetString()!; Assert.Contains("BR", message); Assert.Contains("CO", message); Assert.Contains("not a compliance finding", message);
        Assert.NotEqual(Guid.Empty, advisory.GetProperty("subjectId").GetGuid());
        AssertNoOrganizationId(advisory);

        // Outside every completeness axis and item: the same dossier with a consistent or a differing submission country reads identically.
        Assert.Equal(consistent.Completeness.GetProperty("dataCoverage").GetString(), differs.Completeness.GetProperty("dataCoverage").GetString());
        Assert.Equal(consistent.Completeness.GetProperty("evidenceReadiness").GetString(), differs.Completeness.GetProperty("evidenceReadiness").GetString());
        List<(string, string)> Items(JsonElement c) => c.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("code").GetString()!, i.GetProperty("status").GetString()!)).ToList();
        Assert.Equal(Items(consistent.Completeness), Items(differs.Completeness));
        Assert.DoesNotContain(Items(differs.Completeness), i => i.Item1.Contains("COUNTRY", StringComparison.Ordinal) || i.Item1.Contains("SUBMISSION", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_advisory_does_not_use_the_shipment_origin_country()
    {
        var (a, orgA, _, _) = await Tenants(); var ids = await NewCase(a); var s = await NewSource(a); // shipment origin is unset here; sources are BR
        await Save(a, ids.CaseId, Lot(Unique("LOT"), 10, Single(s, 10, "2024-01-01", "2024-02-01")));
        await AddSubmission(orgA, ids.CaseId, "BR");
        Assert.DoesNotContain((await CompletenessOf(a, ids.CaseId)).GetProperty("advisories").EnumerateArray(), x => x.GetProperty("code").GetString() == "PRODUCTION_COUNTRY_DIFFERS_FROM_SUBMISSION");
    }

    // ---- lot concurrency ----------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Expected_lot_id_contract_null_first_exact_id_updates_and_a_stale_or_null_id_conflicts_with_safe_metadata()
    {
        var (a, orgA, _, _) = await Tenants(); var ids = await NewCase(a); var s = await NewSource(a); var reference = Unique("LOT");
        var wrongFirst = await Reason(await LotSupport.PutExpecting(a, ids.CaseId, Lot(reference, 10), Guid.NewGuid())); // an id when no lot exists
        Assert.Equal(ReasonChanged, wrongFirst.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, wrongFirst.GetProperty("currentLotId").ValueKind); Assert.Equal(JsonValueKind.Null, wrongFirst.GetProperty("currentLotNumber").ValueKind);
        Assert.Equal(HttpStatusCode.NoContent, (await a.GetAsync($"/api/eudr-cases/{ids.CaseId}/dossier")).StatusCode); // nothing was written

        var v1 = await Json(await LotSupport.PutExpecting(a, ids.CaseId, Lot(reference, 10, Single(s, 10)), null), HttpStatusCode.OK); // null = no lot loaded
        var v1Id = LotId(v1); Assert.Equal(1, LotNumber(v1));

        var nullAgain = await Reason(await LotSupport.PutExpecting(a, ids.CaseId, Lot(reference, 12, Single(s, 12)), null)); // null while a lot exists
        Assert.Equal(ReasonChanged, nullAgain.GetProperty("reason").GetString()); Assert.Equal(v1Id, nullAgain.GetProperty("currentLotId").GetGuid()); Assert.Equal(1, nullAgain.GetProperty("currentLotNumber").GetInt32());
        AssertNoOrganizationId(nullAgain);

        var phantom = await Reason(await LotSupport.PutExpecting(a, ids.CaseId, Lot(reference, 12, Single(s, 12)), Guid.NewGuid()));
        Assert.Equal(v1Id, phantom.GetProperty("currentLotId").GetGuid());

        var audits = await LotAuditEvents(orgA, ids.CaseId);
        var v2 = await Json(await LotSupport.PutExpecting(a, ids.CaseId, Lot(reference, 12, Single(s, 12)), v1Id), HttpStatusCode.OK); // exact id
        var v2Id = LotId(v2); Assert.NotEqual(v1Id, v2Id); Assert.Equal(2, LotNumber(v2));
        Assert.Equal(audits + 1, await LotAuditEvents(orgA, ids.CaseId));

        var stale = await Reason(await LotSupport.PutExpecting(a, ids.CaseId, Lot(reference, 13, Single(s, 13)), v1Id)); // v1 is no longer current
        Assert.Equal(v2Id, stale.GetProperty("currentLotId").GetGuid()); Assert.Equal(2, stale.GetProperty("currentLotNumber").GetInt32());
        Assert.Equal(audits + 1, await LotAuditEvents(orgA, ids.CaseId)); // conflicts write nothing
        Assert.Equal(2, (await History(a, ids.CaseId)).GetArrayLength());
    }

    [Fact]
    public async Task A_stale_expected_id_is_checked_before_the_identical_resubmission_no_op()
    {
        var (a, _, _, _) = await Tenants(); var ids = await NewCase(a); var s = await NewSource(a); var reference = Unique("LOT");
        var v1 = await Save(a, ids.CaseId, Lot(reference, 10, Single(s, 10))); var v1Id = LotId(v1);
        var v2 = await Json(await LotSupport.PutExpecting(a, ids.CaseId, Lot(reference, 11, Single(s, 11)), v1Id), HttpStatusCode.OK); var v2Id = LotId(v2);

        // Identical to the current v2 but carrying the stale v1 id (or null): a conflict, never a silent success.
        Assert.Equal(ReasonChanged, (await Reason(await LotSupport.PutExpecting(a, ids.CaseId, Lot(reference, 11, Single(s, 11)), v1Id))).GetProperty("reason").GetString());
        Assert.Equal(ReasonChanged, (await Reason(await LotSupport.PutExpecting(a, ids.CaseId, Lot(reference, 11, Single(s, 11)), null))).GetProperty("reason").GetString());

        // With the exact id the identical resubmission is still the idempotent no-op: same version, no new history.
        var same = await Json(await LotSupport.PutExpecting(a, ids.CaseId, Lot(reference, 11, Single(s, 11)), v2Id), HttpStatusCode.OK);
        Assert.Equal(v2Id, LotId(same)); Assert.Equal(2, (await History(a, ids.CaseId)).GetArrayLength());
    }

    [Fact]
    public async Task Concurrent_writers_that_loaded_the_same_lot_one_wins_and_the_stale_ones_conflict()
    {
        var (a, orgA, _, _) = await Tenants(); var ids = await NewCase(a); var s = await NewSource(a); var reference = Unique("LOT");
        var v1Id = LotId(await Save(a, ids.CaseId, Lot(reference, 10, Single(s, 10))));
        var responses = await Task.WhenAll(Enumerable.Range(1, 5).Select(i => LotSupport.PutExpecting(a, ids.CaseId, Lot(reference, 10 + i, Single(s, 10 + i)), v1Id)));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        foreach (var loser in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            var errors = await Reason(loser); Assert.Equal(ReasonChanged, errors.GetProperty("reason").GetString()); Assert.Equal(2, errors.GetProperty("currentLotNumber").GetInt32());
        }
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, orgA);
        var lots = await db.CaseLots.Where(x => db.CaseDossiers.Any(d => d.Id == x.CaseDossierId && d.EudrCaseId == ids.CaseId)).OrderBy(x => x.Number).ToListAsync();
        Assert.Equal([1, 2], lots.Select(x => x.Number).ToArray()); Assert.Single(lots, x => x.SupersededAtUtc == null);
    }

    [Fact]
    public async Task The_body_without_the_property_means_no_lot_was_loaded_and_validation_and_the_route_case_come_first()
    {
        var (a, _, _, _) = await Tenants(); var ids = await NewCase(a); var s = await NewSource(a); var reference = Unique("LOT");
        // A legacy client that omits the property can still create the first lot ...
        Assert.Equal(HttpStatusCode.OK, (await a.PutAsync($"/api/eudr-cases/{ids.CaseId}/dossier/lot", Raw($$"""{"lotReference":"{{reference}}","quantity":10,"unit":"Kg","note":null,"contributions":[]}"""))).StatusCode);
        // ... but never silently replace an existing one.
        Assert.Equal(ReasonChanged, (await Reason(await a.PutAsync($"/api/eudr-cases/{ids.CaseId}/dossier/lot", Raw($$"""{"lotReference":"{{reference}}","quantity":11,"unit":"Kg","note":null,"contributions":[]}""")))).GetProperty("reason").GetString());
        // An invalid body is a 400 even with a stale id (validation precedes the comparison); an unknown case is a 404 even with an id.
        await Problem(await LotSupport.PutExpecting(a, ids.CaseId, new { lotReference = "", quantity = 1, unit = "Kg", note = (string?)null, contributions = Array.Empty<object>() }, Guid.NewGuid()), HttpStatusCode.BadRequest);
        await Problem(await LotSupport.PutExpecting(a, Guid.NewGuid(), Lot(reference, 10, Single(s, 10)), Guid.NewGuid()), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_foreign_tenant_cannot_use_or_learn_a_lot_id_through_the_concurrency_check()
    {
        var (a, _, b, _) = await Tenants(); var ids = await NewCase(a); var s = await NewSource(a);
        var lotId = LotId(await Save(a, ids.CaseId, Lot(Unique("LOT"), 10, Single(s, 10))));
        var response = await LotSupport.PutExpecting(b, ids.CaseId, Lot(Unique("LOT"), 10), lotId);
        var body = await Problem(response, HttpStatusCode.NotFound);
        Assert.DoesNotContain(lotId.ToString(), body.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.False(body.TryGetProperty("errors", out _));
        Assert.Equal(lotId, LotId(await Dossier(a, ids.CaseId))); // unchanged
    }

    // ---- no schema change ---------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Coverage_and_lot_concurrency_need_no_migration_the_model_matches_the_snapshot()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(db.Database.HasPendingModelChanges(), "The snapshot must match the DbContext: this slice adds no entity, column or index.");
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var lotColumns = db.Model.FindEntityType(typeof(CaseLot))!.GetProperties().Select(p => p.Name).Order().ToArray();
        Assert.Equal(["CaseDossierId", "CreatedAtUtc", "CreatedByUserId", "Id", "LotReference", "Note", "Number", "OrganizationId", "Quantity", "SupersededAtUtc", "Unit", "UpdatedAtUtc"], lotColumns);
    }
}
