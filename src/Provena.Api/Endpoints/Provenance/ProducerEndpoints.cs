using Microsoft.AspNetCore.Http.HttpResults;
using Provena.Api.Contracts;
using Provena.Api.Services;
namespace Provena.Api.Endpoints;
internal static class ProducerEndpoints
{
    private const string GetRoute = "GetProducer";
    public static void MapProducerEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/producers").WithTags("Producers");
        group.MapGet("", List);
        group.MapGet("/{id:guid}", Get).WithName(GetRoute);
        group.MapPost("", Create);
        group.MapPut("/{id:guid}", Update);
    }
    private static async Task<Ok<PageResult<ProducerDto>>> List(string? search, bool? isActive, ProvenanceService service, CancellationToken ct, int page = 1, int pageSize = 25) => TypedResults.Ok(await service.Producers(search, isActive, page, pageSize, ct));
    private static async Task<Ok<ProducerDto>> Get(Guid id, ProvenanceService service, CancellationToken ct) => TypedResults.Ok(await service.Producer(id, ct));
    private static async Task<CreatedAtRoute<ProducerDto>> Create(ProducerInput input, ProvenanceService service, CancellationToken ct)
    { var value = await service.SaveProducer(null, input, ct); return TypedResults.CreatedAtRoute(value, GetRoute, new { id = value.Id }); }
    private static async Task<Ok<ProducerDto>> Update(Guid id, ProducerInput input, ProvenanceService service, CancellationToken ct) => TypedResults.Ok(await service.SaveProducer(id, input, ct));
}
