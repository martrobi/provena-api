using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Provena.Api.Auth;
using Provena.Api.Contracts;
namespace Provena.Api.Controllers;
[ApiController, Route("api/auth")]
public sealed class AuthController(AuthService auth) : ControllerBase
{
    [HttpPost("register"), AllowAnonymous, EnableRateLimiting("auth")]
    public Task<TokenResponse> Register(RegisterRequest request, CancellationToken ct) => auth.Register(request, ct);
    [HttpPost("login"), AllowAnonymous, EnableRateLimiting("auth")]
    public Task<TokenResponse> Login(LoginRequest request, CancellationToken ct) => auth.Login(request, ct);
    [HttpGet("me"), Authorize]
    public Task<object> Me(CancellationToken ct) => auth.Me(ct);
}
