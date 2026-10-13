using Microsoft.AspNetCore.Http.HttpResults;
using Provena.Api.Contracts;
using Provena.Api.Services;
namespace Provena.Api.Endpoints;
internal static class OperatorProfileEndpoints
{
    public static void MapOperatorProfileEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/operator-profile").WithTags("OperatorProfile");
        group.MapGet("", Get);
        group.MapPut("", Save);
    }
    // No profile yet is 204 (as for geolocation).
    private static async Task<Results<Ok<OperatorProfileDto>, NoContent>> Get(OperatorProfileService service, CancellationToken ct)
        => await service.Get(ct) is { } profile ? TypedResults.Ok(profile) : TypedResults.NoContent();
    // Upsert: the body is the complete profile. There is no DELETE.
    private static async Task<Ok<OperatorProfileDto>> Save(OperatorProfileInput input, OperatorProfileService service, CancellationToken ct) => TypedResults.Ok(await service.Save(input, ct));
}
