using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Services;
namespace Provena.Api.Endpoints;
// Reviewed dossier history (ADR 0008). Internal and authenticated. POST is Owner-only (live membership check inside ReviewService, before any case lookup);
// the reads and the reviewed export are available to any member. A review never changes requirements or the case status and is not a compliance determination.
internal static class ReviewEndpoints
{
    private const string GetRoute = "GetReview";
    public static void MapReviewEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/eudr-cases/{caseId:guid}/reviews").WithTags("Reviews");
        // The body is read by the handler AFTER the Owner check (OpenAPI advertises it through the ManuallyReadJsonBody metadata), so that a Member gets 403
        // whatever the body contains; the framework would otherwise reject a malformed body with 400 before the handler runs.
        group.MapPost("", Create).WithManuallyReadJsonBody<CaseReviewInput, RouteHandlerBuilder>("expectedLatestReviewId"); // optional and nullable: omitted or null means "no review existed when loaded"
        group.MapGet("", List);
        group.MapGet("/{reviewId:guid}", Get).WithName(GetRoute);
        group.MapGet("/{reviewId:guid}/export", Export);
    }
    // 201 with a Location that targets the GET-by-id route below.
    private static async Task<CreatedAtRoute<ReviewDetailDto>> Create(Guid caseId, HttpRequest request, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, ReviewService service, CancellationToken ct)
    {
        var review = await service.Create(caseId, async token =>
        {
            if (!request.HasJsonContentType()) throw new BadHttpRequestException("Expected a JSON request body with Content-Type application/json.", StatusCodes.Status415UnsupportedMediaType);
            try { return await request.ReadFromJsonAsync<CaseReviewInput>(json.Value.SerializerOptions, token); }
            catch (JsonException) { throw new ApiException(400, "The request body is not valid JSON for a review."); }
        }, ct);
        return TypedResults.CreatedAtRoute(review, GetRoute, new { caseId, reviewId = review.ReviewId });
    }
    private static async Task<Ok<List<ReviewSummaryDto>>> List(Guid caseId, ReviewReadService service, CancellationToken ct) => TypedResults.Ok(await service.List(caseId, ct));
    private static async Task<Ok<ReviewDetailDto>> Get(Guid caseId, Guid reviewId, ReviewReadService service, CancellationToken ct) => TypedResults.Ok(await service.Get(caseId, reviewId, ct));
    private static async Task<FileStreamHttpResult> Export(Guid caseId, Guid reviewId, ReviewedExportService exports, CancellationToken ct)
    { var file = await exports.Export(caseId, reviewId, ct); return TypedResults.File(file.Stream, "application/zip", file.FileName); }
}
