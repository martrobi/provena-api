using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;
namespace Provena.Api.IntegrationTests;
// Pins externally visible HTTP behaviour (status codes, headers, binding edge cases, error envelope).
// Auth endpoints are rate limited per process (10 / 5 minutes), so the class registers only two tenants.
[Collection("Api")]
public sealed class HttpContractTests(ApiFactory factory)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (string A, string B)? tokens;
    private async Task<(HttpClient A, HttpClient B)> Tenants()
    {
        await Gate.WaitAsync();
        try { tokens ??= (await Register("contract-a"), await Register("contract-b")); } finally { Gate.Release(); }
        return (Client(tokens.Value.A), Client(tokens.Value.B));
    }
    private HttpClient Client(string token)
    { var c = factory.CreateClient(); c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); return c; }
    private async Task<string> Register(string suffix)
    {
        using var client = factory.CreateClient();
        var json = await Json(await client.PostAsJsonAsync("/api/auth/register", new { email = $"{suffix}-{Guid.NewGuid():N}@example.test", password = "Test-Only-Password-928!", firstName = "Test", lastName = "User", organizationName = "Org " + suffix }));
        return json.GetProperty("accessToken").GetString()!;
    }
    private static async Task<JsonElement> Json(HttpResponseMessage response)
    { var body = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {body}"); return JsonDocument.Parse(body).RootElement.Clone(); }
    private static async Task<JsonElement> Problem(HttpResponseMessage response, HttpStatusCode status)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"Expected {(int)status}, got {(int)response.StatusCode}: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal((int)status, json.GetProperty("status").GetInt32());
        Assert.True(json.TryGetProperty("traceId", out _)); Assert.False(json.TryGetProperty("instance", out _));
        return json;
    }
    private static async Task<Guid> Case(HttpClient client)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var s = await Json(await client.PostAsJsonAsync("/api/suppliers", new { name = "Supplier " + tag }));
        var p = await Json(await client.PostAsJsonAsync("/api/products", new { name = "Product " + tag, cnCode = "0901", commodityType = "Coffee" }));
        var shipment = await Json(await client.PostAsJsonAsync("/api/shipments", new { referenceNumber = "REF-" + tag, supplierId = s.GetProperty("id").GetGuid(), productId = p.GetProperty("id").GetGuid(), quantity = 10, unit = "Kg" }));
        return shipment.GetProperty("eudrCaseId").GetGuid();
    }
    private static MultipartFormDataContent Upload(string filename, string type, string text, string? category = null)
    {
        var body = new MultipartFormDataContent(); var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text)); file.Headers.ContentType = new MediaTypeHeaderValue(type);
        body.Add(file, "file", filename); if (category != null) body.Add(new StringContent(category), "category"); return body;
    }
    private static StringContent RawJson(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Catalog_create_returns_201_with_absolute_location_and_shipment_create_omits_location()
    {
        var (a, _) = await Tenants();
        foreach (var (path, body) in new (string, object)[] { ("/api/suppliers", new { name = "Located supplier" }), ("/api/products", new { name = "Located product", cnCode = "1801", commodityType = "Cocoa" }) })
        {
            var response = await a.PostAsJsonAsync(path, body); var json = await Json(response);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.NotNull(response.Headers.Location); Assert.True(response.Headers.Location!.IsAbsoluteUri, response.Headers.Location.ToString());
            Assert.Equal($"{path}/{json.GetProperty("id").GetGuid()}", response.Headers.Location.AbsolutePath, ignoreCase: true);
            Assert.Equal(HttpStatusCode.OK, (await a.GetAsync(response.Headers.Location)).StatusCode);
        }
        var s = await Json(await a.PostAsJsonAsync("/api/suppliers", new { name = "Shipment supplier" }));
        var p = await Json(await a.PostAsJsonAsync("/api/products", new { name = "Shipment product", cnCode = "0901", commodityType = "Coffee" }));
        var created = await a.PostAsJsonAsync("/api/shipments", new { referenceNumber = "LOC-1", supplierId = s.GetProperty("id").GetGuid(), productId = p.GetProperty("id").GetGuid(), quantity = 5, unit = "Kg" });
        var shipment = await Json(created);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); Assert.Null(created.Headers.Location);
        Assert.True(shipment.TryGetProperty("shipment", out _)); Assert.True(shipment.TryGetProperty("eudrCaseId", out _));
    }

    [Fact]
    public async Task Null_geolocation_returns_204_with_an_empty_body()
    {
        var (a, _) = await Tenants(); var caseId = await Case(a);
        var geo = await a.GetAsync($"/api/eudr-cases/{caseId}/geolocation");
        Assert.Equal(HttpStatusCode.NoContent, geo.StatusCode); Assert.Empty(await geo.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Document_upload_binds_category_and_rejects_non_multipart()
    {
        var (a, _) = await Tenants(); var caseId = await Case(a); var path = $"/api/eudr-cases/{caseId}/documents";
        async Task<string> Category(string? value)
        { using var body = Upload("note.txt", "text/plain", "evidence", value); return (await Json(await a.PostAsync(path, body))).GetProperty("category").GetString()!; }
        Assert.Equal("SupportingDocument", await Category(null));
        Assert.Equal("Certificate", await Category("Certificate"));
        Assert.Equal("Certificate", await Category("certificate"));
        Assert.Equal("Invoice", await Category("3"));
        using (var undefined = Upload("note.txt", "text/plain", "evidence", "99")) await Problem(await a.PostAsync(path, undefined), HttpStatusCode.BadRequest);
        using (var unknown = Upload("note.txt", "text/plain", "evidence", "Bogus")) Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsync(path, unknown)).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await a.PostAsync(path, RawJson("{}"))).StatusCode);
        using (var missingFile = new MultipartFormDataContent { { new StringContent("Certificate"), "category" } }) Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsync(path, missingFile)).StatusCode);
        Assert.Equal(4, (await Json(await a.GetAsync(path))).GetArrayLength());
    }

    [Fact]
    public async Task Binding_failures_return_400_and_unknown_or_non_guid_routes_return_404()
    {
        var (a, _) = await Tenants();
        // Framework binding failures use the ExceptionHandler envelope (owner decision for the Minimal API migration).
        var malformed = await Problem(await a.PostAsync("/api/suppliers", RawJson("{\"name\":")), HttpStatusCode.BadRequest);
        Assert.Equal("Validation failed", malformed.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsync("/api/suppliers", RawJson(""))).StatusCode);
        // No body and no Content-Type where a JSON body is required: 415, as with MVC.
        await Problem(await a.PostAsync("/api/suppliers", null), HttpStatusCode.UnsupportedMediaType);
        await Problem(await a.PutAsync($"/api/suppliers/{Guid.NewGuid()}", null), HttpStatusCode.UnsupportedMediaType);
        using (var anonymous = factory.CreateClient())
            await Problem(await anonymous.PutAsync("/public/supplier-requests/not-a-valid-token/details", null), HttpStatusCode.UnsupportedMediaType);
        await Problem(await a.PostAsync("/api/suppliers", new StringContent("{}", Encoding.UTF8, "text/plain")), HttpStatusCode.UnsupportedMediaType);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsync("/api/suppliers", RawJson("{}"))).StatusCode);
        // Integer enum values are disallowed for JSON conversion.
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsync("/api/products", RawJson("""{"name":"N","cnCode":"0901","commodityType":1}"""))).StatusCode);
        // JSON depth limit is 32 (MVC default, preserved explicitly for Minimal APIs).
        var deep = new string('[', 40) + new string(']', 40);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsync("/api/suppliers", RawJson($$"""{"name":"Deep","extra":{{deep}}}"""))).StatusCode);
        foreach (var query in new[] { "status=Bogus", "status=99", "page=abc", "supplierId=not-a-guid", "page=0", "pageSize=101" })
            await Problem(await a.GetAsync($"/api/eudr-cases?{query}"), HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/eudr-cases?status=draft&page=1&pageSize=5")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync("/api/eudr-cases/not-a-guid")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync("/api/does-not-exist")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await a.PatchAsync("/api/suppliers", RawJson("{}"))).StatusCode);
    }

    private static async Task<(List<JsonElement> Items, JsonElement Root)> Page(HttpClient client, string url)
    { var root = await Json(await client.GetAsync(url)); return (root.GetProperty("items").EnumerateArray().ToList(), root); }
    private static List<Guid> Ids(IEnumerable<JsonElement> items) => items.Select(x => x.GetProperty("id").GetGuid()).ToList();
    private static async Task<(Guid Supplier, Guid Product, Guid Shipment)> NewShipment(HttpClient client, string tag)
    {
        var s = await Json(await client.PostAsJsonAsync("/api/suppliers", new { name = "Supplier " + tag }));
        var p = await Json(await client.PostAsJsonAsync("/api/products", new { name = "Product " + tag, cnCode = "0901", commodityType = "Coffee" }));
        var shipment = await Json(await client.PostAsJsonAsync("/api/shipments", new { referenceNumber = "REF-" + tag, supplierId = s.GetProperty("id").GetGuid(), productId = p.GetProperty("id").GetGuid(), quantity = 10, unit = "Kg" }));
        return (s.GetProperty("id").GetGuid(), p.GetProperty("id").GetGuid(), shipment.GetProperty("shipment").GetProperty("id").GetGuid());
    }

    // Shared paging semantics (validation, overflow, out-of-range pages, Name+Id tiebreak) are pinned once here; products and shipments only prove their own wiring and order.
    [Fact]
    public async Task Supplier_list_is_paginated_with_stable_order_and_validated_paging()
    {
        var (a, _) = await Tenants(); var tag = Guid.NewGuid().ToString("N");
        for (var i = 0; i < 5; i++) await Json(await a.PostAsJsonAsync("/api/suppliers", new { name = "Dup " + tag }));
        var first = await Page(a, $"/api/suppliers?search={tag}&pageSize=2");
        Assert.Equal(1, first.Root.GetProperty("page").GetInt32()); Assert.Equal(2, first.Root.GetProperty("pageSize").GetInt32()); Assert.Equal(5, first.Root.GetProperty("totalCount").GetInt32());
        var second = await Page(a, $"/api/suppliers?search={tag}&page=2&pageSize=2"); var third = await Page(a, $"/api/suppliers?search={tag}&page=3&pageSize=2");
        Assert.Equal([2, 2, 1], [first.Items.Count, second.Items.Count, third.Items.Count]);
        var all = Ids((await Page(a, $"/api/suppliers?search={tag}&pageSize=100")).Items);
        Assert.Equal(5, all.Distinct().Count()); Assert.Equal(all, Ids(first.Items).Concat(Ids(second.Items)).Concat(Ids(third.Items)).ToList());
        var defaults = (await Page(a, $"/api/suppliers?search={tag}")).Root;
        Assert.Equal(1, defaults.GetProperty("page").GetInt32()); Assert.Equal(25, defaults.GetProperty("pageSize").GetInt32());
        var beyond = (await Page(a, $"/api/suppliers?search={tag}&page=4&pageSize=2")).Root;
        Assert.Empty(beyond.GetProperty("items").EnumerateArray()); Assert.Equal(5, beyond.GetProperty("totalCount").GetInt32());
        foreach (var query in new[] { "page=0", "pageSize=0", "pageSize=101", "page=abc", "pageSize=abc", "page=2147483647&pageSize=100" })
            await Problem(await a.GetAsync($"/api/suppliers?{query}"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Product_list_is_paginated_by_name_then_id_and_search_still_matches_cn_code()
    {
        var (a, _) = await Tenants(); var tag = Guid.NewGuid().ToString("N");
        for (var i = 0; i < 3; i++) await Json(await a.PostAsJsonAsync("/api/products", new { name = "Dup " + tag, cnCode = "0901", commodityType = "Coffee" }));
        var unique = await Json(await a.PostAsJsonAsync("/api/products", new { name = "Cn lookup " + tag, cnCode = "8765432109", commodityType = "Coffee" }));
        var first = await Page(a, $"/api/products?search=Dup {tag}&pageSize=2"); var second = await Page(a, $"/api/products?search=Dup {tag}&page=2&pageSize=2");
        Assert.Equal(3, first.Root.GetProperty("totalCount").GetInt32()); Assert.Equal([2, 1], [first.Items.Count, second.Items.Count]);
        Assert.Equal(Ids((await Page(a, $"/api/products?search=Dup {tag}&pageSize=100")).Items), Ids(first.Items).Concat(Ids(second.Items)).ToList());
        Assert.Contains(unique.GetProperty("id").GetGuid(), Ids((await Page(a, "/api/products?search=8765432109")).Items));
        await Problem(await a.GetAsync("/api/products?pageSize=101"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Product_list_filters_by_is_active_combined_with_search_and_paging()
    {
        var (a, b) = await Tenants(); var tag = Guid.NewGuid().ToString("N");
        foreach (var active in new[] { true, true, true, false, false })
            await Json(await a.PostAsJsonAsync("/api/products", new { name = "Filter " + tag, cnCode = "0901", commodityType = "Coffee", isActive = active }));
        var both = await Page(a, $"/api/products?search=Filter {tag}&pageSize=100");
        Assert.Equal(5, both.Root.GetProperty("totalCount").GetInt32());
        var active3 = await Page(a, $"/api/products?search=Filter {tag}&isActive=true&pageSize=100");
        Assert.Equal(3, active3.Root.GetProperty("totalCount").GetInt32()); Assert.All(active3.Items, x => Assert.True(x.GetProperty("isActive").GetBoolean()));
        var inactive2 = await Page(a, $"/api/products?search=Filter {tag}&isActive=false&pageSize=100");
        Assert.Equal(2, inactive2.Root.GetProperty("totalCount").GetInt32()); Assert.All(inactive2.Items, x => Assert.False(x.GetProperty("isActive").GetBoolean()));
        var first = await Page(a, $"/api/products?search=Filter {tag}&isActive=true&pageSize=2"); var second = await Page(a, $"/api/products?search=Filter {tag}&isActive=true&page=2&pageSize=2");
        Assert.Equal(3, first.Root.GetProperty("totalCount").GetInt32()); Assert.Equal([2, 1], [first.Items.Count, second.Items.Count]);
        Assert.Equal(Ids(active3.Items), Ids(first.Items).Concat(Ids(second.Items)).ToList());
        Assert.Equal(0, (await Page(b, $"/api/products?search=Filter {tag}&isActive=true")).Root.GetProperty("totalCount").GetInt32());
        await Problem(await a.GetAsync("/api/products?isActive=abc"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Shipment_list_is_paginated_newest_first_with_stable_order()
    {
        var (a, _) = await Tenants(); var created = new List<Guid>();
        for (var i = 0; i < 3; i++) created.Add((await NewShipment(a, Guid.NewGuid().ToString("N")[..8])).Shipment);
        var all = await Page(a, "/api/shipments?pageSize=100"); var allIds = Ids(all.Items);
        Assert.Equal(1, all.Root.GetProperty("page").GetInt32()); Assert.Equal(100, all.Root.GetProperty("pageSize").GetInt32()); Assert.True(all.Root.GetProperty("totalCount").GetInt32() >= 3);
        var positions = Enumerable.Reverse(created).Select(id => allIds.IndexOf(id)).ToList(); Assert.DoesNotContain(-1, positions); Assert.Equal(positions.Order(), positions);
        var first = await Page(a, "/api/shipments?pageSize=2"); var second = await Page(a, "/api/shipments?page=2&pageSize=2");
        Assert.Equal(allIds.Take(4), Ids(first.Items).Concat(Ids(second.Items)));
        Assert.Equal(25, (await Page(a, "/api/shipments")).Root.GetProperty("pageSize").GetInt32());
        await Problem(await a.GetAsync("/api/shipments?pageSize=101"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Paginated_catalog_and_shipment_lists_do_not_include_other_tenants_rows()
    {
        var (a, b) = await Tenants(); var tag = Guid.NewGuid().ToString("N")[..12]; var ids = await NewShipment(a, tag);
        Assert.Equal(1, (await Page(a, $"/api/suppliers?search={tag}")).Root.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Page(b, $"/api/suppliers?search={tag}")).Root.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Page(b, $"/api/products?search={tag}")).Root.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Page(b, $"/api/products?search={tag}&isActive=true")).Root.GetProperty("totalCount").GetInt32());
        Assert.DoesNotContain(ids.Shipment, Ids((await Page(b, "/api/shipments?pageSize=100")).Items));
    }

    [Fact]
    public async Task Error_responses_use_problem_details_without_instance()
    {
        var (a, b) = await Tenants(); var caseId = await Case(a);
        using var anonymous = factory.CreateClient();
        var unauthorized = await anonymous.GetAsync("/api/suppliers"); Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        var validation = await Problem(await a.PostAsJsonAsync("/api/suppliers", new { name = "" }), HttpStatusCode.BadRequest);
        Assert.Equal("Validation failed", validation.GetProperty("title").GetString()); Assert.True(validation.GetProperty("errors").TryGetProperty("Name", out _));
        await Problem(await b.GetAsync($"/api/eudr-cases/{caseId}"), HttpStatusCode.NotFound);
        await Problem(await anonymous.GetAsync("/public/supplier-requests/not-a-valid-token"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Internal_routes_require_authentication_and_hide_other_tenants_files()
    {
        var (a, b) = await Tenants(); var caseId = await Case(a); using var anonymous = factory.CreateClient();
        using (var doc = Upload("declaration.txt", "text/plain", "Supplier declaration")) await Json(await a.PostAsync($"/api/eudr-cases/{caseId}/documents", doc));
        var documentId = (await Json(await a.GetAsync($"/api/eudr-cases/{caseId}/documents")))[0].GetProperty("id").GetGuid();
        using (var geoFile = Upload("plot.geojson", "application/geo+json", """{"type":"Polygon","coordinates":[[[-48,-20],[-47,-20],[-47,-19],[-48,-20]]]}""")) await Json(await a.PostAsync($"/api/eudr-cases/{caseId}/geolocation", geoFile));
        var geoId = (await Json(await a.GetAsync($"/api/eudr-cases/{caseId}/geolocation"))).GetProperty("id").GetGuid();
        var id = Guid.NewGuid();
        var requests = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, "/api/auth/me"), (HttpMethod.Get, "/api/suppliers"), (HttpMethod.Get, "/api/products"), (HttpMethod.Get, "/api/shipments"),
            (HttpMethod.Get, "/api/eudr-cases"), (HttpMethod.Get, "/api/dashboard"), (HttpMethod.Post, $"/api/supplier-requests/{id}/revoke"),
            (HttpMethod.Post, $"/api/eudr-cases/{caseId}/supplier-request"), (HttpMethod.Get, $"/api/eudr-cases/{caseId}/export"),
            (HttpMethod.Get, $"/api/eudr-cases/{caseId}/documents"), (HttpMethod.Post, $"/api/eudr-cases/{caseId}/documents"),
            (HttpMethod.Get, $"/api/eudr-cases/{caseId}/documents/{documentId}"), (HttpMethod.Delete, $"/api/eudr-cases/{caseId}/documents/{documentId}"),
            (HttpMethod.Get, $"/api/eudr-cases/{caseId}/geolocation"), (HttpMethod.Post, "/api/import/suppliers"),
            (HttpMethod.Get, $"/api/eudr-cases/{caseId}/evidence-history"), (HttpMethod.Get, $"/api/eudr-cases/{caseId}/geolocation/{geoId}/file"),
            (HttpMethod.Get, "/api/producers"), (HttpMethod.Post, "/api/producers"), (HttpMethod.Get, $"/api/producers/{id}"), (HttpMethod.Put, $"/api/producers/{id}"),
            (HttpMethod.Get, $"/api/eudr-cases/{caseId}/dossier"), (HttpMethod.Put, $"/api/eudr-cases/{caseId}/dossier/lot"), (HttpMethod.Get, $"/api/eudr-cases/{caseId}/dossier/lot-history"),
            (HttpMethod.Get, "/api/production-plots"), (HttpMethod.Post, "/api/production-plots"), (HttpMethod.Get, $"/api/production-plots/{id}"), (HttpMethod.Put, $"/api/production-plots/{id}"),
            (HttpMethod.Get, $"/api/production-plots/{id}/revisions"), (HttpMethod.Post, $"/api/production-plots/{id}/revisions"), (HttpMethod.Get, $"/api/production-plots/{id}/revisions/{id}"),
            (HttpMethod.Get, "/api/operator-profile"), (HttpMethod.Put, "/api/operator-profile"),
            (HttpMethod.Get, $"/api/eudr-cases/{caseId}/master-data"), (HttpMethod.Post, $"/api/eudr-cases/{caseId}/master-data/refresh"), (HttpMethod.Get, $"/api/eudr-cases/{caseId}/master-data-history"),
            (HttpMethod.Get, $"/api/eudr-cases/{caseId}/applicability"), (HttpMethod.Put, $"/api/eudr-cases/{caseId}/applicability"), (HttpMethod.Get, $"/api/eudr-cases/{caseId}/applicability-history"),
            (HttpMethod.Get, $"/api/eudr-cases/{caseId}/recipients"), (HttpMethod.Put, $"/api/eudr-cases/{caseId}/recipients"), (HttpMethod.Get, $"/api/eudr-cases/{caseId}/recipients-history"),
            (HttpMethod.Get, $"/api/eudr-cases/{caseId}/evidence-links"), (HttpMethod.Post, $"/api/eudr-cases/{caseId}/evidence-links"), (HttpMethod.Delete, $"/api/eudr-cases/{caseId}/evidence-links/{id}"),
            (HttpMethod.Post, $"/api/eudr-cases/{caseId}/close"), (HttpMethod.Post, $"/api/eudr-cases/{caseId}/reopen"), (HttpMethod.Post, $"/api/eudr-cases/{caseId}/archive"), (HttpMethod.Post, $"/api/eudr-cases/{caseId}/unarchive"), (HttpMethod.Get, $"/api/eudr-cases/{caseId}/lifecycle"),
            (HttpMethod.Get, $"/api/eudr-cases/{caseId}/completeness"),
            (HttpMethod.Post, $"/api/eudr-cases/{caseId}/reviews"), (HttpMethod.Get, $"/api/eudr-cases/{caseId}/reviews"), (HttpMethod.Get, $"/api/eudr-cases/{caseId}/reviews/{id}"), (HttpMethod.Get, $"/api/eudr-cases/{caseId}/reviews/{id}/export"),
        };
        foreach (var (method, path) in requests)
            Assert.True(HttpStatusCode.Unauthorized == (await anonymous.SendAsync(new HttpRequestMessage(method, path))).StatusCode, $"{method} {path}");
        foreach (var path in new[] { $"/api/eudr-cases/{caseId}/documents", $"/api/eudr-cases/{caseId}/documents/{documentId}", $"/api/eudr-cases/{caseId}/geolocation", $"/api/eudr-cases/{caseId}/evidence-history", $"/api/eudr-cases/{caseId}/geolocation/{geoId}/file" })
            await Problem(await b.GetAsync(path), HttpStatusCode.NotFound);
        await Problem(await b.DeleteAsync($"/api/eudr-cases/{caseId}/documents/{documentId}"), HttpStatusCode.NotFound);
        using (var foreign = Upload("x.txt", "text/plain", "x")) await Problem(await b.PostAsync($"/api/eudr-cases/{caseId}/documents", foreign), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task File_responses_keep_content_type_disposition_and_security_headers()
    {
        var (a, _) = await Tenants(); var caseId = await Case(a);
        using (var doc = Upload("declaration.txt", "text/plain", "Supplier declaration")) await Json(await a.PostAsync($"/api/eudr-cases/{caseId}/documents", doc));
        var documentId = (await Json(await a.GetAsync($"/api/eudr-cases/{caseId}/documents")))[0].GetProperty("id").GetGuid();
        var download = await a.GetAsync($"/api/eudr-cases/{caseId}/documents/{documentId}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode); Assert.Equal("text/plain", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType); Assert.Equal("declaration.txt", download.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal("Supplier declaration", await download.Content.ReadAsStringAsync());
        var export = await a.GetAsync($"/api/eudr-cases/{caseId}/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode); Assert.Equal("application/zip", export.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", export.Content.Headers.ContentDisposition?.DispositionType); Assert.EndsWith(".zip", export.Content.Headers.ContentDisposition?.FileNameStar);
        foreach (var response in new[] { download, export })
        {
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single()); Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    [Fact]
    public async Task Csv_imports_accept_multipart_files_for_each_kind()
    {
        var (a, _) = await Tenants(); var tag = Guid.NewGuid().ToString("N")[..8];
        async Task<JsonElement> Import(string kind, string csv)
        { using var body = Upload(kind + ".csv", "text/csv", csv); return await Json(await a.PostAsync($"/api/import/{kind}", body)); }
        var suppliers = await Import("suppliers", $"externalReference,name,email,contactPerson,countryCode\nS-{tag},Imported supplier,,,BR\n");
        var products = await Import("products", $"externalReference,name,cnCode,commodityType,description\nP-{tag},Imported product,0901,Coffee,\n");
        var shipments = await Import("shipments", $"referenceNumber,supplierExternalReference,productExternalReference,quantity,unit,originCountryCode\nI-{tag},S-{tag},P-{tag},12.5,Kg,BR\nI2-{tag},S-missing,P-{tag},1,Kg,\n");
        foreach (var result in new[] { suppliers, products }) { Assert.Equal(1, result.GetProperty("totalRows").GetInt32()); Assert.Equal(1, result.GetProperty("imported").GetInt32()); }
        Assert.Equal(2, shipments.GetProperty("totalRows").GetInt32()); Assert.Equal(1, shipments.GetProperty("imported").GetInt32()); Assert.Equal(1, shipments.GetProperty("failed").GetInt32());
        Assert.Equal(3, shipments.GetProperty("errors")[0].GetProperty("row").GetInt32());
        using (var wrongType = Upload("suppliers.txt", "text/plain", "x")) await Problem(await a.PostAsync("/api/import/suppliers", wrongType), HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await a.PostAsync("/api/import/suppliers", RawJson("{}"))).StatusCode);
    }


    [Fact]
    public async Task Dossier_json_uses_string_enums_iso_dates_numeric_decimals_and_no_organization_id()
    {
        var (a, b) = await Tenants(); var caseId = await Case(a);
        var producer = await Json(await a.PostAsJsonAsync("/api/producers", new { name = "Contract producer", countryCode = "BR" }));
        var plotBody = $$"""{"producerId":"{{producer.GetProperty("id").GetGuid()}}","name":"Contract plot","initialRevision":{"countryCode":"BR","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,1],[0,0]]]},"areaHectares":1.5} }""";
        var plot = await Json(await a.PostAsync("/api/production-plots", RawJson(plotBody)));
        var body = new
        {
            lotReference = "CONTRACT-" + Guid.NewGuid().ToString("N")[..8], quantity = 10.5m, unit = "Kg", note = "n",
            contributions = new[] { new { producerId = producer.GetProperty("id").GetGuid(), quantity = 10.5m, unit = "Kg", plots = new[] { new { productionPlotId = plot.GetProperty("id").GetGuid(), plotRevisionId = plot.GetProperty("currentRevision").GetProperty("id").GetGuid(), productionStartDate = "2024-03-01", productionEndDate = "2024-04-30" } } } }
        };
        var response = await a.PutAsJsonAsync($"/api/eudr-cases/{caseId}/dossier/lot", body); var dossier = await Json(response);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var lot = dossier.GetProperty("currentLot"); var link = lot.GetProperty("contributions")[0].GetProperty("plots")[0];
        Assert.Equal("Kg", lot.GetProperty("unit").GetString()); Assert.Equal("Kg", dossier.GetProperty("shipmentUnit").GetString());
        Assert.Equal("Reconciled", lot.GetProperty("reconciliation").GetProperty("status").GetString());
        Assert.Equal("Polygon", link.GetProperty("geometryType").GetString());
        Assert.Equal("2024-03-01", link.GetProperty("productionStartDate").GetString()); Assert.Equal("2024-04-30", link.GetProperty("productionEndDate").GetString());
        Assert.Equal(JsonValueKind.Number, lot.GetProperty("quantity").ValueKind); Assert.Equal(10.5m, lot.GetProperty("quantity").GetDecimal());
        Assert.Equal(JsonValueKind.Number, lot.GetProperty("reconciliation").GetProperty("remainingQuantity").ValueKind);
        Assert.Equal(JsonValueKind.Number, dossier.GetProperty("shipmentQuantity").ValueKind); Assert.Equal(JsonValueKind.Number, link.GetProperty("areaHectares").ValueKind);
        Assert.DoesNotContain("organizationid", dossier.GetRawText().ToLowerInvariant());
        Assert.DoesNotContain("organizationid", (await Json(await a.GetAsync($"/api/eudr-cases/{caseId}/dossier/lot-history"))).GetRawText().ToLowerInvariant());
        // Integer enum values are not accepted on input, matching the rest of the API.
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PutAsync($"/api/eudr-cases/{caseId}/dossier/lot", RawJson("""{"lotReference":"X","quantity":1,"unit":0,"contributions":[]}"""))).StatusCode);
        await Problem(await b.GetAsync($"/api/eudr-cases/{caseId}/dossier"), HttpStatusCode.NotFound);
    }
    [Fact]
    public async Task A_review_request_may_omit_expected_latest_review_id_when_no_review_exists()
    {
        var (a, _) = await Tenants(); var caseId = await Case(a); var url = $"/api/eudr-cases/{caseId}/reviews";
        var fingerprint = (await Json(await a.GetAsync($"/api/eudr-cases/{caseId}/completeness"))).GetProperty("fingerprint").GetString();
        var omitted = await a.PostAsync(url, RawJson($$"""{"decision":"ReturnedForCorrection","rationale":"No previous review existed.","expectedFingerprint":"{{fingerprint}}"}"""));
        Assert.Equal(HttpStatusCode.Created, omitted.StatusCode); // the property is optional: omitting it means "no review existed when loaded"
        // A previous review now exists: null (like omission) no longer matches, only its id does.
        var nullAgain = await a.PostAsync(url, RawJson($$"""{"decision":"ReturnedForCorrection","rationale":"r","expectedFingerprint":"{{fingerprint}}","expectedLatestReviewId":null}"""));
        Assert.Equal(HttpStatusCode.Conflict, nullAgain.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsync(url, RawJson($$"""{"decision":"ReturnedForCorrection","rationale":"r","expectedFingerprint":"{{fingerprint}}"}"""))).StatusCode);
    }

    [Fact]
    public async Task Openapi_document_lists_every_route()
    {
        using var anonymous = factory.CreateClient();
        var document = await Json(await anonymous.GetAsync("/openapi/v1.json"));
        var paths = document.GetProperty("paths").EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var path in new[]
        {
            "/api/auth/register", "/api/auth/login", "/api/auth/me", "/api/suppliers", "/api/suppliers/{id}", "/api/products", "/api/products/{id}",
            "/api/shipments", "/api/shipments/{id}", "/api/eudr-cases", "/api/eudr-cases/{id}", "/api/eudr-cases/{caseId}/requirements/{requirementId}",
            "/api/eudr-cases/{caseId}/supplier-request", "/api/eudr-cases/{caseId}/export", "/api/dashboard",
            "/api/supplier-requests/{id}/revoke", "/api/supplier-requests/{id}/regenerate", "/api/eudr-cases/{caseId}/documents",
            "/api/eudr-cases/{caseId}/documents/{documentId}", "/api/eudr-cases/{caseId}/geolocation", "/api/eudr-cases/{caseId}/evidence-history", "/api/eudr-cases/{caseId}/geolocation/{geolocationId}/file", "/api/import/suppliers", "/api/import/products",
            "/api/eudr-cases/{caseId}/dossier", "/api/eudr-cases/{caseId}/dossier/lot", "/api/eudr-cases/{caseId}/dossier/lot-history",
            "/api/producers", "/api/producers/{id}", "/api/production-plots", "/api/production-plots/{id}", "/api/production-plots/{plotId}/revisions", "/api/production-plots/{plotId}/revisions/{revisionId}",
            "/api/operator-profile", "/api/eudr-cases/{caseId}/master-data", "/api/eudr-cases/{caseId}/master-data/refresh", "/api/eudr-cases/{caseId}/master-data-history",
            "/api/eudr-cases/{caseId}/applicability", "/api/eudr-cases/{caseId}/applicability-history", "/api/eudr-cases/{caseId}/recipients", "/api/eudr-cases/{caseId}/recipients-history",
            "/api/eudr-cases/{caseId}/evidence-links", "/api/eudr-cases/{caseId}/evidence-links/{linkId}",
            "/api/eudr-cases/{caseId}/close", "/api/eudr-cases/{caseId}/reopen", "/api/eudr-cases/{caseId}/archive", "/api/eudr-cases/{caseId}/unarchive", "/api/eudr-cases/{caseId}/lifecycle",
            "/api/eudr-cases/{caseId}/completeness",
            "/api/eudr-cases/{caseId}/reviews", "/api/eudr-cases/{caseId}/reviews/{reviewId}", "/api/eudr-cases/{caseId}/reviews/{reviewId}/export",
            "/api/import/shipments", "/public/supplier-requests/{token}", "/public/supplier-requests/{token}/details", "/public/supplier-requests/{token}/documents",
            "/public/supplier-requests/{token}/geolocation", "/public/supplier-requests/{token}/complete",
        })
            Assert.Contains(path, paths);
        Assert.Equal(58, paths.Count);
        // The review POST reads its body by hand (so the Owner check precedes parsing); its request body must still be advertised.
        var reviewPost = document.GetProperty("paths").GetProperty("/api/eudr-cases/{caseId}/reviews").GetProperty("post");
        Assert.True(reviewPost.GetProperty("requestBody").GetProperty("content").TryGetProperty("application/json", out var reviewBody));
        var reviewSchema = reviewBody.GetProperty("schema"); // inline: the body is advertised by an operation transformer
        Assert.Equal(["decision", "expectedFingerprint", "expectedLatestReviewId", "rationale"], reviewSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(["decision", "expectedFingerprint", "rationale"], reviewSchema.GetProperty("required").EnumerateArray().Select(x => x.GetString()!).Order().ToArray()); // expectedLatestReviewId is optional
        Assert.Contains("null", reviewSchema.GetProperty("properties").GetProperty("expectedLatestReviewId").GetRawText()); // and nullable when supplied
        Assert.True(reviewPost.GetProperty("requestBody").GetProperty("required").GetBoolean());
        Assert.True(reviewPost.TryGetProperty("security", out _));
        var login = document.GetProperty("paths").GetProperty("/api/auth/login").GetProperty("post");
        Assert.False(login.TryGetProperty("security", out _));
        Assert.True(document.GetProperty("paths").GetProperty("/api/suppliers").GetProperty("get").TryGetProperty("security", out _));
    }
}
