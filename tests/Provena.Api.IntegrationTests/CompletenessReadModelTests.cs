using System.IO.Compression;
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
using Xunit;
namespace Provena.Api.IntegrationTests;

// Slice 5 / commit 2: GET completeness (policy v1, three axes, advisories, fingerprint) and the working-export additions, over real HTTP and PostgreSQL.
// Each scenario provisions its own organization through AuthService (the operator profile is per organization and the auth rate limiter is shared).
[Collection("Api")]
public sealed class CompletenessReadModelTests(ApiFactory factory)
{
    internal const string Square = """{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,1],[0,0]]]}""";
    internal const string Bigger = """{"type":"Polygon","coordinates":[[[0,0],[2,0],[2,2],[0,2],[0,0]]]}""";
    private const string GeoFile = """{"type":"Polygon","coordinates":[[[-48,-20],[-47,-20],[-47,-19],[-48,-20]]]}""";

    private async Task<(HttpClient Client, Guid Org)> Tenant()
    {
        var name = "Compl-" + Guid.NewGuid().ToString("N")[..8];
        await using var scope = factory.Services.CreateAsyncScope();
        var token = await scope.ServiceProvider.GetRequiredService<AuthService>().Register(new RegisterRequest($"{name}@example.test", "Test-Only-Password-928!", "Test", "User", "Org " + name, null), CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Organizations.Where(x => x.Name == "Org " + name).Select(x => x.Id).SingleAsync();
        var c = factory.CreateClient(); c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return (c, org);
    }
    internal static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode? expected = null)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (expected.HasValue) Assert.True(expected == response.StatusCode, $"Expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        else Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }
    internal static async Task Ok(Task<HttpResponseMessage> request, HttpStatusCode expected = HttpStatusCode.OK) => await Json(await request, expected);
    internal static StringContent Raw(string json) => new(json, Encoding.UTF8, "application/json");
    internal static void AssertNoOrganizationId(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object: foreach (var p in element.EnumerateObject()) { Assert.NotEqual("organizationid", p.Name.ToLowerInvariant()); AssertNoOrganizationId(p.Value); } break;
            case JsonValueKind.Array: foreach (var item in element.EnumerateArray()) AssertNoOrganizationId(item); break;
        }
    }
    private AppDbContext TenantDb(IServiceScope scope, Guid org)
    {
        var tenant = new TenantContext(new HttpContextAccessor()); tenant.SetOrganization(org);
        return new AppDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>(), tenant);
    }
    private async Task<int> AuditCount(Guid org, Guid caseId)
    { await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org); return await db.AuditEvents.CountAsync(x => x.EudrCaseId == caseId); }

    // ---- fixtures -----------------------------------------------------------------------------------------------------------------------
    internal sealed record Opt(bool Profile = true, bool SupplierFacts = true, string Commodity = "Coffee", string Cn = "0901", bool Applicability = true, bool Recipients = true, bool Lot = true, decimal Contribution = 10m,
        bool Periods = true, bool Links = true, bool Documents = true, bool Accept = true, string Geometry = Square);
    internal sealed record World(HttpClient C, Guid Org, Guid CaseId, Guid ShipmentId, Guid SupplierId, Guid ProductId, string Reference, Guid ProducerId, Guid PlotId, Guid RevisionId, List<Guid> Docs, List<Guid> Links);

    internal static object Application(string scope = "InScope", string flow = "Supported", string activity = "PlacingOnMarketByImport", bool single = true, string? description = "Green coffee beans") => new
    {
        productDescription = description, singleRelevantCommodity = single, activityType = activity, operatorRole = "Operator", activityDate = "2026-06-01", activityDateKind = "Planned",
        scopeCitation = "Regulation (EU) 2023/1115, Annex I", legalBasisVersion = "2023/1115 as applicable", effectiveDate = "2026-01-01", reasoning = "Assessed", regulatoryScope = scope, flowSupport = flow,
    };
    internal static MultipartFormDataContent Upload(string name, string type, string text)
    { var body = new MultipartFormDataContent(); var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text)); file.Headers.ContentType = new MediaTypeHeaderValue(type); body.Add(file, "file", name); return body; }
    internal static Task<HttpResponseMessage> Patch(HttpClient c, string url, object body) => c.SendAsync(new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(body) });

    internal static async Task<World> Build(HttpClient c, Guid org, Opt? options = null)
    {
        var o = options ?? new Opt(); var tag = Guid.NewGuid().ToString("N")[..8];
        if (o.Profile) await Ok(c.PutAsJsonAsync("/api/operator-profile", new { legalName = "Operator d.o.o.", postalAddress = "Ilica 1, Zagreb", email = "operator@example.test", establishmentCountryCode = "HR" }));
        var supplier = (await Json(await c.PostAsJsonAsync("/api/suppliers", o.SupplierFacts
            ? new { name = "Supplier " + tag, email = (string?)"supplier@example.test", postalAddress = (string?)"Street 5, Sao Paulo", countryCode = "BR" }
            : new { name = "Supplier " + tag, email = (string?)null, postalAddress = (string?)null, countryCode = "BR" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var product = (await Json(await c.PostAsJsonAsync("/api/products", new { name = "Product " + tag, cnCode = o.Cn, commodityType = o.Commodity, description = "Catalogue text" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var reference = "CPL-" + tag;
        var shipment = await Json(await c.PostAsJsonAsync("/api/shipments", new { referenceNumber = reference, supplierId = supplier, productId = product, quantity = 10, unit = "Kg" }), HttpStatusCode.Created);
        var caseId = shipment.GetProperty("eudrCaseId").GetGuid(); var shipmentId = shipment.GetProperty("shipment").GetProperty("id").GetGuid();
        await Ok(c.PostAsync($"/api/eudr-cases/{caseId}/master-data/refresh", null));
        if (o.Applicability) await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{caseId}/applicability", Application()));
        if (o.Recipients) await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{caseId}/recipients", new { resolution = "RecipientsIdentified", recipients = new[] { new { name = "Importer GmbH", postalAddress = "Hauptstrasse 1, Berlin", email = "importer@example.test" } } }));
        Guid producer = default, plot = default, revision = default;
        if (o.Lot)
        {
            producer = (await Json(await c.PostAsJsonAsync("/api/producers", new { name = "Producer " + tag, countryCode = "BR" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
            var created = await Json(await c.PostAsync("/api/production-plots", Raw($$"""{"producerId":"{{producer}}","name":"Plot {{tag}}","initialRevision":{"countryCode":"BR","geometry":{{o.Geometry}},"areaHectares":1.5} }""")), HttpStatusCode.Created);
            plot = created.GetProperty("id").GetGuid(); revision = created.GetProperty("currentRevision").GetProperty("id").GetGuid();
            await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{caseId}/dossier/lot", new
            {
                lotReference = "LOT-" + tag, quantity = 10, unit = "Kg", note = (string?)null,
                contributions = new[] { new { producerId = producer, quantity = o.Contribution, unit = "Kg", plots = new[] { new { productionPlotId = plot, plotRevisionId = revision, productionStartDate = o.Periods ? "2024-01-01" : null, productionEndDate = o.Periods ? "2024-12-31" : null } } } },
            }));
        }
        var docs = new List<Guid>(); var links = new List<Guid>();
        if (o.Documents)
        {
            for (var i = 0; i < 2; i++) { using var body = Upload($"evidence{i}.txt", "text/plain", "evidence " + i); docs.Add((await Json(await c.PostAsync($"/api/eudr-cases/{caseId}/documents", body), HttpStatusCode.OK)).GetProperty("id").GetGuid()); }
            using (var geo = Upload("plot.geojson", "application/geo+json", GeoFile)) await Ok(c.PostAsync($"/api/eudr-cases/{caseId}/geolocation", geo));
            if (o.Links)
            {
                links.Add((await Json(await c.PostAsJsonAsync($"/api/eudr-cases/{caseId}/evidence-links", new { caseDocumentId = docs[0], purpose = "Legality" }), HttpStatusCode.Created)).GetProperty("id").GetGuid());
                links.Add((await Json(await c.PostAsJsonAsync($"/api/eudr-cases/{caseId}/evidence-links", new { caseDocumentId = docs[1], purpose = "DeforestationFree" }), HttpStatusCode.Created)).GetProperty("id").GetGuid());
            }
            if (o.Accept)
            {
                foreach (var r in (await Json(await c.GetAsync($"/api/eudr-cases/{caseId}"))).GetProperty("requirements").EnumerateArray())
                {
                    var type = r.GetProperty("type").GetString(); var id = r.GetProperty("id").GetGuid();
                    var body = type is "SupportingDocuments" or "Geolocation" ? (object)new { status = "Accepted", reviewerNote = "ok" } : new { status = "NotRequired", reviewerNote = "Not needed in this pilot case" };
                    Assert.Equal(HttpStatusCode.NoContent, (await Patch(c, $"/api/eudr-cases/{caseId}/requirements/{id}", body)).StatusCode);
                }
            }
        }
        return new(c, org, caseId, shipmentId, supplier, product, reference, producer, plot, revision, docs, links);
    }

    internal static async Task<JsonElement> Completeness(HttpClient c, Guid caseId) => await Json(await c.GetAsync($"/api/eudr-cases/{caseId}/completeness"), HttpStatusCode.OK);
    internal static JsonElement Item(JsonElement completeness, string code, Guid? subject = null) =>
        completeness.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("code").GetString() == code && (subject == null || (i.GetProperty("subjectId").ValueKind == JsonValueKind.String && i.GetProperty("subjectId").GetGuid() == subject)));
    internal static string StatusOf(JsonElement completeness, string code, Guid? subject = null) => Item(completeness, code, subject).GetProperty("status").GetString()!;
    internal static string Fingerprint(JsonElement completeness) => completeness.GetProperty("fingerprint").GetString()!;

    [Fact]
    public async Task Completeness_json_uses_string_enums_iso_timestamps_and_the_planned_shape()
    {
        var (c, org) = await Tenant(); var w = await Build(c, org);
        var response = await c.GetAsync($"/api/eudr-cases/{w.CaseId}/completeness");
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var r = await Json(response);
        Assert.Equal(["policy", "evaluatedAtUtc", "fingerprint", "fingerprintVersion", "applicability", "dataCoverage", "evidenceReadiness", "reviewFreshness", "latestReview", "advisories", "items", "quantityReconciliation"], r.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(JsonValueKind.String, r.GetProperty("dataCoverage").ValueKind); Assert.Equal(JsonValueKind.String, r.GetProperty("evidenceReadiness").ValueKind); Assert.Equal(JsonValueKind.String, r.GetProperty("reviewFreshness").ValueKind);
        Assert.Equal(JsonValueKind.Number, r.GetProperty("fingerprintVersion").ValueKind); Assert.Equal(JsonValueKind.Null, r.GetProperty("latestReview").ValueKind);
        Assert.True(DateTimeOffset.TryParse(r.GetProperty("evaluatedAtUtc").GetString(), out var evaluated) && evaluated > DateTimeOffset.UtcNow.AddMinutes(-5));
        var item = r.GetProperty("items")[0];
        Assert.Equal(["code", "group", "subjectType", "subjectId", "status", "reason"], item.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.All(r.GetProperty("items").EnumerateArray(), i => { Assert.Equal(JsonValueKind.String, i.GetProperty("group").ValueKind); Assert.Equal(JsonValueKind.String, i.GetProperty("status").ValueKind); });
        Assert.Contains(r.GetProperty("items").EnumerateArray(), i => i.GetProperty("group").GetString() == "MasterData");
        AssertNoOrganizationId(r);
        var allowedStatus = new[] { "Satisfied", "Missing", "Invalid", "AwaitingReview", "NotApplicable" };
        Assert.All(r.GetProperty("items").EnumerateArray(), i => Assert.Contains(i.GetProperty("status").GetString(), allowedStatus));
    }

    // ---- access ---------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Completeness_requires_authentication_and_hides_unknown_and_foreign_cases()
    {
        var (a, orgA) = await Tenant(); var (b, _) = await Tenant(); var w = await Build(a, orgA, new Opt(Lot: false, Documents: false));
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/eudr-cases/{w.CaseId}/completeness")).StatusCode);
        var foreign = await b.GetAsync($"/api/eudr-cases/{w.CaseId}/completeness");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode); Assert.Equal("application/problem+json", foreign.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/api/eudr-cases/{Guid.NewGuid()}/completeness")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await a.PostAsync($"/api/eudr-cases/{w.CaseId}/completeness", null)).StatusCode);
    }

    // ---- a case with nothing recorded -----------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_fresh_case_reports_missing_facts_the_policy_and_no_review_state()
    {
        var (c, org) = await Tenant(); var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = (await Json(await c.PostAsJsonAsync("/api/suppliers", new { name = "S" + tag }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var product = (await Json(await c.PostAsJsonAsync("/api/products", new { name = "P" + tag, cnCode = "0901", commodityType = "Coffee" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var caseId = (await Json(await c.PostAsJsonAsync("/api/shipments", new { referenceNumber = "FRESH-" + tag, supplierId = supplier, productId = product, quantity = 10, unit = "Kg" }), HttpStatusCode.Created)).GetProperty("eudrCaseId").GetGuid();
        var result = await Completeness(c, caseId);

        Assert.Equal("provena-pilot-completeness", result.GetProperty("policy").GetProperty("id").GetString()); Assert.Equal("2", result.GetProperty("policy").GetProperty("version").GetString());
        Assert.NotEmpty(result.GetProperty("policy").GetProperty("limitations").EnumerateArray());
        Assert.Matches("^[0-9a-f]{64}$", Fingerprint(result)); Assert.Equal(1, result.GetProperty("fingerprintVersion").GetInt32());
        Assert.Equal("NotRecorded", result.GetProperty("applicability").GetProperty("status").GetString()); Assert.Equal(JsonValueKind.Null, result.GetProperty("applicability").GetProperty("currentVersionId").ValueKind);
        Assert.Equal("Incomplete", result.GetProperty("dataCoverage").GetString()); Assert.Equal("EvidenceMissing", result.GetProperty("evidenceReadiness").GetString());
        Assert.Equal("None", result.GetProperty("reviewFreshness").GetString()); Assert.Equal(JsonValueKind.Null, result.GetProperty("latestReview").ValueKind);
        Assert.Empty(result.GetProperty("advisories").EnumerateArray());
        Assert.Equal(JsonValueKind.String, result.GetProperty("evaluatedAtUtc").ValueKind);
        AssertNoOrganizationId(result);
        foreach (var code in new[] { "MASTER_DATA_SHIPMENT_MATCH", "APPLICABILITY_RECORDED", "PRODUCT_DESCRIPTION", "PRODUCT_CN_CODE", "LOT_PRESENT", "LOT_RECONCILED", "PARTY_OPERATOR", "PARTY_SUPPLIER_NAME", "PARTY_SUPPLIER_POSTAL_ADDRESS", "PARTY_SUPPLIER_EMAIL", "PARTY_RECIPIENTS", "EVIDENCE_LEGALITY_PRESENT", "EVIDENCE_DEFORESTATION_FREE_PRESENT" })
            Assert.Equal("Missing", StatusOf(result, code));
        Assert.Equal(6, result.GetProperty("items").EnumerateArray().Count(i => i.GetProperty("code").GetString()!.StartsWith("REQUIREMENT_")));
        Assert.DoesNotContain("ompliant", result.GetRawText()); Assert.DoesNotContain("SafeToTrade", result.GetRawText());
        Assert.Contains("unresolved", Item(result, "PARTY_RECIPIENTS").GetProperty("reason").GetString());
    }

    // ---- quantity and production ----------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Lot_reconciliation_and_production_periods_drive_the_quantity_and_production_items()
    {
        var (c, org) = await Tenant();
        var under = await Build(c, org, new Opt(Contribution: 6m, Periods: false, Documents: false));
        var r = await Completeness(c, under.CaseId);
        Assert.Equal("Satisfied", StatusOf(r, "LOT_PRESENT")); Assert.Equal("Invalid", StatusOf(r, "LOT_RECONCILED")); Assert.Contains("remaining 4 Kg", Item(r, "LOT_RECONCILED").GetProperty("reason").GetString());
        Assert.Equal("Missing", StatusOf(r, "SOURCE_PRODUCTION_PERIOD")); Assert.Equal("Satisfied", StatusOf(r, "SOURCE_HAS_PLOT")); Assert.Equal("Satisfied", StatusOf(r, "SOURCE_GEOLOCATION_FORM"));
        Assert.Equal("Incomplete", r.GetProperty("dataCoverage").GetString());

        var full = await Build(c, org, new Opt(Documents: false));
        var f = await Completeness(c, full.CaseId);
        Assert.Equal("Satisfied", StatusOf(f, "LOT_RECONCILED")); Assert.Equal("Satisfied", StatusOf(f, "SOURCE_PRODUCTION_PERIOD")); Assert.Equal("Satisfied", StatusOf(f, "SOURCE_GEOLOCATION_FORM", full.RevisionId));
        // Data coverage does not depend on the evidence group. Policy v2: without documents the quantity confirmation stays required and unprovided, so the supplier-confirmed quantity is the one missing data item.
        Assert.Equal("Incomplete", f.GetProperty("dataCoverage").GetString()); Assert.Equal("Missing", StatusOf(f, "SUPPLIER_CONFIRMED_QUANTITY_MATCH"));
        Assert.Equal(["SUPPLIER_CONFIRMED_QUANTITY_MATCH"], f.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("group").GetString() != "Evidence" && i.GetProperty("status").GetString() is "Missing" or "Invalid").Select(i => i.GetProperty("code").GetString()));
        Assert.Equal("EvidenceMissing", f.GetProperty("evidenceReadiness").GetString());
        // Policy v2: a lot that differs from the shipment stays reconciled with its own contributions but no longer matches the shipment.
        await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{full.CaseId}/dossier/lot", new { lotReference = "LOT-X" + Guid.NewGuid().ToString("N")[..6], quantity = 25, unit = "Kg", contributions = new[] { new { producerId = full.ProducerId, quantity = 25, unit = "Kg", plots = new[] { new { productionPlotId = full.PlotId, plotRevisionId = full.RevisionId, productionStartDate = "2024-01-01", productionEndDate = "2024-02-01" } } } } }));
        var differentLot = await Completeness(c, full.CaseId); Assert.Equal("Satisfied", StatusOf(differentLot, "LOT_RECONCILED")); Assert.Equal("Invalid", StatusOf(differentLot, "SHIPMENT_LOT_QUANTITY_MATCH"));

        var none = await Build(c, org, new Opt(Lot: false, Documents: false));
        var n = await Completeness(c, none.CaseId);
        Assert.Equal("Missing", StatusOf(n, "LOT_PRESENT")); Assert.DoesNotContain(n.GetProperty("items").EnumerateArray(), i => i.GetProperty("code").GetString() == "SOURCE_HAS_PLOT");
    }

    // ---- parties --------------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Party_items_follow_the_case_master_data_and_the_recipient_statement()
    {
        var (c, org) = await Tenant();
        var missing = await Build(c, org, new Opt(Profile: false, SupplierFacts: false, Recipients: false, Lot: false, Documents: false));
        var m = await Completeness(c, missing.CaseId);
        foreach (var code in new[] { "PARTY_OPERATOR", "PARTY_SUPPLIER_POSTAL_ADDRESS", "PARTY_SUPPLIER_EMAIL", "PARTY_RECIPIENTS" }) Assert.Equal("Missing", StatusOf(m, code));
        Assert.Equal("Satisfied", StatusOf(m, "PARTY_SUPPLIER_NAME"));

        // The operator profile and the supplier facts become case facts only through a refresh.
        await Ok(c.PutAsJsonAsync("/api/operator-profile", new { legalName = "Operator d.o.o.", postalAddress = "Ilica 1", email = "operator@example.test", establishmentCountryCode = "HR" }));
        Assert.Equal("Missing", StatusOf(await Completeness(c, missing.CaseId), "PARTY_OPERATOR"));
        await Ok(c.PostAsync($"/api/eudr-cases/{missing.CaseId}/master-data/refresh", null));
        Assert.Equal("Satisfied", StatusOf(await Completeness(c, missing.CaseId), "PARTY_OPERATOR"));

        await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{missing.CaseId}/recipients", new { resolution = "NotApplicable", notApplicableBasis = "Goods stay with the operator", recipients = Array.Empty<object>() }));
        var na = Item(await Completeness(c, missing.CaseId), "PARTY_RECIPIENTS");
        Assert.Equal("NotApplicable", na.GetProperty("status").GetString()); Assert.Equal("CaseRecipientStatement", na.GetProperty("subjectType").GetString());
        await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{missing.CaseId}/recipients", new { resolution = "RecipientsIdentified", recipients = new[] { new { name = "R", postalAddress = "A", email = "r@example.test" } } }));
        Assert.Equal("Satisfied", StatusOf(await Completeness(c, missing.CaseId), "PARTY_RECIPIENTS"));
    }

    // ---- applicability and shipment binding -----------------------------------------------------------------------------------------------
    [Fact]
    public async Task Applicability_items_report_pending_outside_scope_unsupported_flow_and_supported()
    {
        var (c, org) = await Tenant(); var w = await Build(c, org, new Opt(Lot: false, Documents: false));
        var supported = await Completeness(c, w.CaseId);
        Assert.Equal("WithinSupportedScope", supported.GetProperty("applicability").GetProperty("status").GetString()); Assert.Equal(JsonValueKind.String, supported.GetProperty("applicability").GetProperty("currentVersionId").ValueKind);
        foreach (var code in new[] { "APPLICABILITY_RECORDED", "APPLICABILITY_DECIDED", "APPLICABILITY_PRODUCT_MATCH", "APPLICABILITY_IN_SCOPE", "FLOW_SUPPORTED", "PRODUCT_DESCRIPTION", "MASTER_DATA_SHIPMENT_MATCH" }) Assert.Equal("Satisfied", StatusOf(supported, code));

        await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/applicability", Application(scope: "Pending", flow: "Pending", description: null)));
        var pending = await Completeness(c, w.CaseId);
        Assert.Equal("Pending", pending.GetProperty("applicability").GetProperty("status").GetString()); Assert.Equal("Missing", StatusOf(pending, "APPLICABILITY_DECIDED")); Assert.Equal("Missing", StatusOf(pending, "PRODUCT_DESCRIPTION"));

        await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/applicability", Application(scope: "OutsideRegulatoryScope", flow: "Pending", description: null)));
        var outside = await Completeness(c, w.CaseId);
        Assert.Equal("OutsideRegulatoryScope", outside.GetProperty("applicability").GetProperty("status").GetString()); Assert.Equal("Invalid", StatusOf(outside, "APPLICABILITY_IN_SCOPE"));
        Assert.Contains("outside this policy's scope", Item(outside, "APPLICABILITY_IN_SCOPE").GetProperty("reason").GetString()); Assert.Equal("NotApplicable", StatusOf(outside, "FLOW_SUPPORTED"));

        await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/applicability", Application(activity: "Export")));
        var unsupported = await Completeness(c, w.CaseId);
        Assert.Equal("UnsupportedFlow", unsupported.GetProperty("applicability").GetProperty("status").GetString()); Assert.Equal("Invalid", StatusOf(unsupported, "FLOW_SUPPORTED"));
        Assert.Equal("Incomplete", unsupported.GetProperty("dataCoverage").GetString());

        var wood = await Build(c, org, new Opt(Commodity: "Wood", Cn: "4407", Lot: false, Documents: false));
        Assert.Equal("Invalid", StatusOf(await Completeness(c, wood.CaseId), "FLOW_SUPPORTED"));
    }

    [Fact]
    public async Task A_shipment_change_makes_the_master_data_mismatch_visible_and_a_refresh_clears_it()
    {
        var (c, org) = await Tenant(); var w = await Build(c, org, new Opt(Lot: false, Documents: false));
        var supplierB = (await Json(await c.PostAsJsonAsync("/api/suppliers", new { name = "Supplier B", email = "b@example.test", postalAddress = "B street", countryCode = "AR" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var productY = (await Json(await c.PostAsJsonAsync("/api/products", new { name = "Product Y", cnCode = "0901", commodityType = "Coffee" }), HttpStatusCode.Created)).GetProperty("id").GetGuid(); // same CN and commodity as X
        await Ok(c.PutAsJsonAsync($"/api/shipments/{w.ShipmentId}", new { referenceNumber = w.Reference, supplierId = supplierB, productId = productY, quantity = 10, unit = "Kg" }));
        var mismatch = await Completeness(c, w.CaseId);
        Assert.Equal("Invalid", StatusOf(mismatch, "MASTER_DATA_SHIPMENT_MATCH")); Assert.Contains("refresh", Item(mismatch, "MASTER_DATA_SHIPMENT_MATCH").GetProperty("reason").GetString());
        Assert.Equal("Incomplete", mismatch.GetProperty("dataCoverage").GetString());
        Assert.Equal(Fingerprint(await Completeness(c, w.CaseId)), Fingerprint(await Completeness(c, w.CaseId)));

        await Ok(c.PostAsync($"/api/eudr-cases/{w.CaseId}/master-data/refresh", null));
        var adopted = await Completeness(c, w.CaseId);
        Assert.Equal("Satisfied", StatusOf(adopted, "MASTER_DATA_SHIPMENT_MATCH"));
        // The old assessment was about product X: the product id alone makes it ProductChanged, though CN code and commodity are equal.
        Assert.Equal("ProductChanged", adopted.GetProperty("applicability").GetProperty("status").GetString()); Assert.Equal("Invalid", StatusOf(adopted, "APPLICABILITY_PRODUCT_MATCH"));
        Assert.NotEqual(Fingerprint(mismatch), Fingerprint(adopted));
        await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/applicability", Application()));
        Assert.Equal("Satisfied", StatusOf(await Completeness(c, w.CaseId), "APPLICABILITY_PRODUCT_MATCH"));
    }

    // ---- evidence axis --------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Submitted_requirements_await_decisions_and_accepted_requirements_with_current_links_are_ready_for_owner_review()
    {
        var (c, org) = await Tenant(); var w = await Build(c, org, new Opt(Accept: false));
        var submitted = await Completeness(c, w.CaseId);
        Assert.Equal("AwaitingReview", StatusOf(submitted, "REQUIREMENT_SupportingDocuments")); Assert.Equal("AwaitingReview", StatusOf(submitted, "REQUIREMENT_Geolocation"));
        Assert.Equal("Missing", StatusOf(submitted, "REQUIREMENT_SupplierInformation")); // the other four still have no evidence
        Assert.Equal("EvidenceMissing", submitted.GetProperty("evidenceReadiness").GetString());
        var detail = await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}"));
        foreach (var r in detail.GetProperty("requirements").EnumerateArray())
        {
            var type = r.GetProperty("type").GetString();
            var body = type is "SupportingDocuments" or "Geolocation" ? (object)new { status = "Accepted", reviewerNote = "ok" } : new { status = "NotRequired", reviewerNote = "Not needed in this pilot case" };
            Assert.Equal(HttpStatusCode.NoContent, (await Patch(c, $"/api/eudr-cases/{w.CaseId}/requirements/{r.GetProperty("id").GetGuid()}", body)).StatusCode);
        }
        var ready = await Completeness(c, w.CaseId);
        Assert.Equal("ReadyForOwnerReview", ready.GetProperty("evidenceReadiness").GetString()); Assert.Equal("Complete", ready.GetProperty("dataCoverage").GetString());
        Assert.Equal("Satisfied", StatusOf(ready, "REQUIREMENT_Geolocation")); Assert.Contains("not a review of the dossier", Item(ready, "REQUIREMENT_Geolocation").GetProperty("reason").GetString());
        Assert.Equal("NotApplicable", StatusOf(ready, "REQUIREMENT_ProductionPeriod"));
        Assert.Equal("None", ready.GetProperty("reviewFreshness").GetString()); Assert.Equal(JsonValueKind.Null, ready.GetProperty("latestReview").ValueKind);
        Assert.DoesNotContain("\"Reviewed\"", ready.GetRawText());
    }

    [Fact]
    public async Task Submitted_only_requirements_report_awaiting_requirement_decisions()
    {
        var (c, org) = await Tenant(); var w = await Build(c, org, new Opt(Accept: false));
        foreach (var r in (await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}"))).GetProperty("requirements").EnumerateArray().Where(r => r.GetProperty("type").GetString() is not ("SupportingDocuments" or "Geolocation")))
            Assert.Equal(HttpStatusCode.NoContent, (await Patch(c, $"/api/eudr-cases/{w.CaseId}/requirements/{r.GetProperty("id").GetGuid()}", new { status = "NotRequired", reviewerNote = "n/a" })).StatusCode);
        Assert.Equal("AwaitingRequirementDecisions", (await Completeness(c, w.CaseId)).GetProperty("evidenceReadiness").GetString());
    }

    [Fact]
    public async Task Withdrawn_links_and_withdrawn_documents_no_longer_count_as_current_evidence()
    {
        var (c, org) = await Tenant(); var w = await Build(c, org);
        Assert.Equal("Satisfied", StatusOf(await Completeness(c, w.CaseId), "EVIDENCE_LEGALITY_PRESENT"));
        Assert.Contains("presence only; not reviewed and not a sufficiency finding", Item(await Completeness(c, w.CaseId), "EVIDENCE_LEGALITY_PRESENT").GetProperty("reason").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/eudr-cases/{w.CaseId}/evidence-links/{w.Links[0]}")).StatusCode);
        var withdrawnLink = await Completeness(c, w.CaseId);
        Assert.Equal("Missing", StatusOf(withdrawnLink, "EVIDENCE_LEGALITY_PRESENT")); Assert.Equal("EvidenceMissing", withdrawnLink.GetProperty("evidenceReadiness").GetString());
        Assert.Equal("Satisfied", StatusOf(withdrawnLink, "EVIDENCE_DEFORESTATION_FREE_PRESENT"));

        // The deforestation-free link is still a current link row, but its document is withdrawn.
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/eudr-cases/{w.CaseId}/documents/{w.Docs[1]}")).StatusCode);
        var links = await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}/evidence-links"));
        Assert.Equal(JsonValueKind.Null, links.EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == w.Links[1]).GetProperty("withdrawnAtUtc").ValueKind);
        Assert.Equal("Missing", StatusOf(await Completeness(c, w.CaseId), "EVIDENCE_DEFORESTATION_FREE_PRESENT"));
    }

    [Fact]
    public async Task A_valid_polygon_without_legality_evidence_does_not_imply_readiness_and_not_required_without_a_note_stays_missing()
    {
        var (c, org) = await Tenant(); var w = await Build(c, org, new Opt(Links: false));
        var r = await Completeness(c, w.CaseId);
        Assert.Equal("Satisfied", StatusOf(r, "SOURCE_GEOLOCATION_FORM", w.RevisionId)); Assert.Equal("Complete", r.GetProperty("dataCoverage").GetString());
        Assert.Equal("Missing", StatusOf(r, "EVIDENCE_LEGALITY_PRESENT")); Assert.Equal("EvidenceMissing", r.GetProperty("evidenceReadiness").GetString());

        var detail = await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}"));
        var quantity = detail.GetProperty("requirements").EnumerateArray().Single(x => x.GetProperty("type").GetString() == "QuantityConfirmation").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await Patch(c, $"/api/eudr-cases/{w.CaseId}/requirements/{quantity}", new { status = "NotRequired" })).StatusCode); // allowed by the unchanged PATCH contract
        var noNote = await Completeness(c, w.CaseId);
        Assert.Equal("Missing", StatusOf(noNote, "REQUIREMENT_QuantityConfirmation")); Assert.NotEqual("NotApplicable", StatusOf(noNote, "REQUIREMENT_QuantityConfirmation"));
        Assert.Equal(HttpStatusCode.NoContent, (await Patch(c, $"/api/eudr-cases/{w.CaseId}/requirements/{quantity}", new { status = "NotRequired", reviewerNote = "Not needed" })).StatusCode);
        Assert.Equal("NotApplicable", StatusOf(await Completeness(c, w.CaseId), "REQUIREMENT_QuantityConfirmation"));
    }

    // ---- advisories and the fingerprint ---------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Catalogue_edits_are_advisory_drift_and_only_fact_bearing_refreshes_change_the_fingerprint()
    {
        var (c, org) = await Tenant(); var w = await Build(c, org);
        var baseline = await Completeness(c, w.CaseId); var fp = Fingerprint(baseline);
        Assert.Empty(baseline.GetProperty("advisories").EnumerateArray()); Assert.Equal(fp, Fingerprint(await Completeness(c, w.CaseId))); // deterministic

        await Ok(c.PutAsJsonAsync($"/api/suppliers/{w.SupplierId}", new { name = "Edited supplier", email = "edited@example.test", postalAddress = "Street 5, Sao Paulo", countryCode = "BR" }));
        var drift = await Completeness(c, w.CaseId);
        var advisory = Assert.Single(drift.GetProperty("advisories").EnumerateArray());
        Assert.Equal("CATALOGUE_DRIFT_SUPPLIER", advisory.GetProperty("code").GetString()); Assert.Equal("Supplier", advisory.GetProperty("area").GetString()); Assert.Contains("name, email", advisory.GetProperty("message").GetString());
        Assert.Equal(fp, Fingerprint(drift)); Assert.Equal(baseline.GetProperty("dataCoverage").GetString(), drift.GetProperty("dataCoverage").GetString()); // advisory only

        await Ok(c.PostAsync($"/api/eudr-cases/{w.CaseId}/master-data/refresh", null));
        var adopted = await Completeness(c, w.CaseId);
        Assert.Empty(adopted.GetProperty("advisories").EnumerateArray()); Assert.NotEqual(fp, Fingerprint(adopted)); // fact-bearing content changed
        var fp2 = Fingerprint(adopted);

        // A context-only change (product name / catalogue description) creates a new master-data version but leaves the fingerprint untouched.
        await Ok(c.PutAsJsonAsync($"/api/products/{w.ProductId}", new { name = "Renamed product", cnCode = "0901", commodityType = "Coffee", description = "Another catalogue text" }));
        Assert.Empty((await Completeness(c, w.CaseId)).GetProperty("advisories").EnumerateArray()); // context never drifts
        await Ok(c.PostAsync($"/api/eudr-cases/{w.CaseId}/master-data/refresh", null));
        Assert.Equal(3, (await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}/master-data-history"))).GetArrayLength());
        Assert.Equal(fp2, Fingerprint(await Completeness(c, w.CaseId)));

        // A product CN change is a fact-bearing drift and, once refreshed, a different fingerprint (and ProductChanged for the old assessment).
        await Ok(c.PutAsJsonAsync($"/api/products/{w.ProductId}", new { name = "Renamed product", cnCode = "0902", commodityType = "Coffee", description = "Another catalogue text" }));
        Assert.Equal("CATALOGUE_DRIFT_PRODUCT", Assert.Single((await Completeness(c, w.CaseId)).GetProperty("advisories").EnumerateArray()).GetProperty("code").GetString());
        await Ok(c.PostAsync($"/api/eudr-cases/{w.CaseId}/master-data/refresh", null));
        var changedCn = await Completeness(c, w.CaseId);
        Assert.NotEqual(fp2, Fingerprint(changedCn)); Assert.Equal("ProductChanged", changedCn.GetProperty("applicability").GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_superseded_pinned_revision_is_an_advisory_and_leaves_the_fingerprint_unchanged_until_the_lot_adopts_a_new_revision()
    {
        var (c, org) = await Tenant(); var w = await Build(c, org);
        var fp = Fingerprint(await Completeness(c, w.CaseId));
        var newRevision = (await Json(await c.PostAsync($"/api/production-plots/{w.PlotId}/revisions", Raw($$"""{"countryCode":"BR","geometry":{{Bigger}},"areaHectares":null,"note":null}""")), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var after = await Completeness(c, w.CaseId);
        var advisory = Assert.Single(after.GetProperty("advisories").EnumerateArray());
        Assert.Equal("PINNED_REVISION_SUPERSEDED", advisory.GetProperty("code").GetString()); Assert.Equal(w.RevisionId, advisory.GetProperty("subjectId").GetGuid());
        Assert.Equal(fp, Fingerprint(after)); Assert.Equal("Complete", after.GetProperty("dataCoverage").GetString());
        // A producer or plot rename is a label change, not a fact.
        await Ok(c.PutAsJsonAsync($"/api/production-plots/{w.PlotId}", new { name = "Renamed plot", isActive = true }));
        await Ok(c.PutAsJsonAsync($"/api/producers/{w.ProducerId}", new { name = "Renamed producer", countryCode = "BR", isActive = true }));
        Assert.Equal(fp, Fingerprint(await Completeness(c, w.CaseId)));
        // Pinning the newer revision is a deliberate lot save and a different fingerprint.
        await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/dossier/lot", new { lotReference = "LOT-NEW" + Guid.NewGuid().ToString("N")[..6], quantity = 10, unit = "Kg", contributions = new[] { new { producerId = w.ProducerId, quantity = 10, unit = "Kg", plots = new[] { new { productionPlotId = w.PlotId, plotRevisionId = newRevision, productionStartDate = "2024-01-01", productionEndDate = "2024-12-31" } } } } }));
        var adopted = await Completeness(c, w.CaseId);
        Assert.NotEqual(fp, Fingerprint(adopted)); Assert.Empty(adopted.GetProperty("advisories").EnumerateArray());
    }

    [Fact]
    public async Task Any_current_evidence_or_requirement_change_changes_the_fingerprint()
    {
        var (c, org) = await Tenant(); var w = await Build(c, org);
        var fp = Fingerprint(await Completeness(c, w.CaseId));
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/eudr-cases/{w.CaseId}/evidence-links/{w.Links[0]}")).StatusCode);
        var withoutLink = Fingerprint(await Completeness(c, w.CaseId)); Assert.NotEqual(fp, withoutLink);
        var detail = await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}"));
        var requirement = detail.GetProperty("requirements").EnumerateArray().Single(x => x.GetProperty("type").GetString() == "SupportingDocuments").GetProperty("id").GetGuid();
        await Task.Delay(20);
        Assert.Equal(HttpStatusCode.NoContent, (await Patch(c, $"/api/eudr-cases/{w.CaseId}/requirements/{requirement}", new { status = "NotRequired", reviewerNote = "n/a" })).StatusCode);
        Assert.NotEqual(withoutLink, Fingerprint(await Completeness(c, w.CaseId)));
        await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/recipients", new { resolution = "NotApplicable", notApplicableBasis = "x", recipients = Array.Empty<object>() }));
        await Ok(c.PutAsJsonAsync($"/api/eudr-cases/{w.CaseId}/applicability", Application(description: "A revised assessed description")));
        Assert.NotEqual(withoutLink, Fingerprint(await Completeness(c, w.CaseId)));
    }

    // ---- read-only behaviour --------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Reading_completeness_writes_nothing_and_does_not_touch_requirements_or_the_case_status()
    {
        var (c, org) = await Tenant(); var w = await Build(c, org);
        var before = await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}")); var audits = await AuditCount(org, w.CaseId);
        for (var i = 0; i < 3; i++) await Completeness(c, w.CaseId);
        var after = await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}"));
        Assert.Equal(before.GetProperty("status").GetString(), after.GetProperty("status").GetString()); Assert.Equal(before.GetProperty("requirements").GetRawText(), after.GetProperty("requirements").GetRawText());
        Assert.Equal(before.GetProperty("updatedAtUtc").GetDateTimeOffset(), after.GetProperty("updatedAtUtc").GetDateTimeOffset()); Assert.Equal(audits, await AuditCount(org, w.CaseId));
    }

    [Fact]
    public async Task A_case_that_is_ready_for_export_and_closed_can_still_report_incomplete_data()
    {
        var (c, org) = await Tenant(); var w = await Build(c, org, new Opt(Profile: false, Recipients: false));
        await Ok(c.PostAsync($"/api/eudr-cases/{w.CaseId}/supplier-request", null));
        var detail = await Json(await c.GetAsync($"/api/eudr-cases/{w.CaseId}"));
        // Operationally the six requirements are decided; the completeness axes say otherwise about the data. Both facts coexist.
        var result = await Completeness(c, w.CaseId);
        Assert.Equal("Incomplete", result.GetProperty("dataCoverage").GetString()); Assert.Equal("None", result.GetProperty("reviewFreshness").GetString());
        Assert.NotEqual("Closed", detail.GetProperty("status").GetString());
        Assert.Equal("ReadyForExport", detail.GetProperty("status").GetString());
        if (detail.GetProperty("status").GetString() == "ReadyForExport")
        {
            await LifecycleTestSupport.SetStatus(factory.Services, w.CaseId, Provena.Api.Domain.EudrCaseStatus.Closed); // closing arrives with the lifecycle routes (Slice 2); completeness is independent of the status
            var closed = await Completeness(c, w.CaseId);
            Assert.Equal("Incomplete", closed.GetProperty("dataCoverage").GetString());
            await Ok(c.PutAsJsonAsync("/api/suppliers/" + w.SupplierId, new { name = "Edited after closure", countryCode = "BR" }));
            Assert.Contains("after case closure", (await Completeness(c, w.CaseId)).GetProperty("advisories")[0].GetProperty("message").GetString());
        }
    }

    // ---- working export ---------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task The_working_export_carries_the_completeness_model_and_keeps_its_existing_contract()
    {
        var (c, org) = await Tenant(); var w = await Build(c, org);
        var response = await c.GetAsync($"/api/eudr-cases/{w.CaseId}/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var manifestEntry = zip.Entries.Single(e => e.FullName.EndsWith("/manifest.json"));
        using var manifest = JsonDocument.Parse(await new StreamReader(manifestEntry.Open()).ReadToEndAsync()); var root = manifest.RootElement;

        var completeness = root.GetProperty("completeness");
        Assert.Equal("provena-pilot-completeness", completeness.GetProperty("policy").GetProperty("id").GetString()); Assert.Equal("None", completeness.GetProperty("reviewFreshness").GetString());
        Assert.Equal(JsonValueKind.Null, completeness.GetProperty("latestReview").ValueKind); Assert.Equal(JsonValueKind.Null, root.GetProperty("latestReview").ValueKind);
        Assert.Equal("Working-current export. Not the reviewed record; use the review export for what was reviewed.", root.GetProperty("reviewScope").GetString());
        Assert.Matches("^[0-9a-f]{64}$", completeness.GetProperty("fingerprint").GetString()!);
        Assert.Equal(Fingerprint(await Completeness(c, w.CaseId)).Length, completeness.GetProperty("fingerprint").GetString()!.Length);
        // Existing members are unchanged.
        Assert.Equal("Operational documentation only. ReadyForExport is not a legal EUDR compliance determination.", root.GetProperty("notice").GetString());
        Assert.StartsWith("Current working evidence at export time. This is not a reviewed dossier snapshot;", root.GetProperty("evidenceScope").GetString());
        Assert.StartsWith("Current operator dossier lot at export time; not a reviewed dossier snapshot", root.GetProperty("dossierScope").GetString());
        Assert.Equal(w.CaseId, root.GetProperty("dossier").GetProperty("eudrCaseId").GetGuid()); Assert.True(root.TryGetProperty("retainedNotIncluded", out _));
        Assert.Contains(zip.Entries, e => e.FullName.Contains("/plots/") && e.FullName.EndsWith(".geojson"));
        var summary = await new StreamReader(zip.Entries.Single(e => e.FullName.EndsWith("/summary.csv")).Open()).ReadToEndAsync();
        Assert.Equal("referenceNumber,supplier,product,cnCode,quantity,unit,status", summary.TrimStart((char)0xFEFF).Split(Environment.NewLine.ToCharArray(), StringSplitOptions.RemoveEmptyEntries)[0].Trim());
    }
}
