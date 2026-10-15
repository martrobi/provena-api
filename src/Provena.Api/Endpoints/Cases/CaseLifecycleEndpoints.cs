using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Services;
namespace Provena.Api.Endpoints;

// Case Lifecycle v2: close, reopen, archive and unarchive (all Owner-only) and the lifecycle history (any member). The four POST handlers read their JSON body themselves, AFTER the
// live Owner check inside the service (advertised in OpenAPI through WithManuallyReadJsonBody), so a Member gets 403 whatever the case id or body and the framework cannot reject a
// malformed body first. Closing never submits a DDS declaration and is not a legal-compliance decision.
internal static class CaseLifecycleEndpoints
{
    private const string LifecycleRoute = "GetCaseLifecycle";
    public static void MapCaseLifecycleEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/eudr-cases/{caseId:guid}").WithTags("Lifecycle");
        group.MapPost("/close", Close).WithManuallyReadJsonBody<CloseCaseInput, RouteHandlerBuilder>("externalDdsReference"); // optional legacy value: rejected for other outcomes, and for ExternalDdsCompleted it must equal the current ExternalDdsRecord reference
        group.MapPost("/reopen", Reopen).WithManuallyReadJsonBody<ReopenCaseInput, RouteHandlerBuilder>();
        group.MapPost("/archive", Archive).WithManuallyReadJsonBody<ArchiveCaseInput, RouteHandlerBuilder>();
        group.MapPost("/unarchive", Unarchive).WithManuallyReadJsonBody<UnarchiveCaseInput, RouteHandlerBuilder>();
        group.MapGet("/lifecycle", Lifecycle).WithName(LifecycleRoute);
    }

    internal static Func<CancellationToken, Task<T?>> Body<T>(HttpRequest request, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, string what) where T : class => async token =>
    {
        if (!request.HasJsonContentType()) throw new BadHttpRequestException("Expected a JSON request body with Content-Type application/json.", StatusCodes.Status415UnsupportedMediaType);
        try { return await request.ReadFromJsonAsync<T>(json.Value.SerializerOptions, token); }
        catch (JsonException) { throw new ApiException(400, $"The request body is not valid JSON for {what}."); }
    };

    // 201 with a Location that targets the lifecycle history.
    private static async Task<CreatedAtRoute<CaseClosureDto>> Close(Guid caseId, HttpRequest request, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, CaseLifecycleService service, CancellationToken ct)
        => TypedResults.CreatedAtRoute(await service.Close(caseId, Body<CloseCaseInput>(request, json, "closing a case"), ct), LifecycleRoute, new { caseId });
    private static async Task<CreatedAtRoute<CaseReopeningDto>> Reopen(Guid caseId, HttpRequest request, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, CaseLifecycleService service, CancellationToken ct)
        => TypedResults.CreatedAtRoute(await service.Reopen(caseId, Body<ReopenCaseInput>(request, json, "reopening a case"), ct), LifecycleRoute, new { caseId });
    private static async Task<CreatedAtRoute<CaseArchivalDto>> Archive(Guid caseId, HttpRequest request, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, CaseLifecycleService service, CancellationToken ct)
        => TypedResults.CreatedAtRoute(await service.Archive(caseId, Body<ArchiveCaseInput>(request, json, "archiving a case"), ct), LifecycleRoute, new { caseId });
    private static async Task<Ok<CaseArchivalDto>> Unarchive(Guid caseId, HttpRequest request, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, CaseLifecycleService service, CancellationToken ct)
        => TypedResults.Ok(await service.Unarchive(caseId, Body<UnarchiveCaseInput>(request, json, "unarchiving a case"), ct));
    private static async Task<Ok<CaseLifecycleDto>> Lifecycle(Guid caseId, CaseLifecycleService service, CancellationToken ct) => TypedResults.Ok(await service.Get(caseId, ct));
}
