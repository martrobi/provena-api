using Microsoft.AspNetCore.Http.HttpResults;
using Provena.Api.Contracts;
using Provena.Api.Services;
namespace Provena.Api.Endpoints;
internal static class ProductionPlotEndpoints
{
    private const string GetRoute = "GetProductionPlot", GetRevisionRoute = "GetPlotRevision";
    public static void MapProductionPlotEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/production-plots").WithTags("Production plots");
        group.MapGet("", List);
        group.MapGet("/{id:guid}", Get).WithName(GetRoute);
        group.MapPost("", Create);
        group.MapPut("/{id:guid}", Update);
        group.MapPost("/{plotId:guid}/revisions", CreateRevision);
        group.MapGet("/{plotId:guid}/revisions", Revisions);
        group.MapGet("/{plotId:guid}/revisions/{revisionId:guid}", Revision).WithName(GetRevisionRoute);
    }
    private static async Task<Ok<PageResult<ProductionPlotListItem>>> List(Guid? producerId, string? search, bool? isActive, ProvenanceService service, CancellationToken ct, int page = 1, int pageSize = 25) => TypedResults.Ok(await service.Plots(producerId, search, isActive, page, pageSize, ct));
    private static async Task<Ok<ProductionPlotDto>> Get(Guid id, ProvenanceService service, CancellationToken ct) => TypedResults.Ok(await service.Plot(id, ct));
    private static async Task<CreatedAtRoute<ProductionPlotDto>> Create(ProductionPlotInput input, ProvenanceService service, CancellationToken ct)
    { var value = await service.CreatePlot(input, ct); return TypedResults.CreatedAtRoute(value, GetRoute, new { id = value.Id }); }
    private static async Task<Ok<ProductionPlotDto>> Update(Guid id, ProductionPlotUpdateInput input, ProvenanceService service, CancellationToken ct) => TypedResults.Ok(await service.UpdatePlot(id, input, ct));
    private static async Task<CreatedAtRoute<PlotRevisionDto>> CreateRevision(Guid plotId, PlotRevisionInput input, ProvenanceService service, CancellationToken ct)
    { var value = await service.CreateRevision(plotId, input, ct); return TypedResults.CreatedAtRoute(value, GetRevisionRoute, new { plotId, revisionId = value.Id }); }
    private static async Task<Ok<List<PlotRevisionSummaryDto>>> Revisions(Guid plotId, ProvenanceService service, CancellationToken ct) => TypedResults.Ok(await service.RevisionHistory(plotId, ct));
    private static async Task<Ok<PlotRevisionDto>> Revision(Guid plotId, Guid revisionId, ProvenanceService service, CancellationToken ct) => TypedResults.Ok(await service.Revision(plotId, revisionId, ct));
}
