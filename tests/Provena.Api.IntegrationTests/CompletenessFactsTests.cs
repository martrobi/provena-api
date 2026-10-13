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

// Slice 5 / commit 1: operator profile, case master-data copy (refresh, drift, shipment binding), applicability, recipients and evidence links over HTTP.
// Tenants are provisioned through AuthService directly because the shared auth rate limiter (10 / 5 minutes) is nearly consumed by other classes.
[Collection("Api")]
public sealed class CompletenessFactsTests(ApiFactory factory)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (string TokenA, Guid OrgA, string TokenB, Guid OrgB)? tenants;
    private async Task<(HttpClient A, Guid OrgA, HttpClient B, Guid OrgB)> Tenants()
    {
        await Gate.WaitAsync();
        try { tenants ??= await Provision(); } finally { Gate.Release(); }
        return (Client(tenants.Value.TokenA), tenants.Value.OrgA, Client(tenants.Value.TokenB), tenants.Value.OrgB);
    }
    // Each test that needs a pristine organization (for example for the operator profile) provisions its own.
    private async Task<(HttpClient Client, Guid Org)> FreshTenant()
    {
        var (token, org) = await RegisterOne("Facts-" + Guid.NewGuid().ToString("N")[..8]);
        return (Client(token), org);
    }
    private async Task<(string, Guid, string, Guid)> Provision()
    {
        var a = await RegisterOne("Facts-" + Guid.NewGuid().ToString("N")[..8]); var b = await RegisterOne("Facts-" + Guid.NewGuid().ToString("N")[..8]);
        return (a.Item1, a.Item2, b.Item1, b.Item2);
    }
    private async Task<(string, Guid)> RegisterOne(string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var token = await scope.ServiceProvider.GetRequiredService<AuthService>().Register(new RegisterRequest($"{name}-{Guid.NewGuid():N}@example.test", "Test-Only-Password-928!", "Test", "User", "Org " + name, null), CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (token.AccessToken, await db.Organizations.Where(x => x.Name == "Org " + name).Select(x => x.Id).SingleAsync());
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
    private AppDbContext TenantDb(IServiceScope scope, Guid org)
    {
        var tenant = new TenantContext(new HttpContextAccessor()); tenant.SetOrganization(org);
        return new AppDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>(), tenant);
    }
    private async Task<List<AuditEvent>> Audit(Guid org, Guid? caseId, string eventType)
    {
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, org);
        return await db.AuditEvents.AsNoTracking().Where(x => x.EudrCaseId == caseId && x.EventType == eventType).ToListAsync();
    }

    // ---- fixtures -----------------------------------------------------------------------------------------------------------------------
    private sealed record CaseIds(Guid CaseId, Guid ShipmentId, Guid SupplierId, Guid ProductId, string Reference);
    private static async Task<Guid> NewSupplier(HttpClient c, string? email = null) =>
        (await Json(await c.PostAsJsonAsync("/api/suppliers", new { name = "Supplier " + Guid.NewGuid().ToString("N")[..8], email, countryCode = "BR" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
    private static async Task<Guid> NewProduct(HttpClient c, string cn = "0901", string commodity = "Coffee") =>
        (await Json(await c.PostAsJsonAsync("/api/products", new { name = "Product " + Guid.NewGuid().ToString("N")[..8], cnCode = cn, commodityType = commodity }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
    private static async Task<CaseIds> NewCase(HttpClient c, string cn = "0901", string commodity = "Coffee")
    {
        var supplier = await NewSupplier(c); var product = await NewProduct(c, cn, commodity);
        var reference = "FACT-" + Guid.NewGuid().ToString("N")[..8];
        var shipment = await Json(await c.PostAsJsonAsync("/api/shipments", new { referenceNumber = reference, supplierId = supplier, productId = product, quantity = 10, unit = "Kg" }), HttpStatusCode.Created);
        return new(shipment.GetProperty("eudrCaseId").GetGuid(), shipment.GetProperty("shipment").GetProperty("id").GetGuid(), supplier, product, reference);
    }
    private static Task<HttpResponseMessage> ChangeShipment(HttpClient c, CaseIds ids, Guid supplierId, Guid productId) =>
        c.PutAsJsonAsync($"/api/shipments/{ids.ShipmentId}", new { referenceNumber = ids.Reference, supplierId, productId, quantity = 10, unit = "Kg" });
    private static async Task<JsonElement> Refresh(HttpClient c, Guid caseId) => await Json(await c.PostAsync($"/api/eudr-cases/{caseId}/master-data/refresh", null), HttpStatusCode.OK);
    private static async Task<JsonElement> MasterData(HttpClient c, Guid caseId) => await Json(await c.GetAsync($"/api/eudr-cases/{caseId}/master-data"), HttpStatusCode.OK);
    private static async Task<JsonElement> MasterDataHistory(HttpClient c, Guid caseId) => await Json(await c.GetAsync($"/api/eudr-cases/{caseId}/master-data-history"), HttpStatusCode.OK);
    private static object Profile(string name = "Operator d.o.o.", string email = "operator@example.test", string? eori = null) =>
        new { legalName = name, postalAddress = "Ilica 1, Zagreb", email, establishmentCountryCode = "HR", eoriNumber = eori };
    private static object Application(string scope = "InScope", string flow = "Supported", string role = "Operator", string? roleDescription = null, string? description = "Green coffee beans",
        string activity = "PlacingOnMarketByImport", bool single = true, string? reasoning = "Assessed by the operator") => new
    {
        productDescription = description, singleRelevantCommodity = single, activityType = activity, operatorRole = role, operatorRoleDescription = roleDescription,
        activityDate = "2026-06-01", activityDateKind = "Planned", scopeCitation = "Regulation (EU) 2023/1115, Annex I", qualifications = (string?)null,
        legalBasisVersion = "2023/1115 as applicable", effectiveDate = "2026-01-01", reasoning, regulatoryScope = scope, flowSupport = flow,
    };
    private static Task<HttpResponseMessage> PutApplicability(HttpClient c, Guid caseId, object body) => c.PutAsJsonAsync($"/api/eudr-cases/{caseId}/applicability", body);
    private static async Task<JsonElement> Applicability(HttpClient c, Guid caseId) => await Json(await c.GetAsync($"/api/eudr-cases/{caseId}/applicability"), HttpStatusCode.OK);
    private static async Task<JsonElement> ApplicabilityHistory(HttpClient c, Guid caseId) => await Json(await c.GetAsync($"/api/eudr-cases/{caseId}/applicability-history"), HttpStatusCode.OK);
    private static async Task<(CaseIds Ids, JsonElement Copy)> CaseWithCopy(HttpClient c)
    { var ids = await NewCase(c); return (ids, await Refresh(c, ids.CaseId)); }
    private static object Recipient(string name = "Importer GmbH", string address = "Hauptstrasse 1, Berlin", string email = "importer@example.test") => new { name, postalAddress = address, email };
    private static object Identified(params object[] recipients) => new { resolution = "RecipientsIdentified", notApplicableBasis = (string?)null, recipients };
    private static MultipartFormDataContent Upload(string text = "evidence")
    {
        var body = new MultipartFormDataContent(); var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text)); file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        body.Add(file, "file", "evidence.txt"); return body;
    }
    private static async Task<Guid> NewDocument(HttpClient c, Guid caseId, string text = "evidence")
    {
        using var body = Upload(text);
        return (await Json(await c.PostAsync($"/api/eudr-cases/{caseId}/documents", body), HttpStatusCode.OK)).GetProperty("id").GetGuid();
    }
    private static Task<HttpResponseMessage> PostLink(HttpClient c, Guid caseId, Guid documentId, string purpose = "Legality", string? note = null) =>
        c.PostAsJsonAsync($"/api/eudr-cases/{caseId}/evidence-links", new { caseDocumentId = documentId, purpose, note });
    private static async Task<JsonElement> Links(HttpClient c, Guid caseId) => await Json(await c.GetAsync($"/api/eudr-cases/{caseId}/evidence-links"), HttpStatusCode.OK);

    // ---- operator profile ---------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Operator_profile_is_204_until_saved_then_upserted_in_place_and_tenant_isolated()
    {
        var (a, orgA) = await FreshTenant(); var (b, _) = await FreshTenant();
        Assert.Equal(HttpStatusCode.NoContent, (await a.GetAsync("/api/operator-profile")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await b.GetAsync("/api/operator-profile")).StatusCode);

        var created = await Json(await a.PutAsJsonAsync("/api/operator-profile", Profile(eori: "HR12345678901")), HttpStatusCode.OK);
        Assert.Equal("Operator d.o.o.", created.GetProperty("legalName").GetString()); Assert.Equal("HR12345678901", created.GetProperty("eoriNumber").GetString());
        Assert.Equal(JsonValueKind.String, created.GetProperty("updatedByUserId").ValueKind);
        AssertNoOrganizationId(created);

        var updated = await Json(await a.PutAsJsonAsync("/api/operator-profile", Profile("Renamed d.o.o.", "other@example.test")), HttpStatusCode.OK);
        Assert.Equal(created.GetProperty("id").GetGuid(), updated.GetProperty("id").GetGuid()); // one profile per organization, updated in place
        Assert.Equal("Renamed d.o.o.", updated.GetProperty("legalName").GetString()); Assert.Equal(JsonValueKind.Null, updated.GetProperty("eoriNumber").ValueKind);
        Assert.Equal("Renamed d.o.o.", (await Json(await a.GetAsync("/api/operator-profile"), HttpStatusCode.OK)).GetProperty("legalName").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await b.GetAsync("/api/operator-profile")).StatusCode);

        // Audit carries identifiers only: no personal, address or e-mail content.
        var events = await Audit(orgA, null, "OperatorProfileSaved");
        Assert.Equal(2, events.Count);
        Assert.All(events, e => { Assert.Contains(created.GetProperty("id").GetString()!, e.Description); Assert.DoesNotContain("example.test", e.Description); Assert.DoesNotContain("Ilica", e.Description); Assert.DoesNotContain("Renamed", e.Description); });
    }

    [Fact]
    public async Task Operator_profile_validation_and_no_delete()
    {
        var (a, _) = await FreshTenant();
        foreach (var bad in new object[]
        {
            new { legalName = "", postalAddress = "A", email = "a@example.test", establishmentCountryCode = "HR" },
            new { legalName = "N", postalAddress = " ", email = "a@example.test", establishmentCountryCode = "HR" },
            new { legalName = "N", postalAddress = "A", email = "not-an-email", establishmentCountryCode = "HR" },
            new { legalName = "N", postalAddress = "A", email = "a@example.test", establishmentCountryCode = "hr" },
            new { legalName = "N", postalAddress = "A", email = "a@example.test", establishmentCountryCode = "HR", eoriNumber = "not valid!" },
            new { legalName = new string('x', 201), postalAddress = "A", email = "a@example.test", establishmentCountryCode = "HR" },
        })
            await Problem(await a.PutAsJsonAsync("/api/operator-profile", bad), HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.NoContent, (await a.GetAsync("/api/operator-profile")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await a.DeleteAsync("/api/operator-profile")).StatusCode);
        // A client-supplied organizationId is ignored (the profile belongs to the caller's organization).
        var saved = await Json(await a.PutAsJsonAsync("/api/operator-profile", new { legalName = "N", postalAddress = "A", email = "a@example.test", establishmentCountryCode = "HR", organizationId = Guid.NewGuid() }), HttpStatusCode.OK);
        AssertNoOrganizationId(saved);
    }

    // ---- supplier postal address -------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Supplier_postal_address_is_optional_round_trips_and_imports_leave_it_null()
    {
        var (a, _, _, _) = await Tenants();
        var legacy = await Json(await a.PostAsJsonAsync("/api/suppliers", new { name = "Legacy client supplier", email = "l@example.test" }), HttpStatusCode.Created); // a client that does not know the field
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("postalAddress").ValueKind);
        var withAddress = await Json(await a.PostAsJsonAsync("/api/suppliers", new { name = "Addressed supplier", postalAddress = "  Street 5, Sao Paulo " }), HttpStatusCode.Created);
        Assert.Equal("Street 5, Sao Paulo", withAddress.GetProperty("postalAddress").GetString());
        var cleared = await Json(await a.PutAsJsonAsync($"/api/suppliers/{withAddress.GetProperty("id").GetGuid()}", new { name = "Addressed supplier", postalAddress = "   " }), HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("postalAddress").ValueKind);
        await Problem(await a.PostAsJsonAsync("/api/suppliers", new { name = "Too long", postalAddress = new string('x', 1001) }), HttpStatusCode.BadRequest);

        // The CSV header is unchanged and imported suppliers keep a null postal address.
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var csv = new MultipartFormDataContent(); var file = new ByteArrayContent(Encoding.UTF8.GetBytes($"externalReference,name,email,contactPerson,countryCode\nPA-{tag},Imported {tag},,,BR\n")); file.Headers.ContentType = new MediaTypeHeaderValue("text/csv"); csv.Add(file, "file", "suppliers.csv");
        Assert.Equal(1, (await Json(await a.PostAsync("/api/import/suppliers", csv), HttpStatusCode.OK)).GetProperty("imported").GetInt32());
        var imported = (await Json(await a.GetAsync($"/api/suppliers?search=Imported {tag}"), HttpStatusCode.OK)).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, imported.GetProperty("postalAddress").ValueKind);
    }

    // ---- master data: refresh, history, idempotency, drift ----------------------------------------------------------------------------------
    [Fact]
    public async Task Master_data_is_204_until_refreshed_then_copies_the_live_values_and_is_idempotent()
    {
        var (a, orgA, b, _) = await Tenants();
        var ids = await NewCase(a);
        Assert.Equal(HttpStatusCode.NoContent, (await a.GetAsync($"/api/eudr-cases/{ids.CaseId}/master-data")).StatusCode);
        Assert.Empty((await MasterDataHistory(a, ids.CaseId)).EnumerateArray());
        await Problem(await b.GetAsync($"/api/eudr-cases/{ids.CaseId}/master-data"), HttpStatusCode.NotFound);
        await Problem(await b.PostAsync($"/api/eudr-cases/{ids.CaseId}/master-data/refresh", null), HttpStatusCode.NotFound);
        await Problem(await b.GetAsync($"/api/eudr-cases/{ids.CaseId}/master-data-history"), HttpStatusCode.NotFound);
        await Problem(await a.GetAsync($"/api/eudr-cases/{Guid.NewGuid()}/master-data"), HttpStatusCode.NotFound);

        var first = await Refresh(a, ids.CaseId); var current = first.GetProperty("current");
        Assert.Equal(1, current.GetProperty("number").GetInt32()); Assert.True(first.GetProperty("shipmentMatch").GetBoolean()); Assert.Empty(first.GetProperty("drift").EnumerateArray());
        Assert.Equal(ids.SupplierId, current.GetProperty("supplier").GetProperty("supplierId").GetGuid()); Assert.Equal(ids.ProductId, current.GetProperty("product").GetProperty("productId").GetGuid());
        Assert.Equal("0901", current.GetProperty("product").GetProperty("cnCode").GetString()); Assert.Equal("Coffee", current.GetProperty("product").GetProperty("commodity").GetString());
        Assert.Equal(JsonValueKind.Null, current.GetProperty("operator").GetProperty("legalName").ValueKind); // the organization has no profile at this point
        Assert.Equal(JsonValueKind.Null, current.GetProperty("supersededAtUtc").ValueKind);
        AssertNoOrganizationId(first);
        Assert.Equal(first.GetProperty("current").GetProperty("id").GetGuid(), (await MasterData(a, ids.CaseId)).GetProperty("current").GetProperty("id").GetGuid());

        // Identical values: the same version is returned, nothing is added, and no audit event is written.
        var again = await Refresh(a, ids.CaseId);
        Assert.Equal(current.GetProperty("id").GetGuid(), again.GetProperty("current").GetProperty("id").GetGuid());
        Assert.Single((await MasterDataHistory(a, ids.CaseId)).EnumerateArray());
        Assert.Single(await Audit(orgA, ids.CaseId, "CaseMasterDataRefreshed"));
    }

    [Fact]
    public async Task Catalogue_edits_create_advisory_drift_only_and_a_refresh_adopts_them_as_a_new_version()
    {
        var (a, orgA, _, _) = await Tenants();
        var ids = await NewCase(a); var firstRefresh = await Refresh(a, ids.CaseId);
        var beforeUpdatedAt = (await Json(await a.GetAsync($"/api/eudr-cases/{ids.CaseId}"))).GetProperty("updatedAtUtc").GetDateTimeOffset();

        await Status(a.PutAsJsonAsync($"/api/suppliers/{ids.SupplierId}", new { name = "Renamed supplier", email = "new@example.test", countryCode = "AR", postalAddress = "New street 1" }), HttpStatusCode.OK);
        var drifted = await MasterData(a, ids.CaseId);
        // Drift is advisory: the copy and the case are untouched.
        Assert.Equal(firstRefresh.GetProperty("current").GetProperty("id").GetGuid(), drifted.GetProperty("current").GetProperty("id").GetGuid());
        var entry = Assert.Single(drifted.GetProperty("drift").EnumerateArray());
        Assert.Equal("Supplier", entry.GetProperty("area").GetString());
        Assert.Equal(["name", "postalAddress", "email", "countryCode"], entry.GetProperty("fields").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Single((await MasterDataHistory(a, ids.CaseId)).EnumerateArray());
        Assert.Equal(beforeUpdatedAt, (await Json(await a.GetAsync($"/api/eudr-cases/{ids.CaseId}"))).GetProperty("updatedAtUtc").GetDateTimeOffset());

        // A product's CN code change drifts too, but its name and catalogue description are context and never drift.
        await Status(a.PutAsJsonAsync($"/api/products/{ids.ProductId}", new { name = "Renamed product", cnCode = "0901", commodityType = "Coffee", description = "New catalogue text" }), HttpStatusCode.OK);
        Assert.Single((await MasterData(a, ids.CaseId)).GetProperty("drift").EnumerateArray());
        await Status(a.PutAsJsonAsync($"/api/products/{ids.ProductId}", new { name = "Renamed product", cnCode = "1801", commodityType = "Cocoa", description = "New catalogue text" }), HttpStatusCode.OK);
        var both = (await MasterData(a, ids.CaseId)).GetProperty("drift").EnumerateArray().ToList();
        Assert.Equal(["Supplier", "Product"], both.Select(x => x.GetProperty("area").GetString()!).ToArray());
        Assert.Equal(["cnCode", "commodity"], both[1].GetProperty("fields").EnumerateArray().Select(x => x.GetString()!).ToArray());

        var refreshed = await Refresh(a, ids.CaseId);
        Assert.Equal(2, refreshed.GetProperty("current").GetProperty("number").GetInt32()); Assert.Empty(refreshed.GetProperty("drift").EnumerateArray());
        Assert.Equal("Renamed supplier", refreshed.GetProperty("current").GetProperty("supplier").GetProperty("name").GetString());
        Assert.Equal("1801", refreshed.GetProperty("current").GetProperty("product").GetProperty("cnCode").GetString());
        var history = (await MasterDataHistory(a, ids.CaseId)).EnumerateArray().ToList();
        Assert.Equal([2, 1], history.Select(x => x.GetProperty("number").GetInt32()).ToArray()); // newest first
        Assert.Equal(JsonValueKind.Null, history[0].GetProperty("supersededAtUtc").ValueKind); Assert.Equal(JsonValueKind.String, history[1].GetProperty("supersededAtUtc").ValueKind);
        Assert.NotEqual("Renamed supplier", history[1].GetProperty("supplier").GetProperty("name").GetString());
        Assert.Equal(2, (await Audit(orgA, ids.CaseId, "CaseMasterDataRefreshed")).Count);
    }

    [Fact]
    public async Task A_context_only_catalogue_change_creates_no_drift_but_a_refresh_still_records_a_new_version()
    {
        var (a, _, _, _) = await Tenants(); var (ids, _) = await CaseWithCopy(a);
        await Status(a.PutAsJsonAsync($"/api/products/{ids.ProductId}", new { name = "Context rename", cnCode = "0901", commodityType = "Coffee", description = "Context text" }), HttpStatusCode.OK);
        Assert.Empty((await MasterData(a, ids.CaseId)).GetProperty("drift").EnumerateArray());
        var refreshed = await Refresh(a, ids.CaseId);
        Assert.Equal(2, refreshed.GetProperty("current").GetProperty("number").GetInt32());
        Assert.Equal("Context rename", refreshed.GetProperty("current").GetProperty("product").GetProperty("name").GetString());
        Assert.Equal("Context text", refreshed.GetProperty("current").GetProperty("product").GetProperty("catalogueDescription").GetString());
    }

    [Fact]
    public async Task Operator_profile_changes_drift_and_are_adopted_by_refresh()
    {
        var (a, _) = await FreshTenant();
        var ids = await NewCase(a); await Refresh(a, ids.CaseId);
        await Status(a.PutAsJsonAsync("/api/operator-profile", Profile()), HttpStatusCode.OK);
        var drift = Assert.Single((await MasterData(a, ids.CaseId)).GetProperty("drift").EnumerateArray());
        Assert.Equal("Operator", drift.GetProperty("area").GetString());
        Assert.Equal(["legalName", "postalAddress", "email", "establishmentCountryCode"], drift.GetProperty("fields").EnumerateArray().Select(x => x.GetString()!).ToArray()); // EORI is null on both sides
        var refreshed = (await Refresh(a, ids.CaseId)).GetProperty("current").GetProperty("operator");
        Assert.Equal("Operator d.o.o.", refreshed.GetProperty("legalName").GetString()); Assert.Equal("HR", refreshed.GetProperty("establishmentCountryCode").GetString());
        Assert.Empty((await MasterData(a, ids.CaseId)).GetProperty("drift").EnumerateArray());
    }

    [Fact]
    public async Task Parallel_refreshes_serialize_on_the_case_lock_and_add_exactly_one_version()
    {
        var (a, _, _, _) = await Tenants(); var (ids, _) = await CaseWithCopy(a);
        await Status(a.PutAsJsonAsync($"/api/suppliers/{ids.SupplierId}", new { name = "Parallel supplier", countryCode = "BR" }), HttpStatusCode.OK);
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => a.PostAsync($"/api/eudr-cases/{ids.CaseId}/master-data/refresh", null)));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var history = (await MasterDataHistory(a, ids.CaseId)).EnumerateArray().ToList();
        Assert.Equal([2, 1], history.Select(x => x.GetProperty("number").GetInt32()).ToArray());
        Assert.Single(history, x => x.GetProperty("supersededAtUtc").ValueKind == JsonValueKind.Null);
    }

    // ---- shipment binding ---------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task A_shipment_that_changes_supplier_and_product_makes_the_copy_mismatch_until_refreshed()
    {
        var (a, _, _, _) = await Tenants();
        var ids = await NewCase(a); // shipment A / X
        var supplierB = await NewSupplier(a); var productY = await NewProduct(a, "1801", "Cocoa");
        var copy = await Refresh(a, ids.CaseId);
        Assert.True(copy.GetProperty("shipmentMatch").GetBoolean());
        await Status(PutApplicability(a, ids.CaseId, Application()), HttpStatusCode.OK);

        await Status(ChangeShipment(a, ids, supplierB, productY), HttpStatusCode.OK); // B / Y while the case is still editable and has no dossier
        var mismatched = await MasterData(a, ids.CaseId);
        Assert.False(mismatched.GetProperty("shipmentMatch").GetBoolean());
        Assert.Equal(copy.GetProperty("current").GetProperty("id").GetGuid(), mismatched.GetProperty("current").GetProperty("id").GetGuid());
        var blocked = await Problem(await PutApplicability(a, ids.CaseId, Application(description: "Cocoa beans")), HttpStatusCode.Conflict);
        Assert.Contains("Refresh the case master data first", blocked.GetProperty("detail").GetString());

        var adopted = await Refresh(a, ids.CaseId);
        Assert.True(adopted.GetProperty("shipmentMatch").GetBoolean()); Assert.Equal(2, adopted.GetProperty("current").GetProperty("number").GetInt32());
        Assert.Equal(supplierB, adopted.GetProperty("current").GetProperty("supplier").GetProperty("supplierId").GetGuid());
        Assert.Equal(productY, adopted.GetProperty("current").GetProperty("product").GetProperty("productId").GetGuid());
        Assert.True((await MasterData(a, ids.CaseId)).GetProperty("shipmentMatch").GetBoolean());
        // The earlier assessment was about product X: it reads as ProductChanged until re-assessed against the adopted copy.
        Assert.Equal("ProductChanged", (await Applicability(a, ids.CaseId)).GetProperty("status").GetString());
        await Status(PutApplicability(a, ids.CaseId, Application(description: "Cocoa beans")), HttpStatusCode.OK);
        Assert.Equal("WithinSupportedScope", (await Applicability(a, ids.CaseId)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_supplier_only_shipment_change_also_mismatches_and_is_adopted_by_refresh()
    {
        var (a, _, _, _) = await Tenants(); var (ids, _) = await CaseWithCopy(a);
        var otherSupplier = await NewSupplier(a);
        await Status(ChangeShipment(a, ids, otherSupplier, ids.ProductId), HttpStatusCode.OK);
        Assert.False((await MasterData(a, ids.CaseId)).GetProperty("shipmentMatch").GetBoolean());
        await Problem(await PutApplicability(a, ids.CaseId, Application()), HttpStatusCode.Conflict);
        Assert.True((await Refresh(a, ids.CaseId)).GetProperty("shipmentMatch").GetBoolean());
        await Status(PutApplicability(a, ids.CaseId, Application()), HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_supplier_or_product_referenced_only_by_a_case_copy_can_no_longer_be_deleted()
    {
        var (a, _, _, _) = await Tenants(); var (ids, _) = await CaseWithCopy(a);
        var supplierB = await NewSupplier(a); var productY = await NewProduct(a);
        await Status(ChangeShipment(a, ids, supplierB, productY), HttpStatusCode.OK); // the shipment no longer uses the original supplier/product, the case copy still does
        await Problem(await a.DeleteAsync($"/api/suppliers/{ids.SupplierId}"), HttpStatusCode.Conflict);
        await Problem(await a.DeleteAsync($"/api/products/{ids.ProductId}"), HttpStatusCode.Conflict);
        await Refresh(a, ids.CaseId); // the retained copy version still references them
        await Problem(await a.DeleteAsync($"/api/suppliers/{ids.SupplierId}"), HttpStatusCode.Conflict);
        await Status(a.DeleteAsync($"/api/suppliers/{await NewSupplier(a)}"), HttpStatusCode.NoContent); // unreferenced rows are still deletable
    }

    // ---- applicability ------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Applicability_requires_a_master_data_copy_and_is_204_until_recorded()
    {
        var (a, _, _, _) = await Tenants(); var ids = await NewCase(a);
        Assert.Equal(HttpStatusCode.NoContent, (await a.GetAsync($"/api/eudr-cases/{ids.CaseId}/applicability")).StatusCode);
        Assert.Empty((await ApplicabilityHistory(a, ids.CaseId)).EnumerateArray());
        var conflict = await Problem(await PutApplicability(a, ids.CaseId, Application()), HttpStatusCode.Conflict);
        Assert.Contains("Refresh the case master data first", conflict.GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await a.GetAsync($"/api/eudr-cases/{ids.CaseId}/applicability")).StatusCode);
        await Refresh(a, ids.CaseId);
        await Status(PutApplicability(a, ids.CaseId, Application()), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Applicability_is_versioned_idempotent_and_copies_the_assessed_product_from_the_master_data()
    {
        var (a, orgA, b, _) = await Tenants(); var (ids, _) = await CaseWithCopy(a);
        await Problem(await b.GetAsync($"/api/eudr-cases/{ids.CaseId}/applicability"), HttpStatusCode.NotFound);
        await Problem(await b.GetAsync($"/api/eudr-cases/{ids.CaseId}/applicability-history"), HttpStatusCode.NotFound);
        await Problem(await PutApplicability(b, ids.CaseId, Application()), HttpStatusCode.NotFound);

        // Client-supplied assessed identity and decision stamps are ignored.
        var hostile = new Dictionary<string, object?>
        {
            ["assessedProductId"] = Guid.NewGuid(), ["assessedCnCode"] = "9999", ["assessedCommodity"] = "Wood", ["decidedByUserId"] = Guid.NewGuid(), ["number"] = 99, ["organizationId"] = Guid.NewGuid(),
        };
        foreach (var p in Application().GetType().GetProperties()) hostile[p.Name] = p.GetValue(Application());
        var first = await Json(await PutApplicability(a, ids.CaseId, hostile), HttpStatusCode.OK);
        var v1 = first.GetProperty("current");
        Assert.Equal("WithinSupportedScope", first.GetProperty("status").GetString()); Assert.Equal(1, v1.GetProperty("number").GetInt32());
        Assert.Equal(ids.ProductId, v1.GetProperty("assessedProductId").GetGuid()); Assert.Equal("0901", v1.GetProperty("assessedCnCode").GetString()); Assert.Equal("Coffee", v1.GetProperty("assessedCommodity").GetString());
        Assert.Equal(JsonValueKind.String, v1.GetProperty("decidedByUserId").ValueKind); Assert.Equal(JsonValueKind.String, v1.GetProperty("decidedAtUtc").ValueKind);
        Assert.Equal("2026-06-01", v1.GetProperty("activityDate").GetString()); Assert.Equal("Planned", v1.GetProperty("activityDateKind").GetString());
        Assert.Equal("InScope", v1.GetProperty("regulatoryScope").GetString()); Assert.Equal("PlacingOnMarketByImport", v1.GetProperty("activityType").GetString()); Assert.Equal("Operator", v1.GetProperty("operatorRole").GetString());
        AssertNoOrganizationId(first);

        // An identical resubmission (whitespace aside) is a 200 no-op: same version, no audit event, no history row.
        var same = await Json(await PutApplicability(a, ids.CaseId, Application(description: "  Green coffee beans ")), HttpStatusCode.OK);
        Assert.Equal(v1.GetProperty("id").GetGuid(), same.GetProperty("current").GetProperty("id").GetGuid());
        Assert.Single((await ApplicabilityHistory(a, ids.CaseId)).EnumerateArray()); Assert.Single(await Audit(orgA, ids.CaseId, "CaseApplicabilitySaved"));

        var second = await Json(await PutApplicability(a, ids.CaseId, Application(reasoning: "A revised assessment")), HttpStatusCode.OK);
        Assert.Equal(2, second.GetProperty("current").GetProperty("number").GetInt32());
        var history = (await ApplicabilityHistory(a, ids.CaseId)).EnumerateArray().ToList();
        Assert.Equal([2, 1], history.Select(x => x.GetProperty("number").GetInt32()).ToArray());
        Assert.Equal(JsonValueKind.Null, history[0].GetProperty("supersededAtUtc").ValueKind); Assert.Equal(JsonValueKind.String, history[1].GetProperty("supersededAtUtc").ValueKind);
        Assert.Equal("Assessed by the operator", history[1].GetProperty("reasoning").GetString());
        Assert.Equal(2, (await Audit(orgA, ids.CaseId, "CaseApplicabilitySaved")).Count);
        Assert.All(await Audit(orgA, ids.CaseId, "CaseApplicabilitySaved"), e => Assert.DoesNotContain("assessment", e.Description)); // identifiers only
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await a.DeleteAsync($"/api/eudr-cases/{ids.CaseId}/applicability")).StatusCode);
    }

    [Fact]
    public async Task Applicability_status_follows_the_documented_derivation_over_http()
    {
        var (a, _, _, _) = await Tenants(); var (ids, _) = await CaseWithCopy(a);
        async Task<string> Put(object body) => (await Json(await PutApplicability(a, ids.CaseId, body), HttpStatusCode.OK)).GetProperty("status").GetString()!;
        var pending = new { productDescription = (string?)null, singleRelevantCommodity = true, activityType = "PlacingOnMarketByImport", operatorRole = "Operator", activityDate = (string?)null, activityDateKind = "Planned", regulatoryScope = "Pending", flowSupport = "Pending" };
        Assert.Equal("Pending", await Put(pending));
        Assert.Equal("Pending", await Put(Application(flow: "Pending")));
        Assert.Equal("OutsideRegulatoryScope", await Put(Application(scope: "OutsideRegulatoryScope", flow: "Pending", description: null)));
        Assert.Equal("UnsupportedFlow", await Put(Application(flow: "UnsupportedFlow")));
        Assert.Equal("UnsupportedFlow", await Put(Application(activity: "Export")));
        Assert.Equal("UnsupportedFlow", await Put(Application(single: false)));
        Assert.Equal("WithinSupportedScope", await Put(Application(activity: "PlacingOnMarketDomestic")));
        Assert.Equal("WithinSupportedScope", await Put(Application()));
        // A pending record carries no decision stamp.
        await Put(pending);
        Assert.Equal(JsonValueKind.Null, (await Applicability(a, ids.CaseId)).GetProperty("current").GetProperty("decidedByUserId").ValueKind);
    }

    [Fact]
    public async Task Wood_cattle_and_other_commodities_derive_an_unsupported_flow()
    {
        var (a, _, _, _) = await Tenants();
        foreach (var commodity in new[] { "Wood", "Cattle", "Other" })
        {
            var ids = await NewCase(a, "4407", commodity); await Refresh(a, ids.CaseId);
            var result = await Json(await PutApplicability(a, ids.CaseId, Application()), HttpStatusCode.OK);
            Assert.Equal("UnsupportedFlow", result.GetProperty("status").GetString()); Assert.Equal(commodity, result.GetProperty("current").GetProperty("assessedCommodity").GetString());
        }
    }

    [Fact]
    public async Task Other_unsupported_role_is_stored_with_its_description_and_derives_an_unsupported_flow()
    {
        var (a, _, _, _) = await Tenants(); var (ids, _) = await CaseWithCopy(a);
        await Problem(await PutApplicability(a, ids.CaseId, Application(role: "OtherUnsupported")), HttpStatusCode.BadRequest);
        await Problem(await PutApplicability(a, ids.CaseId, Application(role: "OtherUnsupported", roleDescription: "  ")), HttpStatusCode.BadRequest);
        await Problem(await PutApplicability(a, ids.CaseId, Application(role: "Operator", roleDescription: "Downstream trader")), HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.NoContent, (await a.GetAsync($"/api/eudr-cases/{ids.CaseId}/applicability")).StatusCode);
        var saved = await Json(await PutApplicability(a, ids.CaseId, Application(role: "OtherUnsupported", roleDescription: " Downstream trader ")), HttpStatusCode.OK);
        Assert.Equal("UnsupportedFlow", saved.GetProperty("status").GetString());
        Assert.Equal("OtherUnsupported", saved.GetProperty("current").GetProperty("operatorRole").GetString());
        Assert.Equal("Downstream trader", saved.GetProperty("current").GetProperty("operatorRoleDescription").GetString());
        // Back to Operator: the description must go with it.
        var operatorAgain = await Json(await PutApplicability(a, ids.CaseId, Application()), HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, operatorAgain.GetProperty("current").GetProperty("operatorRoleDescription").ValueKind);
    }

    [Fact]
    public async Task Applicability_validation_is_400_and_a_failed_save_adds_no_version()
    {
        var (a, _, _, _) = await Tenants(); var (ids, _) = await CaseWithCopy(a);
        foreach (var bad in new object[]
        {
            Application(description: null), Application(description: " "), Application(reasoning: ""), Application(description: new string('x', 2001)),
            new { productDescription = "d", singleRelevantCommodity = true, activityType = "PlacingOnMarketByImport", operatorRole = "Operator", activityDate = "2026-06-01", activityDateKind = "Planned", scopeCitation = "c", legalBasisVersion = "l", reasoning = "r", regulatoryScope = "InScope", flowSupport = "Supported" }, // no effective date
            new { productDescription = "d", singleRelevantCommodity = true, activityType = "PlacingOnMarketByImport", operatorRole = "Operator", activityDateKind = "Planned", scopeCitation = "c", legalBasisVersion = "l", effectiveDate = "2026-01-01", reasoning = "r", regulatoryScope = "InScope", flowSupport = "Supported" }, // no activity date
            new { productDescription = "d", singleRelevantCommodity = true, activityType = "PlacingOnMarketByImport", operatorRole = "Operator", activityDate = "2026-06-01", activityDateKind = "Planned", effectiveDate = "2026-01-01", legalBasisVersion = "l", reasoning = "r", regulatoryScope = "InScope", flowSupport = "Supported" }, // no citation
        })
            await Problem(await PutApplicability(a, ids.CaseId, bad), HttpStatusCode.BadRequest);
        // Undefined and integer enum values are rejected.
        await Problem(await a.PutAsync($"/api/eudr-cases/{ids.CaseId}/applicability", Raw("""{"singleRelevantCommodity":true,"activityType":"Teleport","operatorRole":"Operator","activityDateKind":"Planned","regulatoryScope":"Pending","flowSupport":"Pending"}""")), HttpStatusCode.BadRequest);
        await Problem(await a.PutAsync($"/api/eudr-cases/{ids.CaseId}/applicability", Raw("""{"singleRelevantCommodity":true,"activityType":0,"operatorRole":"Operator","activityDateKind":"Planned","regulatoryScope":"Pending","flowSupport":"Pending"}""")), HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.NoContent, (await a.GetAsync($"/api/eudr-cases/{ids.CaseId}/applicability")).StatusCode);
    }

    [Fact]
    public async Task A_supplier_only_refresh_leaves_the_applicability_valid_and_a_product_swap_with_the_same_cn_and_commodity_marks_it_product_changed()
    {
        var (a, _, _, _) = await Tenants(); var (ids, _) = await CaseWithCopy(a);
        await Status(PutApplicability(a, ids.CaseId, Application()), HttpStatusCode.OK);

        await Status(a.PutAsJsonAsync($"/api/suppliers/{ids.SupplierId}", new { name = "Edited supplier", email = "edited@example.test", countryCode = "BR" }), HttpStatusCode.OK);
        Assert.Equal(2, (await Refresh(a, ids.CaseId)).GetProperty("current").GetProperty("number").GetInt32());
        var afterSupplierRefresh = await Applicability(a, ids.CaseId);
        Assert.Equal("WithinSupportedScope", afterSupplierRefresh.GetProperty("status").GetString());
        // The unchanged assessment still saves as an idempotent no-op: nothing about the product changed.
        var noop = await Json(await PutApplicability(a, ids.CaseId, Application()), HttpStatusCode.OK);
        Assert.Equal(afterSupplierRefresh.GetProperty("current").GetProperty("id").GetGuid(), noop.GetProperty("current").GetProperty("id").GetGuid());

        // Product Y with the same CN code and commodity as product X.
        var productY = await NewProduct(a, "0901", "Coffee");
        await Status(ChangeShipment(a, ids, ids.SupplierId, productY), HttpStatusCode.OK);
        Assert.Equal("WithinSupportedScope", (await Applicability(a, ids.CaseId)).GetProperty("status").GetString()); // not adopted yet: the case still holds copy X
        var adopted = await Refresh(a, ids.CaseId);
        Assert.Equal("0901", adopted.GetProperty("current").GetProperty("product").GetProperty("cnCode").GetString());
        Assert.Equal("ProductChanged", (await Applicability(a, ids.CaseId)).GetProperty("status").GetString());
        // Re-assessing against the adopted copy is a new version (the assessed product id differs) and clears the flag.
        var reassessed = await Json(await PutApplicability(a, ids.CaseId, Application()), HttpStatusCode.OK);
        Assert.Equal("WithinSupportedScope", reassessed.GetProperty("status").GetString()); Assert.Equal(2, reassessed.GetProperty("current").GetProperty("number").GetInt32());
        Assert.Equal(productY, reassessed.GetProperty("current").GetProperty("assessedProductId").GetGuid());
    }

    [Fact]
    public async Task A_changed_cn_code_adopted_by_refresh_marks_the_applicability_product_changed()
    {
        var (a, _, _, _) = await Tenants(); var (ids, _) = await CaseWithCopy(a);
        await Status(PutApplicability(a, ids.CaseId, Application()), HttpStatusCode.OK);
        await Status(a.PutAsJsonAsync($"/api/products/{ids.ProductId}", new { name = "Same product", cnCode = "0902", commodityType = "Coffee" }), HttpStatusCode.OK);
        Assert.Equal("WithinSupportedScope", (await Applicability(a, ids.CaseId)).GetProperty("status").GetString()); // a catalogue edit alone never changes the status
        await Refresh(a, ids.CaseId);
        Assert.Equal("ProductChanged", (await Applicability(a, ids.CaseId)).GetProperty("status").GetString());
    }

    // ---- recipients ---------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Recipients_are_unresolved_until_a_statement_is_saved_and_the_tri_state_is_enforced()
    {
        var (a, _, b, _) = await Tenants(); var ids = await NewCase(a);
        Assert.Equal(HttpStatusCode.NoContent, (await a.GetAsync($"/api/eudr-cases/{ids.CaseId}/recipients")).StatusCode); // unresolved is not NotApplicable
        Assert.Empty((await Json(await a.GetAsync($"/api/eudr-cases/{ids.CaseId}/recipients-history"))).EnumerateArray());
        await Problem(await b.GetAsync($"/api/eudr-cases/{ids.CaseId}/recipients"), HttpStatusCode.NotFound);
        await Problem(await b.GetAsync($"/api/eudr-cases/{ids.CaseId}/recipients-history"), HttpStatusCode.NotFound);
        await Problem(await b.PutAsJsonAsync($"/api/eudr-cases/{ids.CaseId}/recipients", Identified(Recipient())), HttpStatusCode.NotFound);

        var bad = new object[]
        {
            Identified(), new { resolution = "RecipientsIdentified" }, Identified([.. Enumerable.Range(0, 51).Select(i => Recipient("R" + i))]),
            Identified(Recipient(name: "")), Identified(Recipient(address: " ")), Identified(Recipient(email: "")), Identified(Recipient(email: "nope")),
            new { resolution = "RecipientsIdentified", notApplicableBasis = "basis", recipients = new[] { Recipient() } },
            new { resolution = "NotApplicable", recipients = Array.Empty<object>() }, new { resolution = "NotApplicable", notApplicableBasis = "  " },
            new { resolution = "NotApplicable", notApplicableBasis = "basis", recipients = new[] { Recipient() } },
        };
        foreach (var body in bad) await Problem(await a.PutAsJsonAsync($"/api/eudr-cases/{ids.CaseId}/recipients", body), HttpStatusCode.BadRequest);
        await Problem(await a.PutAsync($"/api/eudr-cases/{ids.CaseId}/recipients", Raw("""{"resolution":"Maybe","recipients":[]}""")), HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.NoContent, (await a.GetAsync($"/api/eudr-cases/{ids.CaseId}/recipients")).StatusCode); // failures never create a statement
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await a.DeleteAsync($"/api/eudr-cases/{ids.CaseId}/recipients")).StatusCode);

        var notApplicable = await Json(await a.PutAsJsonAsync($"/api/eudr-cases/{ids.CaseId}/recipients", new { resolution = "NotApplicable", notApplicableBasis = " Goods stay with the operator ", recipients = Array.Empty<object>() }), HttpStatusCode.OK);
        Assert.Equal("NotApplicable", notApplicable.GetProperty("resolution").GetString()); Assert.Equal("Goods stay with the operator", notApplicable.GetProperty("notApplicableBasis").GetString());
        Assert.Empty(notApplicable.GetProperty("recipients").EnumerateArray()); AssertNoOrganizationId(notApplicable);
    }

    [Fact]
    public async Task Recipients_are_versioned_idempotent_order_independent_and_deterministically_ordered()
    {
        var (a, orgA, _, _) = await Tenants(); var ids = await NewCase(a);
        var first = await Json(await a.PutAsJsonAsync($"/api/eudr-cases/{ids.CaseId}/recipients", Identified(Recipient("Zeta", "Z 1", "z@example.test"), Recipient("Alpha", "A 1", "a@example.test"))), HttpStatusCode.OK);
        Assert.Equal(1, first.GetProperty("number").GetInt32());
        Assert.Equal(["Alpha", "Zeta"], first.GetProperty("recipients").EnumerateArray().Select(x => x.GetProperty("name").GetString()!).ToArray());

        // Same recipients in another order with extra whitespace: a 200 no-op.
        var same = await Json(await a.PutAsJsonAsync($"/api/eudr-cases/{ids.CaseId}/recipients", Identified(Recipient(" Alpha ", "A 1", "a@example.test"), Recipient("Zeta", "Z 1", " z@example.test"))), HttpStatusCode.OK);
        Assert.Equal(first.GetProperty("id").GetGuid(), same.GetProperty("id").GetGuid());
        Assert.Single(await Audit(orgA, ids.CaseId, "CaseRecipientsSaved"));

        var second = await Json(await a.PutAsJsonAsync($"/api/eudr-cases/{ids.CaseId}/recipients", Identified(Recipient("Alpha", "A 1", "a@example.test"))), HttpStatusCode.OK);
        Assert.Equal(2, second.GetProperty("number").GetInt32());
        Assert.Equal(second.GetProperty("id").GetGuid(), (await Json(await a.GetAsync($"/api/eudr-cases/{ids.CaseId}/recipients"), HttpStatusCode.OK)).GetProperty("id").GetGuid());
        var history = (await Json(await a.GetAsync($"/api/eudr-cases/{ids.CaseId}/recipients-history"), HttpStatusCode.OK)).EnumerateArray().ToList();
        Assert.Equal([2, 1], history.Select(x => x.GetProperty("number").GetInt32()).ToArray());
        Assert.Equal(1, history[0].GetProperty("recipients").GetArrayLength()); Assert.Equal(2, history[1].GetProperty("recipients").GetArrayLength()); // history keeps the superseded recipients
        Assert.Equal(JsonValueKind.String, history[1].GetProperty("supersededAtUtc").ValueKind);

        // Resolving the other way creates a new version too; the audit text carries no recipient data.
        await Json(await a.PutAsJsonAsync($"/api/eudr-cases/{ids.CaseId}/recipients", new { resolution = "NotApplicable", notApplicableBasis = "Basis", recipients = Array.Empty<object>() }), HttpStatusCode.OK);
        var events = await Audit(orgA, ids.CaseId, "CaseRecipientsSaved");
        Assert.Equal(3, events.Count); Assert.All(events, e => { Assert.DoesNotContain("example.test", e.Description); Assert.DoesNotContain("Alpha", e.Description); });
    }

    // ---- evidence links -----------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Evidence_links_are_created_listed_withdrawn_idempotently_and_never_physically_deleted()
    {
        var (a, orgA, _, _) = await Tenants(); var ids = await NewCase(a);
        var document = await NewDocument(a, ids.CaseId);
        var before = await Json(await a.GetAsync($"/api/eudr-cases/{ids.CaseId}"));
        Assert.Empty((await Links(a, ids.CaseId)).EnumerateArray());

        var created = await a.PostAsJsonAsync($"/api/eudr-cases/{ids.CaseId}/evidence-links", new { caseDocumentId = document, purpose = "Legality", note = " Certificate of origin " });
        var link = await Json(created, HttpStatusCode.Created);
        var linkId = link.GetProperty("id").GetGuid();
        Assert.Null(created.Headers.Location); // no GET-by-id route exists, so no Location is emitted
        Assert.Equal(JsonValueKind.Null, link.GetProperty("withdrawnAtUtc").ValueKind); Assert.Equal(JsonValueKind.Null, link.GetProperty("withdrawnByUserId").ValueKind);
        Assert.Equal("Legality", link.GetProperty("purpose").GetString()); Assert.Equal("Certificate of origin", link.GetProperty("note").GetString());
        Assert.Equal("evidence.txt", link.GetProperty("documentFileName").GetString()); AssertNoOrganizationId(link);

        // The same document may serve several purposes; the same purpose twice is a conflict.
        Assert.Equal(HttpStatusCode.Conflict, (await PostLink(a, ids.CaseId, document, "Legality")).StatusCode);
        await Json(await PostLink(a, ids.CaseId, document, "DeforestationFree"), HttpStatusCode.Created);
        var listed = (await Links(a, ids.CaseId)).EnumerateArray().ToList();
        Assert.Equal(["Legality", "DeforestationFree"], listed.Select(x => x.GetProperty("purpose").GetString()!).ToArray());
        Assert.All(listed, x => Assert.Equal(JsonValueKind.Null, x.GetProperty("withdrawnAtUtc").ValueKind)); // both are current

        // Linking never changes the requirements or the case status.
        var after = await Json(await a.GetAsync($"/api/eudr-cases/{ids.CaseId}"));
        Assert.Equal(before.GetProperty("status").GetString(), after.GetProperty("status").GetString());
        Assert.Equal(before.GetProperty("requirements").GetRawText(), after.GetProperty("requirements").GetRawText());

        await Status(a.DeleteAsync($"/api/eudr-cases/{ids.CaseId}/evidence-links/{linkId}"), HttpStatusCode.NoContent);
        await Status(a.DeleteAsync($"/api/eudr-cases/{ids.CaseId}/evidence-links/{linkId}"), HttpStatusCode.NoContent); // idempotent
        // The list keeps the withdrawn link (history is retained): same id, now flagged as withdrawn with its actor; the other link is still current.
        var afterWithdrawal = (await Links(a, ids.CaseId)).EnumerateArray().ToList();
        Assert.Equal(["Legality", "DeforestationFree"], afterWithdrawal.Select(x => x.GetProperty("purpose").GetString()!).ToArray());
        Assert.Equal(linkId, afterWithdrawal[0].GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.String, afterWithdrawal[0].GetProperty("withdrawnAtUtc").ValueKind); Assert.Equal(JsonValueKind.String, afterWithdrawal[0].GetProperty("withdrawnByUserId").ValueKind);
        Assert.Equal(JsonValueKind.Null, afterWithdrawal[0].GetProperty("documentWithdrawnAtUtc").ValueKind); // the document itself was never withdrawn: separate fact
        Assert.Equal(JsonValueKind.Null, afterWithdrawal[1].GetProperty("withdrawnAtUtc").ValueKind);
        Assert.Single(await Audit(orgA, ids.CaseId, "CaseEvidenceLinkWithdrawn"));
        {
            // The row is retained with the withdrawal metadata.
            await using var scope = factory.Services.CreateAsyncScope(); await using var db = TenantDb(scope, orgA);
            var row = await db.CaseEvidenceLinks.AsNoTracking().SingleAsync(x => x.Id == linkId);
            Assert.NotNull(row.WithdrawnAtUtc); Assert.NotNull(row.WithdrawnByUserId);
        }
        // The slot is free again after a withdrawal.
        await Json(await PostLink(a, ids.CaseId, document, "Legality"), HttpStatusCode.Created);
        var finalLinks = (await Links(a, ids.CaseId)).EnumerateArray().ToList();
        Assert.Equal(3, finalLinks.Count); Assert.Equal(2, finalLinks.Count(x => x.GetProperty("withdrawnAtUtc").ValueKind == JsonValueKind.Null)); // the withdrawn row is still there next to its re-created successor
        Assert.Equal(3, (await Audit(orgA, ids.CaseId, "CaseEvidenceLinked")).Count); // three links created in total, one audit event each
    }

    [Fact]
    public async Task Evidence_link_documents_must_be_current_documents_of_the_same_case()
    {
        var (a, _, b, _) = await Tenants();
        var ids = await NewCase(a); var otherCase = await NewCase(a); var foreignCase = await NewCase(b);
        var own = await NewDocument(a, ids.CaseId); var otherCaseDocument = await NewDocument(a, otherCase.CaseId); var foreignDocument = await NewDocument(b, foreignCase.CaseId);
        // Other-case, foreign and unknown documents are indistinguishable (non-disclosing 400).
        var messages = new List<string>();
        foreach (var documentId in new[] { otherCaseDocument, foreignDocument, Guid.NewGuid() })
            messages.Add((await Problem(await PostLink(a, ids.CaseId, documentId), HttpStatusCode.BadRequest)).GetProperty("detail").GetString()!);
        Assert.Single(messages.Distinct());
        // Validation errors are 400 as well.
        await Problem(await PostLink(a, ids.CaseId, Guid.Empty), HttpStatusCode.BadRequest);
        await Problem(await a.PostAsync($"/api/eudr-cases/{ids.CaseId}/evidence-links", Raw($$"""{"caseDocumentId":"{{own}}","purpose":"Everything"}""")), HttpStatusCode.BadRequest);
        await Problem(await PostLink(a, ids.CaseId, own, note: new string('x', 1001)), HttpStatusCode.BadRequest);
        Assert.Empty((await Links(a, ids.CaseId)).EnumerateArray());

        // A withdrawn document cannot be linked; a link to a document withdrawn later stays listed with the document's withdrawal exposed.
        var withdrawn = await NewDocument(a, ids.CaseId, "to be withdrawn");
        await Status(a.DeleteAsync($"/api/eudr-cases/{ids.CaseId}/documents/{withdrawn}"), HttpStatusCode.NoContent);
        await Problem(await PostLink(a, ids.CaseId, withdrawn), HttpStatusCode.Conflict);
        await Json(await PostLink(a, ids.CaseId, own, "ProductionSource"), HttpStatusCode.Created);
        await Status(a.DeleteAsync($"/api/eudr-cases/{ids.CaseId}/documents/{own}"), HttpStatusCode.NoContent);
        var link = Assert.Single((await Links(a, ids.CaseId)).EnumerateArray());
        Assert.Equal(JsonValueKind.String, link.GetProperty("documentWithdrawnAtUtc").ValueKind); Assert.Equal(JsonValueKind.Null, link.GetProperty("withdrawnAtUtc").ValueKind); // the document withdrawal does not withdraw the link
    }

    [Fact]
    public async Task Evidence_link_withdrawal_is_scoped_to_the_route_case_and_organization()
    {
        var (a, _, b, _) = await Tenants();
        var ids = await NewCase(a); var other = await NewCase(a);
        var link = (await Json(await PostLink(a, ids.CaseId, await NewDocument(a, ids.CaseId)), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await Problem(await a.DeleteAsync($"/api/eudr-cases/{other.CaseId}/evidence-links/{link}"), HttpStatusCode.NotFound); // a link of another case
        await Problem(await a.DeleteAsync($"/api/eudr-cases/{ids.CaseId}/evidence-links/{Guid.NewGuid()}"), HttpStatusCode.NotFound);
        await Problem(await b.DeleteAsync($"/api/eudr-cases/{ids.CaseId}/evidence-links/{link}"), HttpStatusCode.NotFound);
        await Problem(await a.DeleteAsync($"/api/eudr-cases/{Guid.NewGuid()}/evidence-links/{link}"), HttpStatusCode.NotFound);
        await Problem(await b.GetAsync($"/api/eudr-cases/{ids.CaseId}/evidence-links"), HttpStatusCode.NotFound);
        await Problem(await PostLink(b, ids.CaseId, Guid.NewGuid()), HttpStatusCode.NotFound);
        Assert.Single((await Links(a, ids.CaseId)).EnumerateArray());
    }

    // ---- terminal cases -----------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Archived_cases_reject_every_working_fact_write_with_409_before_the_body_is_examined_but_stay_readable()
    {
        var (a, _, _, _) = await Tenants(); var (ids, _) = await CaseWithCopy(a);
        await Status(PutApplicability(a, ids.CaseId, Application()), HttpStatusCode.OK);
        var recipients = await a.PutAsJsonAsync($"/api/eudr-cases/{ids.CaseId}/recipients", Identified(Recipient())); Assert.Equal(HttpStatusCode.OK, recipients.StatusCode);
        var document = await NewDocument(a, ids.CaseId); var link = (await Json(await PostLink(a, ids.CaseId, document), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await Status(a.DeleteAsync($"/api/shipments/{ids.ShipmentId}"), HttpStatusCode.NoContent);
        Assert.Equal("Archived", (await Json(await a.GetAsync($"/api/eudr-cases/{ids.CaseId}"))).GetProperty("status").GetString());

        await Problem(await a.PostAsync($"/api/eudr-cases/{ids.CaseId}/master-data/refresh", null), HttpStatusCode.Conflict);
        await Problem(await PutApplicability(a, ids.CaseId, Application(reasoning: "changed")), HttpStatusCode.Conflict);
        await Problem(await PutApplicability(a, ids.CaseId, Application(reasoning: "")), HttpStatusCode.Conflict); // 409 wins over the invalid body (409 before 400)
        await Problem(await a.PutAsJsonAsync($"/api/eudr-cases/{ids.CaseId}/recipients", Identified(Recipient("Another"))), HttpStatusCode.Conflict);
        await Problem(await a.PutAsJsonAsync($"/api/eudr-cases/{ids.CaseId}/recipients", Identified()), HttpStatusCode.Conflict);
        await Problem(await PostLink(a, ids.CaseId, document, "DeforestationFree"), HttpStatusCode.Conflict);
        await Problem(await a.DeleteAsync($"/api/eudr-cases/{ids.CaseId}/evidence-links/{link}"), HttpStatusCode.Conflict);

        Assert.Equal(1, (await MasterData(a, ids.CaseId)).GetProperty("current").GetProperty("number").GetInt32());
        Assert.Equal("WithinSupportedScope", (await Applicability(a, ids.CaseId)).GetProperty("status").GetString());
        Assert.Single((await Links(a, ids.CaseId)).EnumerateArray());
        Assert.Equal(1, (await Json(await a.GetAsync($"/api/eudr-cases/{ids.CaseId}/recipients"), HttpStatusCode.OK)).GetProperty("number").GetInt32());
    }

    [Fact]
    public async Task Route_case_is_resolved_before_the_body_is_validated_for_every_write()
    {
        var (a, _, b, _) = await Tenants(); var ids = await NewCase(a);
        var unknown = Guid.NewGuid();
        await Problem(await PutApplicability(a, unknown, Application(reasoning: "")), HttpStatusCode.NotFound);
        await Problem(await PutApplicability(b, ids.CaseId, Application(reasoning: "")), HttpStatusCode.NotFound);
        await Problem(await a.PutAsJsonAsync($"/api/eudr-cases/{unknown}/recipients", Identified()), HttpStatusCode.NotFound);
        await Problem(await b.PutAsJsonAsync($"/api/eudr-cases/{ids.CaseId}/recipients", Identified()), HttpStatusCode.NotFound);
        await Problem(await PostLink(a, unknown, Guid.Empty), HttpStatusCode.NotFound);
        await Problem(await PostLink(b, ids.CaseId, Guid.Empty), HttpStatusCode.NotFound);
    }

    // ---- contract -----------------------------------------------------------------------------------------------------------------------
    [Fact]
    public async Task New_responses_use_string_enums_iso_dates_and_never_expose_the_organization()
    {
        var (a, _, _, _) = await Tenants(); var (ids, copy) = await CaseWithCopy(a);
        var applicability = await Json(await PutApplicability(a, ids.CaseId, Application()), HttpStatusCode.OK);
        var recipients = await Json(await a.PutAsJsonAsync($"/api/eudr-cases/{ids.CaseId}/recipients", Identified(Recipient())), HttpStatusCode.OK);
        var link = await Json(await PostLink(a, ids.CaseId, await NewDocument(a, ids.CaseId)), HttpStatusCode.Created);
        foreach (var json in new[] { copy, applicability, recipients, link, await MasterData(a, ids.CaseId), await MasterDataHistory(a, ids.CaseId), await ApplicabilityHistory(a, ids.CaseId), await Links(a, ids.CaseId),
                     await Json(await a.GetAsync($"/api/eudr-cases/{ids.CaseId}/recipients-history"), HttpStatusCode.OK) })
            AssertNoOrganizationId(json);
        Assert.Equal(JsonValueKind.String, copy.GetProperty("current").GetProperty("product").GetProperty("commodity").ValueKind);
        Assert.Equal(JsonValueKind.True, copy.GetProperty("shipmentMatch").ValueKind);
        var v = applicability.GetProperty("current");
        Assert.Equal(JsonValueKind.String, applicability.GetProperty("status").ValueKind);
        foreach (var name in new[] { "activityType", "operatorRole", "activityDateKind", "regulatoryScope", "flowSupport", "assessedCommodity" }) Assert.Equal(JsonValueKind.String, v.GetProperty(name).ValueKind);
        Assert.Equal("2026-01-01", v.GetProperty("effectiveDate").GetString());
        Assert.Equal(JsonValueKind.True, v.GetProperty("singleRelevantCommodity").ValueKind);
        Assert.Equal("RecipientsIdentified", recipients.GetProperty("resolution").GetString());
        Assert.Equal("Legality", link.GetProperty("purpose").GetString());
    }

    [Fact]
    public async Task New_routes_are_isolated_between_tenants_and_anonymous_callers_are_rejected()
    {
        var (a, _, b, _) = await Tenants(); var (ids, _) = await CaseWithCopy(a);
        await Status(PutApplicability(a, ids.CaseId, Application()), HttpStatusCode.OK);
        foreach (var path in new[] { "master-data", "master-data-history", "applicability", "applicability-history", "recipients", "recipients-history", "evidence-links" })
            await Problem(await b.GetAsync($"/api/eudr-cases/{ids.CaseId}/{path}"), HttpStatusCode.NotFound);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/eudr-cases/{ids.CaseId}/master-data")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/operator-profile")).StatusCode);
    }
}
