using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Provena.Api.Data;
using Xunit;
namespace Provena.Api.IntegrationTests;
[Collection("Api")]
public sealed class WorkflowTests(ApiFactory factory)
{
    private static async Task<JsonElement> Json(HttpResponseMessage response)
    { var body = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {body}"); return JsonDocument.Parse(body).RootElement.Clone(); }
    private async Task<HttpClient> Register(string suffix)
    {
        var client = factory.CreateClient(); var email = $"{suffix}-{Guid.NewGuid():N}@example.test";
        var json = await Json(await client.PostAsJsonAsync("/api/auth/register", new { email, password = "Test-Only-Password-928!", firstName = "Test", lastName = "User", organizationName = "Org " + suffix }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", json.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Test-Only-Password-928!" })).StatusCode);
        return client;
    }
    private static async Task<(Guid Supplier, Guid Product, Guid Shipment, Guid Case)> Setup(HttpClient client)
    {
        var s = await Json(await client.PostAsJsonAsync("/api/suppliers", new { name = "Coffee Brasil Ltd.", externalReference = "SUP-001", email = "contact@example.com", countryCode = "BR" }));
        var p = await Json(await client.PostAsJsonAsync("/api/products", new { name = "Arabica Coffee", cnCode = "0901", commodityType = "Coffee", externalReference = "P-001", isActive = true }));
        var supplier = s.GetProperty("id").GetGuid(); var product = p.GetProperty("id").GetGuid();
        var shipment = await Json(await client.PostAsJsonAsync("/api/shipments", new { referenceNumber = "IMP-2027-00124", supplierId = supplier, productId = product, quantity = 18500, unit = "Kg", originCountryCode = "BR" }));
        return (supplier, product, shipment.GetProperty("shipment").GetProperty("id").GetGuid(), shipment.GetProperty("eudrCaseId").GetGuid());
    }
    private static MultipartFormDataContent Upload(string filename, string type, string text)
    { var body = new MultipartFormDataContent(); var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text)); file.Headers.ContentType = new MediaTypeHeaderValue(type); body.Add(file, "file", filename); return body; }
    [Fact]
    public async Task Full_http_flow_and_organization_isolation()
    {
        using var publicClient = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await publicClient.GetAsync("/api/suppliers")).StatusCode);
        using var a = await Register("A"); using var b = await Register("B");
        var ids = await Setup(a);
        var initial = await Json(await a.GetAsync($"/api/eudr-cases/{ids.Case}"));
        Assert.Equal("Draft", initial.GetProperty("status").GetString()); Assert.Equal(6, initial.GetProperty("requirements").GetArrayLength());
        Assert.All(initial.GetProperty("requirements").EnumerateArray(), r => Assert.Equal("Missing", r.GetProperty("status").GetString()));
        foreach (var path in new[] { $"/api/suppliers/{ids.Supplier}", $"/api/eudr-cases/{ids.Case}", $"/api/eudr-cases/{ids.Case}/export", $"/api/shipments/{ids.Shipment}" })
            Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.PostAsJsonAsync("/api/shipments", new { referenceNumber = "X", supplierId = ids.Supplier, productId = ids.Product, quantity = 1, unit = "Kg" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsJsonAsync($"/api/eudr-cases/{ids.Case}/complete", new { })).StatusCode);
        var link = await Json(await a.PostAsync($"/api/eudr-cases/{ids.Case}/supplier-request", null));
        var requestId = link.GetProperty("requestId").GetGuid(); var token = new Uri(link.GetProperty("publicUrl").GetString()!).Segments.Last();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>(); var stored = await db.SupplierRequests.IgnoreQueryFilters().SingleAsync(x => x.Id == requestId);
            Assert.NotEqual(token, stored.TokenHash); Assert.Equal(64, stored.TokenHash.Length);
        }
        var pathBase = $"/public/supplier-requests/{token}";
        var publicData = await Json(await publicClient.GetAsync(pathBase));
        Assert.Equal("IMP-2027-00124", publicData.GetProperty("shipmentReference").GetString());
        Assert.False(publicData.TryGetProperty("auditEvents", out _)); Assert.False(publicData.TryGetProperty("organizationId", out _));
        Assert.Equal(HttpStatusCode.Conflict, (await publicClient.PostAsync(pathBase + "/complete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await publicClient.PutAsJsonAsync(pathBase + "/details", new { countryOfProductionCode = "BR", productionPeriodFrom = "2026-01-01", productionPeriodTo = "2026-06-30", confirmedQuantity = 18500, notes = "Harvest 2026" })).StatusCode);
        var afterDetails = await Json(await a.GetAsync($"/api/eudr-cases/{ids.Case}"));
        Assert.Equal(4, afterDetails.GetProperty("requirements").EnumerateArray().Count(x => x.GetProperty("status").GetString() == "Submitted"));
        using var badGeo = Upload("bad.geojson", "application/geo+json", "{}");
        Assert.Equal(HttpStatusCode.BadRequest, (await publicClient.PostAsync(pathBase + "/geolocation", badGeo)).StatusCode);
        using var doc = Upload("declaration.txt", "text/plain", "Supplier declaration"); await Json(await publicClient.PostAsync(pathBase + "/documents", doc));
        using var geo = Upload("plots.geojson", "application/geo+json", """{"type":"Polygon","coordinates":[[[-48,-20],[-47,-20],[-47,-19],[-48,-20]]]}""");
        await Json(await publicClient.PostAsync(pathBase + "/geolocation", geo));
        Assert.Equal(HttpStatusCode.NoContent, (await publicClient.PostAsync(pathBase + "/complete", null)).StatusCode);
        var details = await Json(await a.GetAsync($"/api/eudr-cases/{ids.Case}")); Assert.Equal("ReviewNeeded", details.GetProperty("status").GetString());
        var countryRequirement = details.GetProperty("requirements").EnumerateArray().Single(x => x.GetProperty("type").GetString() == "CountryOfProduction").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await a.PatchAsJsonAsync($"/api/eudr-cases/{ids.Case}/requirements/{countryRequirement}", new { status = "Rejected", reviewerNote = "Please confirm." })).StatusCode);
        await publicClient.PutAsJsonAsync(pathBase + "/details", new { countryOfProductionCode = "BR", productionPeriodFrom = "2026-01-01", productionPeriodTo = "2026-06-30", confirmedQuantity = 18500 });
        details = await Json(await a.GetAsync($"/api/eudr-cases/{ids.Case}"));
        foreach (var r in details.GetProperty("requirements").EnumerateArray())
            Assert.Equal(HttpStatusCode.NoContent, (await a.PatchAsJsonAsync($"/api/eudr-cases/{ids.Case}/requirements/{r.GetProperty("id").GetGuid()}", new { status = "Accepted", reviewerNote = "Reviewed." })).StatusCode);
        details = await Json(await a.GetAsync($"/api/eudr-cases/{ids.Case}")); Assert.Equal("ReadyForExport", details.GetProperty("status").GetString());
        var zipResponse = await a.GetAsync($"/api/eudr-cases/{ids.Case}/export"); Assert.Equal(HttpStatusCode.OK, zipResponse.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await zipResponse.Content.ReadAsByteArrayAsync()));
        Assert.Contains(zip.Entries, e => e.FullName.EndsWith("manifest.json")); Assert.Contains(zip.Entries, e => e.FullName.EndsWith("summary.csv")); Assert.Contains(zip.Entries, e => e.FullName.Contains("documents/") && e.FullName.EndsWith("declaration.txt")); Assert.Contains(zip.Entries, e => e.FullName.EndsWith("geolocation/original.geojson"));
        Assert.Equal(HttpStatusCode.NoContent, (await a.PostAsJsonAsync($"/api/eudr-cases/{ids.Case}/complete", new { externalDdsReference = "ABC-123456" })).StatusCode);
        Assert.Equal("Completed", (await Json(await a.GetAsync($"/api/eudr-cases/{ids.Case}"))).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await publicClient.PutAsJsonAsync(pathBase + "/details", new { countryOfProductionCode = "BR" })).StatusCode);
    }
    [Fact]
    public async Task Token_rotation_revocation_and_expiry_are_enforced()
    {
        using var a = await Register("tokens"); using var publicClient = factory.CreateClient(); var ids = await Setup(a);
        var link = await Json(await a.PostAsync($"/api/eudr-cases/{ids.Case}/supplier-request", null)); var requestId = link.GetProperty("requestId").GetGuid();
        string Token(JsonElement value) => new Uri(value.GetProperty("publicUrl").GetString()!).Segments.Last();
        var old = Token(link); link = await Json(await a.PostAsync($"/api/supplier-requests/{requestId}/regenerate", null)); var current = Token(link);
        Assert.Equal(HttpStatusCode.NotFound, (await publicClient.GetAsync($"/public/supplier-requests/{old}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await publicClient.GetAsync($"/public/supplier-requests/{current}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.PostAsync($"/api/supplier-requests/{requestId}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await publicClient.GetAsync($"/public/supplier-requests/{current}")).StatusCode);
        link = await Json(await a.PostAsync($"/api/supplier-requests/{requestId}/regenerate", null)); current = Token(link);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.SupplierRequests.IgnoreQueryFilters().Where(x => x.Id == requestId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAtUtc, DateTimeOffset.UtcNow.AddDays(-1)));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await publicClient.GetAsync($"/public/supplier-requests/{current}")).StatusCode);
    }
}
