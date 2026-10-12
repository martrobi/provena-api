namespace Provena.Api.Endpoints;
public static class ProvenaEndpoints
{
    // Single registration point for application endpoints. Internal routes inherit authorization from the /api group;
    // only explicit AllowAnonymous endpoints (login/register) opt out. Public capability routes live outside /api.
    public static IEndpointRouteBuilder MapProvenaEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithRequiredJsonBodyContentTypeCheck();
        api.MapAuthEndpoints();
        api.MapSupplierEndpoints(); api.MapProductEndpoints(); api.MapShipmentEndpoints();
        api.MapCaseEndpoints(); api.MapCaseFileEndpoints(); api.MapDashboardEndpoints();
        api.MapSupplierRequestEndpoints(); api.MapImportEndpoints();
        app.MapPublicSupplierEndpoints();
        return app;
    }
}
