using Microsoft.AspNetCore.Http.HttpResults;
using Provena.Api.Contracts;
using Provena.Api.Domain;
using Provena.Api.Services;
namespace Provena.Api.Endpoints;
internal static class CaseEndpoints
{
    public static void MapCaseEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/eudr-cases").WithTags("Cases");
        group.MapGet("", List);
        group.MapGet("/{id:guid}", Details);
        group.MapPatch("/{caseId:guid}/requirements/{requirementId:guid}", Review);
        group.MapPost("/{caseId:guid}/supplier-request", CreateSupplierRequest);
        group.MapPost("/{caseId:guid}/complete", Complete);
        group.MapGet("/{caseId:guid}/export", Export);
    }
    private static async Task<Ok<PageResult<CaseListItem>>> List(string? status, Guid? supplierId, Guid? productId, string? search, CaseService service, CancellationToken ct, int page = 1, int pageSize = 25)
        => TypedResults.Ok(await service.List(EndpointConventions.ParseEnum<EudrCaseStatus>(status, nameof(status)), supplierId, productId, search, page, pageSize, ct));
    private static async Task<Ok<CaseDetails>> Details(Guid id, CaseService service, CancellationToken ct) => TypedResults.Ok(await service.Details(id, ct));
    private static async Task<NoContent> Review(Guid caseId, Guid requirementId, ReviewInput input, CaseService service, CancellationToken ct)
    { await service.Review(caseId, requirementId, input, ct); return TypedResults.NoContent(); }
    private static async Task<Ok<RequestLink>> CreateSupplierRequest(Guid caseId, SupplierRequestService requests, CancellationToken ct) => TypedResults.Ok(await requests.Create(caseId, ct));
    // The body is optional: a nullable body parameter accepts an empty request.
    private static async Task<NoContent> Complete(Guid caseId, CompleteInput? input, CaseService service, CancellationToken ct)
    { await service.Complete(caseId, input ?? new(null), ct); return TypedResults.NoContent(); }
    private static async Task<FileStreamHttpResult> Export(Guid caseId, ExportService exports, CancellationToken ct)
    { var file = await exports.Export(caseId, ct); return TypedResults.File(file.Stream, "application/zip", file.FileName); }
}
