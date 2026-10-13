using Microsoft.AspNetCore.Http.HttpResults;
using Provena.Api.Contracts;
using Provena.Api.Services;
namespace Provena.Api.Endpoints;
internal static class CaseDossierEndpoints
{
    public static void MapCaseDossierEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/eudr-cases/{caseId:guid}/dossier").WithTags("Dossier");
        group.MapGet("", Get);
        group.MapPut("/lot", SaveLot);
        group.MapGet("/lot-history", History);
    }
    // No dossier yet is 204 (as for geolocation); an unknown or foreign case is 404.
    private static async Task<Results<Ok<CaseDossierDto>, NoContent>> Get(Guid caseId, CaseDossierService service, CancellationToken ct)
        => await service.Dossier(caseId, ct) is { } dossier ? TypedResults.Ok(dossier) : TypedResults.NoContent();
    // Replacement command: the body is the complete lot. The route case is resolved and locked before the body is validated.
    private static async Task<Ok<CaseDossierDto>> SaveLot(Guid caseId, CaseLotInput input, CaseDossierService service, CancellationToken ct) => TypedResults.Ok(await service.SaveLot(caseId, input, ct));
    private static async Task<Ok<List<CaseLotDto>>> History(Guid caseId, CaseDossierService service, CancellationToken ct) => TypedResults.Ok(await service.History(caseId, ct));
}
