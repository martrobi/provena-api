using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http.HttpResults;
using Provena.Api.Contracts;
using Provena.Api.Services;
namespace Provena.Api.Endpoints;
// Working case facts (ADR 0008): master-data copy, applicability, recipients and evidence links. All routes are internal and authenticated;
// the route case is resolved (404) before any body is examined. No DELETE except the semantic evidence-link withdrawal.
internal static class CaseFactEndpoints
{
    public static void MapCaseFactEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/eudr-cases/{caseId:guid}");
        var masterData = group.MapGroup("").WithTags("MasterData");
        masterData.MapGet("/master-data", MasterData);
        masterData.MapPost("/master-data/refresh", Refresh);
        masterData.MapGet("/master-data-history", MasterDataHistory);
        var applicability = group.MapGroup("").WithTags("Applicability");
        applicability.MapGet("/applicability", Applicability);
        applicability.MapPut("/applicability", SaveApplicability).WithManuallyReadJsonBody<CaseApplicabilityInput, RouteHandlerBuilder>("expectedCurrentApplicabilityId"); // null when none existed
        applicability.MapGet("/applicability-history", ApplicabilityHistory);
        var recipients = group.MapGroup("").WithTags("Recipients");
        recipients.MapGet("/recipients", Recipients);
        recipients.MapPut("/recipients", SaveRecipients);
        recipients.MapGet("/recipients-history", RecipientsHistory);
        var links = group.MapGroup("/evidence-links").WithTags("EvidenceLinks");
        links.MapGet("", Links);
        links.MapPost("", CreateLink);
        links.MapDelete("/{linkId:guid}", WithdrawLink);
        group.MapGroup("").WithTags("Completeness").MapGet("/completeness", Completeness);
    }
    // Read-only current-working-dossier completeness (policy v1). 404 for an unknown or foreign case; it writes nothing.
    private static async Task<Ok<CompletenessDto>> Completeness(Guid caseId, CompletenessService service, CancellationToken ct) => TypedResults.Ok(await service.Get(caseId, ct));
    // No copy / no record yet is 204; an unknown or foreign case is 404.
    private static async Task<Results<Ok<CaseMasterDataDto>, NoContent>> MasterData(Guid caseId, CaseMasterDataService service, CancellationToken ct)
        => await service.Current(caseId, ct) is { } value ? TypedResults.Ok(value) : TypedResults.NoContent();
    private static async Task<Ok<CaseMasterDataDto>> Refresh(Guid caseId, CaseMasterDataService service, CancellationToken ct) => TypedResults.Ok(await service.Refresh(caseId, ct));
    private static async Task<Ok<List<CaseMasterDataVersionDto>>> MasterDataHistory(Guid caseId, CaseMasterDataService service, CancellationToken ct) => TypedResults.Ok(await service.History(caseId, ct));

    private static async Task<Results<Ok<CaseApplicabilityDto>, NoContent>> Applicability(Guid caseId, CaseApplicabilityService service, CancellationToken ct)
        => await service.Current(caseId, ct) is { } value ? TypedResults.Ok(value) : TypedResults.NoContent();
    // Replacement command, Owner-only: the body is the complete assessment. The handler reads it AFTER the live Owner check inside the service (a Member gets 403 whatever the case id or body).
    private static async Task<Ok<CaseApplicabilityDto>> SaveApplicability(Guid caseId, HttpRequest request, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, CaseApplicabilityService service, CancellationToken ct)
        => TypedResults.Ok(await service.Save(caseId, CaseLifecycleEndpoints.Body<CaseApplicabilityInput>(request, json, "an applicability determination"), ct));
    private static async Task<Ok<List<CaseApplicabilityVersionDto>>> ApplicabilityHistory(Guid caseId, CaseApplicabilityService service, CancellationToken ct) => TypedResults.Ok(await service.History(caseId, ct));

    private static async Task<Results<Ok<CaseRecipientStatementDto>, NoContent>> Recipients(Guid caseId, CaseRecipientService service, CancellationToken ct)
        => await service.Current(caseId, ct) is { } value ? TypedResults.Ok(value) : TypedResults.NoContent();
    private static async Task<Ok<CaseRecipientStatementDto>> SaveRecipients(Guid caseId, CaseRecipientsInput input, CaseRecipientService service, CancellationToken ct) => TypedResults.Ok(await service.Save(caseId, input, ct));
    private static async Task<Ok<List<CaseRecipientStatementDto>>> RecipientsHistory(Guid caseId, CaseRecipientService service, CancellationToken ct) => TypedResults.Ok(await service.History(caseId, ct));

    private static async Task<Ok<List<CaseEvidenceLinkDto>>> Links(Guid caseId, CaseEvidenceLinkService service, CancellationToken ct) => TypedResults.Ok(await service.List(caseId, ct));
    private static async Task<Created<CaseEvidenceLinkDto>> CreateLink(Guid caseId, CaseEvidenceLinkInput input, CaseEvidenceLinkService service, CancellationToken ct)
    => TypedResults.Created((string?)null, await service.Create(caseId, input, ct)); // 201 without Location: there is no GET-by-id route, so the body carries the link.
    // Semantic withdrawal (the row is retained); an already withdrawn link is an idempotent 204.
    private static async Task<NoContent> WithdrawLink(Guid caseId, Guid linkId, CaseEvidenceLinkService service, CancellationToken ct) { await service.Withdraw(caseId, linkId, ct); return TypedResults.NoContent(); }
}
