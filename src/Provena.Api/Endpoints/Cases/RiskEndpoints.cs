using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Provena.Api.Contracts;
using Provena.Api.Services;
namespace Provena.Api.Endpoints;

// Risk Assessment & Mitigation v1 (internal, authenticated). Recording an assessment is Owner-only (live check inside RiskAssessmentService, before any case lookup and before the body is
// read); recording a mitigation measure and both GETs are open to any member. The POST handlers read their JSON body themselves, after authorization and the case lock. There is no PUT, PATCH or
// DELETE: everything is append-only. These are human-entered records, not a regulatory approval, legal-compliance determination or DDS statement.
internal static class RiskEndpoints
{
    private const string AssessmentRoute = "GetRiskAssessment";
    private const string OverviewRoute = "GetRiskOverview";
    public static void MapRiskEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/eudr-cases/{caseId:guid}").WithTags("Risk");
        group.MapGet("/risk", Overview).WithName(OverviewRoute);
        group.MapGet("/risk-assessments/{assessmentId:guid}", Get).WithName(AssessmentRoute);
        group.MapPost("/risk-assessments", Assess).WithManuallyReadJsonBody<CreateRiskAssessmentInput, RouteHandlerBuilder>("expectedLatestRiskAssessmentId", "expectedLatestMitigationMeasureId"); // null when none existed
        group.MapPost("/risk-mitigations", Mitigate).WithManuallyReadJsonBody<RecordRiskMitigationInput, RouteHandlerBuilder>("evidenceDocumentId"); // optional
    }

    private static async Task<Ok<RiskOverviewDto>> Overview(Guid caseId, RiskAssessmentService service, CancellationToken ct) => TypedResults.Ok(await service.Overview(caseId, ct));
    private static async Task<Ok<RiskAssessmentDto>> Get(Guid caseId, Guid assessmentId, RiskAssessmentService service, CancellationToken ct) => TypedResults.Ok(await service.Get(caseId, assessmentId, ct));
    private static async Task<CreatedAtRoute<RiskAssessmentDto>> Assess(Guid caseId, HttpRequest request, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, RiskAssessmentService service, CancellationToken ct)
    {
        var created = await service.Assess(caseId, CaseLifecycleEndpoints.Body<CreateRiskAssessmentInput>(request, json, "a risk assessment"), ct);
        return TypedResults.CreatedAtRoute(created, AssessmentRoute, new { caseId, assessmentId = created.AssessmentId });
    }
    private static async Task<CreatedAtRoute<RiskMitigationDto>> Mitigate(Guid caseId, HttpRequest request, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, RiskAssessmentService service, CancellationToken ct)
    {
        var created = await service.Mitigate(caseId, CaseLifecycleEndpoints.Body<RecordRiskMitigationInput>(request, json, "a mitigation measure"), ct);
        return TypedResults.CreatedAtRoute(created, OverviewRoute, new { caseId });
    }
}
