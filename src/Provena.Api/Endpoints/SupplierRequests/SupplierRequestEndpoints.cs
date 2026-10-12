using Microsoft.AspNetCore.Http.HttpResults;
using Provena.Api.Contracts;
using Provena.Api.Services;
namespace Provena.Api.Endpoints;
internal static class SupplierRequestEndpoints
{
    public static void MapSupplierRequestEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/supplier-requests").WithTags("SupplierRequests");
        group.MapPost("/{id:guid}/revoke", Revoke);
        group.MapPost("/{id:guid}/regenerate", Regenerate);
    }
    private static async Task<NoContent> Revoke(Guid id, SupplierRequestService service, CancellationToken ct) { await service.Revoke(id, ct); return TypedResults.NoContent(); }
    private static async Task<Ok<RequestLink>> Regenerate(Guid id, SupplierRequestService service, CancellationToken ct) => TypedResults.Ok(await service.Regenerate(id, ct));
}
