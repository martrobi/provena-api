using Microsoft.AspNetCore.Http.HttpResults;
using Provena.Api.Contracts;
using Provena.Api.Domain;
using Provena.Api.Services;
namespace Provena.Api.Endpoints;
internal static class ProductEndpoints
{
    private const string GetRoute = "GetProduct";
    public static void MapProductEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/products").WithTags("Products");
        group.MapGet("", List);
        group.MapGet("/{id:guid}", Get).WithName(GetRoute);
        group.MapPost("", Create);
        group.MapPut("/{id:guid}", Update);
        group.MapDelete("/{id:guid}", Delete);
    }
    private static async Task<Ok<PageResult<Product>>> List(string? search, CatalogService service, CancellationToken ct, int page = 1, int pageSize = 25) => TypedResults.Ok(await service.Products(search, page, pageSize, ct));
    private static async Task<Ok<Product>> Get(Guid id, CatalogService service, CancellationToken ct) => TypedResults.Ok(await service.Product(id, ct));
    private static async Task<CreatedAtRoute<Product>> Create(ProductInput input, CatalogService service, CancellationToken ct)
    { var value = await service.SaveProduct(null, input, ct); return TypedResults.CreatedAtRoute(value, GetRoute, new { id = value.Id }); }
    private static async Task<Ok<Product>> Update(Guid id, ProductInput input, CatalogService service, CancellationToken ct) => TypedResults.Ok(await service.SaveProduct(id, input, ct));
    private static async Task<NoContent> Delete(Guid id, CatalogService service, CancellationToken ct) { await service.DeleteProduct(id, ct); return TypedResults.NoContent(); }
}
