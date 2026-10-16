using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Provena.Api.Services;
using Xunit;
using static Provena.Api.IntegrationTests.CompletenessReadModelTests;
namespace Provena.Api.IntegrationTests;

// HTTP helpers for tests that need a risk assessment (Risk Assessment & Mitigation v1) before closing a case as ExternalDdsCompleted. The text values are fixtures, never real findings.
internal static class RiskTestSupport
{
    public const string Rationale = "Reasoned by the Owner from the recorded facts.";
    public static string Url(Guid caseId, string tail = "risk-assessments") => $"/api/eudr-cases/{caseId}/{tail}";

    // One finding per catalogue criterion; `overrides` maps a code to (finding, note).
    public static List<object> Findings(IReadOnlyDictionary<string, (string Finding, string? Note)>? overrides = null)
        => RiskCriteriaCatalogue.Criteria.Select(c => overrides != null && overrides.TryGetValue(c.Code, out var o)
            ? (object)new { criterionCode = c.Code, finding = o.Finding, note = o.Note } : new { criterionCode = c.Code, finding = "NoConcern", note = (string?)null }).ToList();

    public static object AssessBody(Guid? reviewId, string conclusion = "Negligible", Guid? expectedAssessment = null, Guid? expectedMitigation = null, string version = RiskCriteriaCatalogue.Version,
        string? rationale = Rationale, IEnumerable<object>? findings = null)
        => new { reviewId, expectedLatestRiskAssessmentId = expectedAssessment, expectedLatestMitigationMeasureId = expectedMitigation, criteriaCatalogueVersion = version, conclusion, rationale, findings = findings ?? Findings() };

    public static object MitigationBody(Guid? expectedAssessment, string? description = "Segregated the lot and verified the origin.", DateTimeOffset? performedAt = null, Guid? evidence = null)
        => new { expectedRiskAssessmentId = expectedAssessment, description, performedAtUtc = performedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1), evidenceDocumentId = evidence };

    public static Task<HttpResponseMessage> Assess(HttpClient c, Guid caseId, object body) => c.PostAsJsonAsync(Url(caseId), body);
    public static Task<HttpResponseMessage> Mitigate(HttpClient c, Guid caseId, object body) => c.PostAsJsonAsync(Url(caseId, "risk-mitigations"), body);
    public static async Task<JsonElement> Overview(HttpClient c, Guid caseId) => await Json(await c.GetAsync(Url(caseId, "risk")), HttpStatusCode.OK);

    // The ids an Owner would have loaded: the newest assessment and the newest measure (null when none exist).
    public static async Task<(Guid? Assessment, Guid? Mitigation)> Latest(HttpClient c, Guid caseId)
    {
        var overview = await Overview(c, caseId);
        var assessment = overview.GetProperty("latestAssessment").ValueKind == JsonValueKind.Null ? (Guid?)null : overview.GetProperty("latestAssessment").GetProperty("assessmentId").GetGuid();
        var measures = overview.GetProperty("mitigations").EnumerateArray().ToList();
        return (assessment, measures.Count == 0 ? null : measures[0].GetProperty("mitigationId").GetGuid());
    }

    // Records an assessment of the review exactly as an Owner would (loading the latest ids first); asserts 201 and returns the new assessment.
    public static async Task<JsonElement> AssessOk(HttpClient c, Guid caseId, Guid reviewId, string conclusion = "Negligible", IEnumerable<object>? findings = null, string? rationale = Rationale)
    {
        var (assessment, mitigation) = await Latest(c, caseId);
        var response = await Assess(c, caseId, AssessBody(reviewId, conclusion, assessment, mitigation, rationale: rationale, findings: findings));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
    public static async Task<Guid> AssessNegligibleOk(HttpClient c, Guid caseId, Guid reviewId) => (await AssessOk(c, caseId, reviewId)).GetProperty("assessmentId").GetGuid();

    // The reason of a ProblemDetails 409/400 body.
    public static string Reason(JsonElement problem) => problem.GetProperty("errors").GetProperty("reason").GetString()!;

    // For tests whose subject is something else and that only need the risk gate satisfied before closing as ExternalDdsCompleted: records a Negligible assessment of the review unless the latest
    // assessment already is a Negligible one of exactly that review. It asserts 201, so it never hides a refused assessment.
    public static async Task EnsureNegligible(HttpClient c, Guid caseId, Guid reviewId)
    {
        var latest = (await Overview(c, caseId)).GetProperty("latestAssessment");
        if (latest.ValueKind != JsonValueKind.Null && latest.GetProperty("reviewId").GetGuid() == reviewId && latest.GetProperty("conclusion").GetString() == "Negligible") return;
        await AssessNegligibleOk(c, caseId, reviewId);
    }
}
