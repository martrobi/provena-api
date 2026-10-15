using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Provena.Api.Contracts;
using Provena.Api.Services;
namespace Provena.Api.Endpoints;

// External DDS Record v1 (internal, authenticated). POST is Owner-only (live check inside ExternalDdsService, before any case lookup and before the body is read); the two GETs are open to
// any member. There is no PUT, PATCH or DELETE: records are append-only and a correction is a new record. Records a submission made outside Provena; not an approval or compliance claim.
internal static class ExternalDdsEndpoints
{
    private const string CurrentRoute = "GetCurrentExternalDds";
    public static void MapExternalDdsEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/eudr-cases/{caseId:guid}").WithTags("External DDS");
        group.MapGet("/external-dds", Current).WithName(CurrentRoute);
        group.MapGet("/external-dds-history", History);
        group.MapPost("/external-dds", Record).WithManuallyReadJsonBody<CreateExternalDdsRecordInput, RouteHandlerBuilder>("expectedLatestExternalDdsRecordId", "receiptDocumentId", "replacementReason"); // optional or nullable
    }

    // 204 while no current record exists.
    private static async Task<Results<Ok<ExternalDdsRecordDto>, NoContent>> Current(Guid caseId, ExternalDdsService service, CancellationToken ct)
        => await service.Current(caseId, ct) is { } record ? TypedResults.Ok(record) : TypedResults.NoContent();
    private static async Task<Ok<List<ExternalDdsRecordDto>>> History(Guid caseId, ExternalDdsService service, CancellationToken ct) => TypedResults.Ok(await service.History(caseId, ct));
    private static async Task<CreatedAtRoute<ExternalDdsRecordDto>> Record(Guid caseId, HttpRequest request, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, ExternalDdsService service, CancellationToken ct)
        => TypedResults.CreatedAtRoute(await service.Record(caseId, CaseLifecycleEndpoints.Body<CreateExternalDdsRecordInput>(request, json, "an external DDS record"), ct), CurrentRoute, new { caseId });
}
