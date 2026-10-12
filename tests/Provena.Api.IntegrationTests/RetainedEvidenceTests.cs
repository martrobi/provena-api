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
using Provena.Api.Data;
using Provena.Api.Domain;
using Provena.Api.Infrastructure;
using Xunit;
namespace Provena.Api.IntegrationTests;

// Slice 2 / commit 1: evidence rows are append-only (withdrawn / superseded, never deleted or overwritten) and bytes are never physically removed.
[Collection("Api")]
public sealed class RetainedEvidenceTests(ApiFactory factory)
{
    private const string GeoV1 = """{"type":"Polygon","coordinates":[[[-48,-20],[-47,-20],[-47,-19],[-48,-20]]]}""";
    private const string GeoV2 = """{"type":"Polygon","coordinates":[[[-50,-22],[-49,-22],[-49,-21],[-50,-22]]]}""";

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    { var body = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {body}"); return JsonDocument.Parse(body).RootElement.Clone(); }
    // The auth rate limiter is shared per fixture (10 requests / 5 minutes), so all tests in this class use one registered organization; each test creates its own case with unique references.
    private static Task<HttpClient>? sharedClient; private static readonly object SharedLock = new();
    private Task<HttpClient> Shared() { lock (SharedLock) return sharedClient ??= Register("Retained"); }
    private async Task<HttpClient> Register(string suffix)
    {
        var client = factory.CreateClient(); var email = $"{suffix}-{Guid.NewGuid():N}@example.test";
        var json = await Json(await client.PostAsJsonAsync("/api/auth/register", new { email, password = "Test-Only-Password-928!", firstName = "Test", lastName = "User", organizationName = "Org " + suffix }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", json.GetProperty("accessToken").GetString());
        return client;
    }
    private static async Task<Guid> SetupCase(HttpClient client)
    {
        var s = await Json(await client.PostAsJsonAsync("/api/suppliers", new { name = "Coffee Brasil Ltd.", externalReference = "SUP-" + Guid.NewGuid().ToString("N"), email = "contact@example.com", countryCode = "BR" }));
        var p = await Json(await client.PostAsJsonAsync("/api/products", new { name = "Arabica Coffee", cnCode = "0901", commodityType = "Coffee", externalReference = "P-" + Guid.NewGuid().ToString("N"), isActive = true }));
        var shipment = await Json(await client.PostAsJsonAsync("/api/shipments", new { referenceNumber = "IMP-RET-" + Guid.NewGuid().ToString("N"), supplierId = s.GetProperty("id").GetGuid(), productId = p.GetProperty("id").GetGuid(), quantity = 100, unit = "Kg", originCountryCode = "BR" }));
        return shipment.GetProperty("eudrCaseId").GetGuid();
    }
    private static MultipartFormDataContent Upload(string filename, string type, string text)
    { var body = new MultipartFormDataContent(); var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text)); file.Headers.ContentType = new MediaTypeHeaderValue(type); body.Add(file, "file", filename); return body; }
    private static async Task<Guid> UploadDocument(HttpClient client, Guid caseId, string name, string text)
    { using var body = Upload(name, "text/plain", text); return (await Json(await client.PostAsync($"/api/eudr-cases/{caseId}/documents", body))).GetProperty("id").GetGuid(); }
    private static async Task<Guid> UploadGeo(HttpClient client, Guid caseId, string name, string geoJson)
    { using var body = Upload(name, "application/geo+json", geoJson); return (await Json(await client.PostAsync($"/api/eudr-cases/{caseId}/geolocation", body))).GetProperty("id").GetGuid(); }
    private static async Task<string> PublicBase(HttpClient client, Guid caseId)
    { var link = await Json(await client.PostAsync($"/api/eudr-cases/{caseId}/supplier-request", null)); return "/public/supplier-requests/" + new Uri(link.GetProperty("publicUrl").GetString()!).Segments.Last(); }
    private static Task<HttpResponseMessage> SaveDetails(HttpClient publicClient, string pathBase, string notes)
        => publicClient.PutAsJsonAsync(pathBase + "/details", new { countryOfProductionCode = "BR", productionPeriodFrom = "2026-01-01", productionPeriodTo = "2026-06-30", confirmedQuantity = 100, notes });
    private static async Task<string> RequirementStatus(HttpClient client, Guid caseId, string type)
        => (await Json(await client.GetAsync($"/api/eudr-cases/{caseId}"))).GetProperty("requirements").EnumerateArray().Single(x => x.GetProperty("type").GetString() == type).GetProperty("status").GetString()!;
    private async Task<AppDbContext> TenantDb(IServiceScope scope, Guid caseId)
    {
        // Tests read retained rows directly; a tenant-bound context keeps the production query filters and guards active.
        var admin = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organizationId = await admin.Cases.IgnoreQueryFilters().Where(x => x.Id == caseId).Select(x => x.OrganizationId).SingleAsync();
        var tenant = new TenantContext(new HttpContextAccessor()); tenant.SetOrganization(organizationId);
        return new AppDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>(), tenant);
    }
    private async Task<(List<CaseDocument> Docs, List<GeoSubmission> Geos, List<SupplierSubmission> Subs)> Rows(Guid caseId)
    {
        await using var scope = factory.Services.CreateAsyncScope(); await using var db = await TenantDb(scope, caseId);
        return (await db.Documents.AsNoTracking().Where(x => x.EudrCaseId == caseId).OrderBy(x => x.CreatedAtUtc).ToListAsync(),
            await db.Geolocations.AsNoTracking().Where(x => x.EudrCaseId == caseId).OrderBy(x => x.CreatedAtUtc).ToListAsync(),
            await db.Submissions.AsNoTracking().Where(x => x.EudrCaseId == caseId).OrderBy(x => x.CreatedAtUtc).ToListAsync());
    }
    private async Task<List<AuditEvent>> Audit(Guid caseId)
    { await using var scope = factory.Services.CreateAsyncScope(); await using var db = await TenantDb(scope, caseId); return await db.AuditEvents.AsNoTracking().Where(x => x.EudrCaseId == caseId).ToListAsync(); }
    // Scenario 8: every retained row's bytes still open through IFileStorage and match what was uploaded.
    private async Task AssertBytesRetained(Guid caseId, Dictionary<string, string> uploadedByFileName)
    {
        var (docs, geos, _) = await Rows(caseId); var storage = factory.Services.GetRequiredService<IFileStorage>();
        foreach (var (name, key) in docs.Select(d => (d.OriginalFileName, d.StorageKey)).Concat(geos.Select(g => (g.OriginalFileName, g.StorageKey))))
        { await using var stream = await storage.OpenReadAsync(key, CancellationToken.None); using var reader = new StreamReader(stream); Assert.Equal(uploadedByFileName[name], await reader.ReadToEndAsync()); }
    }
    private static async Task SetStatus(AppDbContext db, Guid caseId, EudrCaseStatus status)
        => await db.Database.ExecuteSqlRawAsync("""UPDATE "Cases" SET "Status" = {0} WHERE "Id" = {1}""", (int)status, caseId);

    [Fact]
    public async Task Document_delete_withdraws_and_retains_row_bytes_and_download()
    {
        var a = await Shared(); var caseId = await SetupCase(a);
        var d1 = await UploadDocument(a, caseId, "one.txt", "first"); var d2 = await UploadDocument(a, caseId, "two.txt", "second");

        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/api/eudr-cases/{caseId}/documents/{d1}")).StatusCode);
        var listed = await Json(await a.GetAsync($"/api/eudr-cases/{caseId}/documents"));
        Assert.Equal([d2], listed.EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal("Submitted", await RequirementStatus(a, caseId, "SupportingDocuments")); // another current document remains
        Assert.Equal([d2], (await Json(await a.GetAsync($"/api/eudr-cases/{caseId}"))).GetProperty("documents").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray());

        var (docs, _, _) = await Rows(caseId);
        var withdrawn = docs.Single(x => x.Id == d1);
        Assert.NotNull(withdrawn.WithdrawnAtUtc); Assert.NotNull(withdrawn.WithdrawnByUserId); Assert.NotNull(withdrawn.UploadedByUserId);
        Assert.Null(docs.Single(x => x.Id == d2).WithdrawnAtUtc);
        var download = await a.GetAsync($"/api/eudr-cases/{caseId}/documents/{d1}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode); Assert.Equal("first", await download.Content.ReadAsStringAsync());
        Assert.Single(await Audit(caseId), e => e.EventType == "DocumentWithdrawn");

        // Repeat delete is an idempotent no-op; unknown ids stay 404.
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/api/eudr-cases/{caseId}/documents/{d1}")).StatusCode);
        Assert.Single(await Audit(caseId), e => e.EventType == "DocumentWithdrawn");
        Assert.Equal(HttpStatusCode.NotFound, (await a.DeleteAsync($"/api/eudr-cases/{caseId}/documents/{Guid.NewGuid()}")).StatusCode);

        // Withdrawing the last current document leaves the requirement Missing.
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/api/eudr-cases/{caseId}/documents/{d2}")).StatusCode);
        Assert.Equal("Missing", await RequirementStatus(a, caseId, "SupportingDocuments"));
        Assert.Empty((await Json(await a.GetAsync($"/api/eudr-cases/{caseId}/documents"))).EnumerateArray());

        // Replacement = upload new + withdraw old: only the new one is current and exported; both are retained.
        var d3 = await UploadDocument(a, caseId, "three.txt", "third");
        var zipResponse = await a.GetAsync($"/api/eudr-cases/{caseId}/export"); Assert.Equal(HttpStatusCode.OK, zipResponse.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await zipResponse.Content.ReadAsByteArrayAsync()));
        var documentEntries = zip.Entries.Where(e => e.FullName.Contains("/documents/")).Select(e => e.FullName).ToArray();
        Assert.Single(documentEntries); Assert.EndsWith($"{d3:N}-three.txt", documentEntries[0]);
        var manifest = JsonDocument.Parse(await new StreamReader(zip.Entries.Single(e => e.FullName.EndsWith("manifest.json")).Open()).ReadToEndAsync()).RootElement;
        Assert.Contains("not a reviewed dossier snapshot", manifest.GetProperty("evidenceScope").GetString());
        Assert.Equal(2, manifest.GetProperty("retainedNotIncluded").GetProperty("withdrawnDocuments").GetInt32());
        Assert.Equal(0, manifest.GetProperty("retainedNotIncluded").GetProperty("supersededGeolocations").GetInt32());
        Assert.Equal(0, manifest.GetProperty("retainedNotIncluded").GetProperty("supersededSupplierSubmissions").GetInt32());

        Assert.Equal(3, (await Rows(caseId)).Docs.Count);
        await AssertBytesRetained(caseId, new() { ["one.txt"] = "first", ["two.txt"] = "second", ["three.txt"] = "third" });
    }

    [Fact]
    public async Task Document_withdrawal_is_blocked_on_completed_cases()
    {
        var a = await Shared(); var caseId = await SetupCase(a);
        var d1 = await UploadDocument(a, caseId, "one.txt", "first");
        await using (var scope = factory.Services.CreateAsyncScope()) { await using var db = await TenantDb(scope, caseId); await SetStatus(db, caseId, EudrCaseStatus.Completed); }

        Assert.Equal(HttpStatusCode.Conflict, (await a.DeleteAsync($"/api/eudr-cases/{caseId}/documents/{d1}")).StatusCode);
        Assert.Null((await Rows(caseId)).Docs.Single().WithdrawnAtUtc);
    }

    [Fact]
    public async Task Geometry_replace_supersedes_and_retains_previous_row_and_bytes()
    {
        var a = await Shared(); using var publicClient = factory.CreateClient(); var caseId = await SetupCase(a);
        var v1 = await UploadGeo(a, caseId, "v1.geojson", GeoV1); var v2 = await UploadGeo(a, caseId, "v2.geojson", GeoV2);

        Assert.NotEqual(v1, v2);
        var current = await Json(await a.GetAsync($"/api/eudr-cases/{caseId}/geolocation"));
        Assert.Equal(v2, current.GetProperty("id").GetGuid()); Assert.Equal(JsonValueKind.Null, current.GetProperty("supersededAtUtc").ValueKind);
        Assert.Equal(v2, (await Json(await a.GetAsync($"/api/eudr-cases/{caseId}"))).GetProperty("geolocation").GetProperty("id").GetGuid());

        var (_, geos, _) = await Rows(caseId);
        Assert.Equal(2, geos.Count);
        var first = geos.Single(x => x.Id == v1); var second = geos.Single(x => x.Id == v2);
        Assert.NotNull(first.SupersededAtUtc); Assert.Null(second.SupersededAtUtc);
        Assert.NotEqual(first.StorageKey, second.StorageKey); Assert.Contains("-48", first.RawGeoJson); Assert.Contains("-50", second.RawGeoJson);
        Assert.NotNull(first.UploadedByUserId);

        // Export carries only the current geometry bytes.
        var zipResponse = await a.GetAsync($"/api/eudr-cases/{caseId}/export"); Assert.Equal(HttpStatusCode.OK, zipResponse.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await zipResponse.Content.ReadAsByteArrayAsync()));
        Assert.Equal(GeoV2, await new StreamReader(zip.Entries.Single(e => e.FullName.EndsWith("geolocation/original.geojson")).Open()).ReadToEndAsync());
        var manifest = JsonDocument.Parse(await new StreamReader(zip.Entries.Single(e => e.FullName.EndsWith("manifest.json")).Open()).ReadToEndAsync()).RootElement;
        Assert.Equal(1, manifest.GetProperty("retainedNotIncluded").GetProperty("supersededGeolocations").GetInt32());

        // Public portal: a supplier replacing the geometry also works (no SingleOrDefault failure) and shows only the current one.
        var pathBase = await PublicBase(a, caseId);
        using (var body = Upload("v3.geojson", "application/geo+json", GeoV1)) await Json(await publicClient.PostAsync(pathBase + "/geolocation", body));
        using (var body = Upload("v4.geojson", "application/geo+json", GeoV2)) await Json(await publicClient.PostAsync(pathBase + "/geolocation", body));
        var publicData = await Json(await publicClient.GetAsync(pathBase));
        Assert.Equal("v4.geojson", publicData.GetProperty("geolocation").GetProperty("originalFileName").GetString());
        var all = (await Rows(caseId)).Geos;
        Assert.Equal(4, all.Count); Assert.Single(all, x => x.SupersededAtUtc == null); Assert.Contains(all, x => x.SupplierRequestId != null);

        await AssertBytesRetained(caseId, new() { ["v1.geojson"] = GeoV1, ["v2.geojson"] = GeoV2, ["v3.geojson"] = GeoV1, ["v4.geojson"] = GeoV2 });
    }

    [Fact]
    public async Task Repeated_supplier_details_saves_append_versions_and_keep_earlier_values()
    {
        var a = await Shared(); using var publicClient = factory.CreateClient(); var caseId = await SetupCase(a); var pathBase = await PublicBase(a, caseId);

        foreach (var notes in new[] { "first", "second", "third" })
            Assert.Equal(HttpStatusCode.NoContent, (await SaveDetails(publicClient, pathBase, notes)).StatusCode);

        var (_, _, subs) = await Rows(caseId);
        Assert.Equal(new string?[] { "first", "second", "third" }, subs.Select(x => x.Notes).ToArray());
        Assert.Equal(2, subs.Count(x => x.SupersededAtUtc != null)); Assert.Null(subs[2].SupersededAtUtc);
        Assert.All(subs, x => Assert.NotNull(x.SupplierRequestId));
        Assert.Equal("third", (await Json(await a.GetAsync($"/api/eudr-cases/{caseId}"))).GetProperty("supplierSubmission").GetProperty("notes").GetString());
        Assert.Equal("third", (await Json(await publicClient.GetAsync(pathBase))).GetProperty("supplierSubmission").GetProperty("notes").GetString());
        Assert.Equal(3, (await Audit(caseId)).Count(e => e.EventType == "SupplierDetailsUpdated"));
    }

    [Fact]
    public async Task Resubmission_after_acceptance_resets_the_requirement_but_retains_the_accepted_values()
    {
        var a = await Shared(); using var publicClient = factory.CreateClient(); var caseId = await SetupCase(a); var pathBase = await PublicBase(a, caseId);
        await SaveDetails(publicClient, pathBase, "accepted values");
        var details = await Json(await a.GetAsync($"/api/eudr-cases/{caseId}"));
        var requirement = details.GetProperty("requirements").EnumerateArray().Single(x => x.GetProperty("type").GetString() == "CountryOfProduction").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await a.PatchAsJsonAsync($"/api/eudr-cases/{caseId}/requirements/{requirement}", new { status = "Accepted", reviewerNote = "ok" })).StatusCode);
        Assert.Equal("Accepted", await RequirementStatus(a, caseId, "CountryOfProduction"));

        await SaveDetails(publicClient, pathBase, "changed values");

        Assert.Equal("Submitted", await RequirementStatus(a, caseId, "CountryOfProduction"));
        // Resubmission clears the supplier completion, so the case is back to waiting for the supplier (it is not review-ready until the supplier completes again).
        Assert.Equal("WaitingForSupplier", (await Json(await a.GetAsync($"/api/eudr-cases/{caseId}"))).GetProperty("status").GetString());
        var (_, _, subs) = await Rows(caseId);
        Assert.Equal(2, subs.Count);
        Assert.Equal("accepted values", subs[0].Notes); Assert.NotNull(subs[0].SupersededAtUtc);
        Assert.Equal("changed values", subs[1].Notes); Assert.Null(subs[1].SupersededAtUtc);
    }

    [Fact]
    public async Task Persistence_guard_rejects_deletes_and_mutation_of_retained_evidence_but_allows_one_way_retirement()
    {
        var a = await Shared(); using var publicClient = factory.CreateClient(); var caseId = await SetupCase(a); var pathBase = await PublicBase(a, caseId);
        var docId = await UploadDocument(a, caseId, "g.txt", "guard"); await UploadGeo(a, caseId, "g.geojson", GeoV1);
        await SaveDetails(publicClient, pathBase, "one"); await SaveDetails(publicClient, pathBase, "two");
        await using var scope = factory.Services.CreateAsyncScope();

        async Task Rejected(Func<AppDbContext, Task> change)
        {
            await using var db = await TenantDb(scope, caseId);
            await change(db);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        // Deletes.
        await Rejected(async db => db.Documents.Remove(await db.Documents.SingleAsync(x => x.EudrCaseId == caseId)));
        await Rejected(async db => db.Geolocations.Remove(await db.Geolocations.SingleAsync(x => x.EudrCaseId == caseId)));
        await Rejected(async db => db.Submissions.Remove(await db.Submissions.OrderBy(x => x.CreatedAtUtc).FirstAsync(x => x.EudrCaseId == caseId)));
        // Mutation of stored content.
        await Rejected(async db => (await db.Documents.SingleAsync(x => x.EudrCaseId == caseId)).StorageKey = "other");
        await Rejected(async db => (await db.Geolocations.SingleAsync(x => x.EudrCaseId == caseId)).RawGeoJson = "{}");
        await Rejected(async db => (await db.Submissions.OrderBy(x => x.CreatedAtUtc).FirstAsync(x => x.EudrCaseId == caseId)).Notes = "tampered");
        // Un-retiring a superseded version.
        await Rejected(async db => (await db.Submissions.OrderBy(x => x.CreatedAtUtc).FirstAsync(x => x.EudrCaseId == caseId)).SupersededAtUtc = null);

        // One-way retirement is allowed once; clearing or changing it afterwards is not.
        await using (var db = await TenantDb(scope, caseId))
        { var geo = await db.Geolocations.SingleAsync(x => x.EudrCaseId == caseId); geo.SupersededAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); }
        await Rejected(async db => (await db.Geolocations.SingleAsync(x => x.EudrCaseId == caseId)).SupersededAtUtc = null);
        await Rejected(async db => (await db.Geolocations.SingleAsync(x => x.EudrCaseId == caseId)).SupersededAtUtc = DateTimeOffset.UtcNow.AddDays(1));
        await using (var db = await TenantDb(scope, caseId))
        { var doc = await db.Documents.SingleAsync(x => x.Id == docId); doc.WithdrawnAtUtc = DateTimeOffset.UtcNow; doc.WithdrawnByUserId = Guid.NewGuid(); await db.SaveChangesAsync(); }
        await Rejected(async db => (await db.Documents.SingleAsync(x => x.Id == docId)).WithdrawnAtUtc = null);
        await Rejected(async db => (await db.Documents.SingleAsync(x => x.Id == docId)).WithdrawnByUserId = null);

        // UpdatedAtUtc alone is not a retirement.
        await Rejected(async db => (await db.Documents.SingleAsync(x => x.Id == docId)).UpdatedAtUtc = DateTimeOffset.UtcNow);
        await Rejected(async db => (await db.Geolocations.SingleAsync(x => x.EudrCaseId == caseId)).UpdatedAtUtc = DateTimeOffset.UtcNow);
        // The withdrawal actor belongs to the same transition as WithdrawnAtUtc: it cannot be filled in afterwards.
        var anonymousDoc = await UploadDocument(a, caseId, "h.txt", "guard2");
        await using (var db = await TenantDb(scope, caseId))
        { (await db.Documents.SingleAsync(x => x.Id == anonymousDoc)).WithdrawnAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); }
        await Rejected(async db => (await db.Documents.SingleAsync(x => x.Id == anonymousDoc)).WithdrawnByUserId = Guid.NewGuid());
        // Changing evidence content in the same save as a retirement is also rejected.
        var third = await UploadDocument(a, caseId, "i.txt", "guard3");
        await Rejected(async db => { var d = await db.Documents.SingleAsync(x => x.Id == third); d.WithdrawnAtUtc = DateTimeOffset.UtcNow; d.StorageKey = "other"; });
    }
}
