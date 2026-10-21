using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Provena.Api.IntegrationTests;

// PUT applicability now carries the optimistic-concurrency token. Tests that are not about concurrency use Put, which loads the current version id the way a client does and sends it back
// (null when nothing is recorded); the concurrency tests build the body themselves.
internal static class ApplicabilitySupport
{
    public static async Task<Guid?> CurrentId(HttpClient client, Guid caseId)
    {
        var response = await client.GetAsync($"/api/eudr-cases/{caseId}/applicability");
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        if (!response.IsSuccessStatusCode) return null; // an unknown or foreign case: let the PUT report it
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("current").GetProperty("id").GetGuid();
    }

    public static JsonObject With(object body, Guid? expected)
    {
        var node = JsonSerializer.SerializeToNode(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        node["expectedCurrentApplicabilityId"] = expected;
        return node;
    }

    public static async Task<HttpResponseMessage> Put(HttpClient client, Guid caseId, object body)
        => await client.PutAsJsonAsync($"/api/eudr-cases/{caseId}/applicability", With(body, await CurrentId(client, caseId)));

    public static Task<HttpResponseMessage> PutExpecting(HttpClient client, Guid caseId, object body, Guid? expected)
        => client.PutAsJsonAsync($"/api/eudr-cases/{caseId}/applicability", With(body, expected));
}
