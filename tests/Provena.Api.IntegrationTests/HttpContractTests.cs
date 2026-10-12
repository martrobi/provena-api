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
    public async Task Null_geolocation_returns_204_and_optional_complete_body_reaches_the_service()
    {
        var (a, _) = await Tenants(); var caseId = await Case(a);
        var geo = await a.GetAsync($"/api/eudr-cases/{caseId}/geolocation");
        Assert.Equal(HttpStatusCode.NoContent, geo.StatusCode); Assert.Empty(await geo.Content.ReadAsByteArrayAsync());
        // A Draft case cannot complete: 409 proves the request bound and reached CaseService for every optional-body form.
        await Problem(await a.PostAsync($"/api/eudr-cases/{caseId}/complete", null), HttpStatusCode.Conflict);
        await Problem(await a.PostAsync($"/api/eudr-cases/{caseId}/complete", RawJson("")), HttpStatusCode.Conflict);
        await Problem(await a.PostAsync($"/api/eudr-cases/{caseId}/complete", RawJson("{}")), HttpStatusCode.Conflict);
        await Problem(await a.PostAsync($"/api/eudr-cases/{caseId}/complete", RawJson("""{"externalDdsReference":"X-1"}""")), HttpStatusCode.Conflict);
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
        // No body and no Content-Type where a JSON body is required: 415, as with MVC. Optional bodies (complete) still accept it.
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

    [Fact]
    public async Task Error_responses_use_problem_details_without_instance()
    {
        var (a, b) = await Tenants(); var caseId = await Case(a);
        using var anonymous = factory.CreateClient();
        var unauthorized = await anonymous.GetAsync("/api/suppliers"); Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        var validation = await Problem(await a.PostAsJsonAsync("/api/suppliers", new { name = "" }), HttpStatusCode.BadRequest);
        Assert.Equal("Validation failed", validation.GetProperty("title").GetString()); Assert.True(validation.GetProperty("errors").TryGetProperty("Name", out _));
        await Problem(await b.GetAsync($"/api/eudr-cases/{caseId}"), HttpStatusCode.NotFound);
        await Problem(await a.PostAsync($"/api/eudr-cases/{caseId}/complete", null), HttpStatusCode.Conflict);
        await Problem(await anonymous.GetAsync("/public/supplier-requests/not-a-valid-token"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Internal_routes_require_authentication_and_hide_other_tenants_files()
    {
        var (a, b) = await Tenants(); var caseId = await Case(a); using var anonymous = factory.CreateClient();
        using (var doc = Upload("declaration.txt", "text/plain", "Supplier declaration")) await Json(await a.PostAsync($"/api/eudr-cases/{caseId}/documents", doc));
        var documentId = (await Json(await a.GetAsync($"/api/eudr-cases/{caseId}/documents")))[0].GetProperty("id").GetGuid();
        var id = Guid.NewGuid();
        var requests = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, "/api/auth/me"), (HttpMethod.Get, "/api/suppliers"), (HttpMethod.Get, "/api/products"), (HttpMethod.Get, "/api/shipments"),
            (HttpMethod.Get, "/api/eudr-cases"), (HttpMethod.Get, "/api/dashboard"), (HttpMethod.Post, $"/api/supplier-requests/{id}/revoke"),
            (HttpMethod.Post, $"/api/eudr-cases/{caseId}/supplier-request"), (HttpMethod.Get, $"/api/eudr-cases/{caseId}/export"),
            (HttpMethod.Get, $"/api/eudr-cases/{caseId}/documents"), (HttpMethod.Post, $"/api/eudr-cases/{caseId}/documents"),
            (HttpMethod.Get, $"/api/eudr-cases/{caseId}/documents/{documentId}"), (HttpMethod.Delete, $"/api/eudr-cases/{caseId}/documents/{documentId}"),
            (HttpMethod.Get, $"/api/eudr-cases/{caseId}/geolocation"), (HttpMethod.Post, "/api/import/suppliers"),
        };
        foreach (var (method, path) in requests)
            Assert.True(HttpStatusCode.Unauthorized == (await anonymous.SendAsync(new HttpRequestMessage(method, path))).StatusCode, $"{method} {path}");
        foreach (var path in new[] { $"/api/eudr-cases/{caseId}/documents", $"/api/eudr-cases/{caseId}/documents/{documentId}", $"/api/eudr-cases/{caseId}/geolocation" })
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
    public async Task Openapi_document_lists_every_route()
    {
        using var anonymous = factory.CreateClient();
        var document = await Json(await anonymous.GetAsync("/openapi/v1.json"));
        var paths = document.GetProperty("paths").EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var path in new[]
        {
            "/api/auth/register", "/api/auth/login", "/api/auth/me", "/api/suppliers", "/api/suppliers/{id}", "/api/products", "/api/products/{id}",
            "/api/shipments", "/api/shipments/{id}", "/api/eudr-cases", "/api/eudr-cases/{id}", "/api/eudr-cases/{caseId}/requirements/{requirementId}",
            "/api/eudr-cases/{caseId}/supplier-request", "/api/eudr-cases/{caseId}/complete", "/api/eudr-cases/{caseId}/export", "/api/dashboard",
            "/api/supplier-requests/{id}/revoke", "/api/supplier-requests/{id}/regenerate", "/api/eudr-cases/{caseId}/documents",
            "/api/eudr-cases/{caseId}/documents/{documentId}", "/api/eudr-cases/{caseId}/geolocation", "/api/import/suppliers", "/api/import/products",
            "/api/import/shipments", "/public/supplier-requests/{token}", "/public/supplier-requests/{token}/details", "/public/supplier-requests/{token}/documents",
            "/public/supplier-requests/{token}/geolocation", "/public/supplier-requests/{token}/complete",
        })
            Assert.Contains(path, paths);
        Assert.Equal(29, paths.Count);
        var login = document.GetProperty("paths").GetProperty("/api/auth/login").GetProperty("post");
        Assert.False(login.TryGetProperty("security", out _));
        Assert.True(document.GetProperty("paths").GetProperty("/api/suppliers").GetProperty("get").TryGetProperty("security", out _));
    }
}
