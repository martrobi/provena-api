using Microsoft.AspNetCore.Http.HttpResults;
using Provena.Api.Contracts;
using Provena.Api.Domain;
using Provena.Api.Services;
namespace Provena.Api.Endpoints;
internal static class PublicSupplierEndpoints
{
    // Anonymous at JWT level: the services validate the capability token and scope access to one request/case.
    // No cookie auth exists, so antiforgery tokens do not apply to multipart uploads.
    public static void MapPublicSupplierEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/public/supplier-requests/{token}").WithTags("Public supplier API")
            .AllowAnonymous().RequireRateLimiting("supplier").WithUploadLimits().DisableAntiforgery().WithRequiredJsonBodyContentTypeCheck();
        group.MapGet("", Get);
        group.MapPut("/details", Details);
        group.MapPost("/documents", Document);
        group.MapPost("/geolocation", Geo);
        group.MapPost("/complete", Complete);
    }
    private static async Task<Ok<object>> Get(string token, SupplierRequestService service, CancellationToken ct) => TypedResults.Ok(await service.PublicDetails(token, ct));
    private static async Task<NoContent> Details(string token, SubmissionInput input, SupplierRequestService service, CancellationToken ct)
    { await service.SaveDetails(token, input, ct); return TypedResults.NoContent(); }
    private static async Task<Ok<object>> Document(string token, IFormFile file, CaseFileService files, CancellationToken ct)
        => TypedResults.Ok(await files.Upload(null, token, file, DocumentCategory.SupportingDocument, false, ct));
    private static async Task<Ok<object>> Geo(string token, IFormFile file, CaseFileService files, CancellationToken ct)
        => TypedResults.Ok(await files.Upload(null, token, file, DocumentCategory.Other, true, ct));
    private static async Task<NoContent> Complete(string token, SupplierRequestService service, CancellationToken ct) { await service.Complete(token, ct); return TypedResults.NoContent(); }
}
