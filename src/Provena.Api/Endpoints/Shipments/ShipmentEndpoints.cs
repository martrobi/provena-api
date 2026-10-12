using Microsoft.AspNetCore.Http.HttpResults;
using Provena.Api.Contracts;
using Provena.Api.Domain;
using Provena.Api.Services;
namespace Provena.Api.Endpoints;
internal static class ShipmentEndpoints
{
    public static void MapShipmentEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/shipments").WithTags("Shipments");
        group.MapGet("", List);
        group.MapGet("/{id:guid}", Get);
        group.MapPost("", Create);
        group.MapPut("/{id:guid}", Update);
        // Archival is deliberately used even before the first request, preserving append-only audit history.
        group.MapDelete("/{id:guid}", Archive);
    }
    private static async Task<Ok<PageResult<Shipment>>> List(ShipmentService service, CancellationToken ct, int page = 1, int pageSize = 25) => TypedResults.Ok(await service.List(page, pageSize, ct));
    private static async Task<Ok<object>> Get(Guid id, ShipmentService service, CancellationToken ct) => TypedResults.Ok(await service.Get(id, ct));
    // 201 without Location: the body carries both the shipment and its EUDR case ID.
    private static async Task<Created<object>> Create(ShipmentInput input, ShipmentService service, CancellationToken ct) => TypedResults.Created((string?)null, await service.Save(null, input, ct));
    private static async Task<Ok<object>> Update(Guid id, ShipmentInput input, ShipmentService service, CancellationToken ct) => TypedResults.Ok(await service.Save(id, input, ct));
    private static async Task<NoContent> Archive(Guid id, ShipmentService service, CancellationToken ct) { await service.Archive(id, ct); return TypedResults.NoContent(); }
}
