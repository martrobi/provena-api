using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Provena.Api.Contracts;
using Provena.Api.Services;
namespace Provena.Api.Endpoints;

// Post-completion Concern & Corrective Action v1 (internal, authenticated). Concerns, actions and notifications are open to any member; resolving is Owner-only (live check inside
// ConcernService, before any case lookup and before the body is read). The POST handlers read their JSON body themselves, after authorization and the case lock. There is no PUT, PATCH or
// DELETE: everything is append-only. A concern never reopens the case; it is a record about a completed or archived case, not a compliance or regulatory statement.
internal static class ConcernEndpoints
{
    private const string ConcernRoute = "GetCaseConcern";
    public static void MapConcernEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/eudr-cases/{caseId:guid}/concerns").WithTags("Concerns");
        group.MapGet("", List);
        group.MapPost("", Create).WithManuallyReadJsonBody<CreateConcernInput, RouteHandlerBuilder>();
        group.MapGet("/{concernId:guid}", Get).WithName(ConcernRoute);
        group.MapPost("/{concernId:guid}/actions", RecordAction).WithManuallyReadJsonBody<RecordConcernActionInput, RouteHandlerBuilder>("evidenceDocumentId"); // optional
        group.MapPost("/{concernId:guid}/notifications", RecordNotification).WithManuallyReadJsonBody<RecordConcernNotificationInput, RouteHandlerBuilder>("note", "evidenceDocumentId"); // optional
        group.MapPost("/{concernId:guid}/resolution", Resolve).WithManuallyReadJsonBody<ResolveConcernInput, RouteHandlerBuilder>();
    }

    private static async Task<Ok<List<ConcernSummaryDto>>> List(Guid caseId, ConcernService service, CancellationToken ct) => TypedResults.Ok(await service.List(caseId, ct));
    private static async Task<Ok<ConcernDto>> Get(Guid caseId, Guid concernId, ConcernService service, CancellationToken ct) => TypedResults.Ok(await service.Get(caseId, concernId, ct));
    private static async Task<CreatedAtRoute<ConcernDto>> Create(Guid caseId, HttpRequest request, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, ConcernService service, CancellationToken ct)
    {
        var created = await service.Create(caseId, CaseLifecycleEndpoints.Body<CreateConcernInput>(request, json, "a concern"), ct);
        return TypedResults.CreatedAtRoute(created, ConcernRoute, new { caseId, concernId = created.ConcernId });
    }
    private static async Task<CreatedAtRoute<ConcernActionDto>> RecordAction(Guid caseId, Guid concernId, HttpRequest request, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, ConcernService service, CancellationToken ct)
        => TypedResults.CreatedAtRoute(await service.RecordAction(caseId, concernId, CaseLifecycleEndpoints.Body<RecordConcernActionInput>(request, json, "a corrective action"), ct), ConcernRoute, new { caseId, concernId });
    private static async Task<CreatedAtRoute<ConcernNotificationDto>> RecordNotification(Guid caseId, Guid concernId, HttpRequest request, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, ConcernService service, CancellationToken ct)
        => TypedResults.CreatedAtRoute(await service.RecordNotification(caseId, concernId, CaseLifecycleEndpoints.Body<RecordConcernNotificationInput>(request, json, "a notification"), ct), ConcernRoute, new { caseId, concernId });
    private static async Task<CreatedAtRoute<ConcernResolutionDto>> Resolve(Guid caseId, Guid concernId, HttpRequest request, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, ConcernService service, CancellationToken ct)
        => TypedResults.CreatedAtRoute(await service.Resolve(caseId, concernId, CaseLifecycleEndpoints.Body<ResolveConcernInput>(request, json, "a concern resolution"), ct), ConcernRoute, new { caseId, concernId });
}
