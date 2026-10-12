using Microsoft.AspNetCore.Http.HttpResults;
using Provena.Api.Contracts;
using Provena.Api.Services;
namespace Provena.Api.Endpoints;
internal static class ImportEndpoints
{
    public static void MapImportEndpoints(this RouteGroupBuilder api)
    {
        // Bearer-only API (no cookie auth), so antiforgery tokens do not apply to multipart uploads.
        var group = api.MapGroup("/import").WithTags("Imports").WithUploadLimits().DisableAntiforgery();
        group.MapPost("/suppliers", Suppliers);
        group.MapPost("/products", Products);
        group.MapPost("/shipments", Shipments);
    }
    private static async Task<Ok<ImportResult>> Suppliers(IFormFile file, ImportService service, CancellationToken ct) => TypedResults.Ok(await service.Import("suppliers", file, ct));
    private static async Task<Ok<ImportResult>> Products(IFormFile file, ImportService service, CancellationToken ct) => TypedResults.Ok(await service.Import("products", file, ct));
    private static async Task<Ok<ImportResult>> Shipments(IFormFile file, ImportService service, CancellationToken ct) => TypedResults.Ok(await service.Import("shipments", file, ct));
}
