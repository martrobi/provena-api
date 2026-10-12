using Microsoft.AspNetCore.Http.HttpResults;
using Provena.Api.Services;
namespace Provena.Api.Endpoints;
internal static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(this RouteGroupBuilder api) => api.MapGroup("/dashboard").WithTags("Dashboard").MapGet("", Get);
    private static async Task<Ok<object>> Get(CaseService service, CancellationToken ct) => TypedResults.Ok(await service.Dashboard(ct));
}
