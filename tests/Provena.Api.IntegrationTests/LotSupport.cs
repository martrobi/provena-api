using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Provena.Api.IntegrationTests;

// PUT dossier/lot carries the optimistic-concurrency token. Tests that are not about concurrency use Put, which loads the current lot id the way a client does and sends it back
// (null when no lot exists); a body that names expectedCurrentLotId itself (including an explicit null) is sent as written. The concurrency tests build the body themselves.
internal static class LotSupport
{
    public static async Task<Guid?> CurrentId(HttpClient client, Guid caseId)
    {
        var response = await client.GetAsync($"/api/eudr-cases/{caseId}/dossier");
        if (!response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NoContent) return null; // no dossier, or an unknown/foreign case: let the PUT report it
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("currentLot").GetProperty("id").GetGuid();
    }

    public static JsonObject With(object body, Guid? expected)
    {
        var node = JsonSerializer.SerializeToNode(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        node["expectedCurrentLotId"] = expected;
        return node;
    }

    public static async Task<HttpResponseMessage> Put(HttpClient client, Guid caseId, object body)
    {
        var node = JsonSerializer.SerializeToNode(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        if (!node.ContainsKey("expectedCurrentLotId")) node["expectedCurrentLotId"] = await CurrentId(client, caseId);
        return await client.PutAsJsonAsync($"/api/eudr-cases/{caseId}/dossier/lot", node);
    }

    public static Task<HttpResponseMessage> PutExpecting(HttpClient client, Guid caseId, object body, Guid? expected)
        => client.PutAsJsonAsync($"/api/eudr-cases/{caseId}/dossier/lot", With(body, expected));
}
