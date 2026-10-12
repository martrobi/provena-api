using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Provena.Api.Contracts;
using Provena.Api.Domain;
using Provena.Api.Services;
namespace Provena.Api.Endpoints;
internal static class CaseFileEndpoints
{
    public static void MapCaseFileEndpoints(this RouteGroupBuilder api)
    {
        // Bearer-only API (no cookie auth), so antiforgery tokens do not apply to multipart uploads.
        var group = api.MapGroup("/eudr-cases/{caseId:guid}").WithTags("Files").WithUploadLimits().DisableAntiforgery();
        group.MapPost("/documents", Upload);
        group.MapGet("/documents", List);
        group.MapGet("/documents/{documentId:guid}", Download);
        group.MapDelete("/documents/{documentId:guid}", Delete);
        group.MapPost("/geolocation", GeoUpload);
        group.MapGet("/geolocation", Geo);
        group.MapGet("/geolocation/{geolocationId:guid}/file", GeoFile);
        group.MapGet("/evidence-history", History);
    }
    // Missing category keeps the previous default (SupportingDocument); undefined values are rejected by the service.
    private static async Task<Ok<object>> Upload(Guid caseId, IFormFile file, [FromForm] string? category, CaseFileService service, CancellationToken ct)
        => TypedResults.Ok(await service.Upload(caseId, null, file, EndpointConventions.ParseEnum<DocumentCategory>(category, nameof(category)) ?? default, false, ct));
    private static async Task<Ok<List<DocumentDto>>> List(Guid caseId, CaseFileService service, CancellationToken ct) => TypedResults.Ok(await service.List(caseId, ct));
    private static async Task<FileStreamHttpResult> Download(Guid caseId, Guid documentId, HttpResponse response, CaseFileService service, CancellationToken ct)
    { var d = await service.Download(caseId, documentId, ct); response.Headers["X-Content-Type-Options"] = "nosniff"; return TypedResults.File(d.Stream, d.ContentType, d.FileName); }
    private static async Task<NoContent> Delete(Guid caseId, Guid documentId, CaseFileService service, CancellationToken ct)
    { await service.Delete(caseId, documentId, ct); return TypedResults.NoContent(); }
    private static async Task<Ok<object>> GeoUpload(Guid caseId, IFormFile file, CaseFileService service, CancellationToken ct)
        => TypedResults.Ok(await service.Upload(caseId, null, file, DocumentCategory.Other, true, ct));
    // No geolocation yet is 204, as before.
    private static async Task<Results<Ok<GeoDto>, NoContent>> Geo(Guid caseId, CaseFileService service, CancellationToken ct)
        => await service.Geo(caseId, ct) is { } geo ? TypedResults.Ok(geo) : TypedResults.NoContent();
    // Internal read-only retained-evidence history (no public token equivalent).
    private static async Task<Ok<EvidenceHistory>> History(Guid caseId, CaseFileService service, CancellationToken ct) => TypedResults.Ok(await service.History(caseId, ct));
    private static async Task<FileStreamHttpResult> GeoFile(Guid caseId, Guid geolocationId, HttpResponse response, CaseFileService service, CancellationToken ct)
    { var g = await service.GeoFile(caseId, geolocationId, ct); response.Headers["X-Content-Type-Options"] = "nosniff"; return TypedResults.File(g.Stream, "application/geo+json", g.FileName); }
}
