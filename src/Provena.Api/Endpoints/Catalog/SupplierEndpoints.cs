using Microsoft.AspNetCore.Http.HttpResults;
using Provena.Api.Contracts;
using Provena.Api.Domain;
using Provena.Api.Services;
namespace Provena.Api.Endpoints;
internal static class SupplierEndpoints
{
    private const string GetRoute = "GetSupplier";
    public static void MapSupplierEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/suppliers").WithTags("Suppliers");
        group.MapGet("", List);
        group.MapGet("/{id:guid}", Get).WithName(GetRoute);
        group.MapPost("", Create);
        group.MapPut("/{id:guid}", Update);
        group.MapDelete("/{id:guid}", Delete);
    }
    private static async Task<Ok<List<Supplier>>> List(string? search, CatalogService service, CancellationToken ct) => TypedResults.Ok(await service.Suppliers(search, ct));
    private static async Task<Ok<Supplier>> Get(Guid id, CatalogService service, CancellationToken ct) => TypedResults.Ok(await service.Supplier(id, ct));
    private static async Task<CreatedAtRoute<Supplier>> Create(SupplierInput input, CatalogService service, CancellationToken ct)
    { var value = await service.SaveSupplier(null, input, ct); return TypedResults.CreatedAtRoute(value, GetRoute, new { id = value.Id }); }
    private static async Task<Ok<Supplier>> Update(Guid id, SupplierInput input, CatalogService service, CancellationToken ct) => TypedResults.Ok(await service.SaveSupplier(id, input, ct));
    private static async Task<NoContent> Delete(Guid id, CatalogService service, CancellationToken ct) { await service.DeleteSupplier(id, ct); return TypedResults.NoContent(); }
}
