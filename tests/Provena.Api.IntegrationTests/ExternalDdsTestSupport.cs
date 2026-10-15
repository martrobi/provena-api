using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
namespace Provena.Api.IntegrationTests;

// HTTP helpers for tests that need an External DDS Record (External DDS Record v1) before closing a case as ExternalDdsCompleted. The VerificationNumber default is a fixture value, never a real one.
internal static class ExternalDdsTestSupport
{
    public const string DefaultVerification = "VERIF-FIXTURE-7Q2X";
    public static string Url(Guid caseId) => $"/api/eudr-cases/{caseId}/external-dds";

    public static object Body(Guid? reviewId, string? reference = "DDS-ABC-1", string? verification = DefaultVerification, Guid? expected = null, Guid? receipt = null, string? reason = null, DateTimeOffset? submittedAt = null)
        => new { expectedLatestExternalDdsRecordId = expected, reviewId, referenceNumber = reference, verificationNumber = verification, submittedAtUtc = submittedAt ?? DateTimeOffset.UtcNow, receiptDocumentId = receipt, replacementReason = reason };

    public static Task<HttpResponseMessage> Post(HttpClient c, Guid caseId, object body) => c.PostAsJsonAsync(Url(caseId), body);

    // The id of the current record, or null on 204.
    public static async Task<Guid?> CurrentId(HttpClient c, Guid caseId)
    {
        var response = await c.GetAsync(Url(caseId));
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("recordId").GetGuid();
    }

    // Records the external submission of the review (a replacement when a record already exists); asserts 201 and returns the new record id.
    public static async Task<Guid> RecordOk(HttpClient c, Guid caseId, Guid reviewId, string reference = "DDS-ABC-1", string verification = DefaultVerification)
    {
        var current = await CurrentId(c, caseId);
        var response = await Post(c, caseId, Body(reviewId, reference, verification, expected: current, reason: current == null ? null : "Re-recorded by a test."));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("recordId").GetGuid();
    }
}
