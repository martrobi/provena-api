using Microsoft.AspNetCore.Http.HttpResults;
using Provena.Api.Auth;
using Provena.Api.Contracts;
namespace Provena.Api.Endpoints;
internal static class AuthEndpoints
{
    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/auth").WithTags("Auth");
        group.MapPost("/register", Register).AllowAnonymous().RequireRateLimiting("auth");
        group.MapPost("/login", Login).AllowAnonymous().RequireRateLimiting("auth");
        group.MapGet("/me", Me);
    }
    private static async Task<Ok<TokenResponse>> Register(RegisterRequest request, AuthService auth, CancellationToken ct) => TypedResults.Ok(await auth.Register(request, ct));
    private static async Task<Ok<TokenResponse>> Login(LoginRequest request, AuthService auth, CancellationToken ct) => TypedResults.Ok(await auth.Login(request, ct));
    private static async Task<Ok<object>> Me(AuthService auth, CancellationToken ct) => TypedResults.Ok(await auth.Me(ct));
}
