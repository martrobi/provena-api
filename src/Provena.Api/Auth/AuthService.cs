using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;

namespace Provena.Api.Auth;

public sealed class AuthService(
    AppDbContext db,
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn,
    IValidator<RegisterRequest> registerValidator,
    IValidator<LoginRequest> loginValidator,
    IConfiguration config,
    TenantContext tenant,
    ILogger<AuthService> logger
)
{
    public async Task<TokenResponse> Register(RegisterRequest input, CancellationToken ct)
    {
        await registerValidator.ValidateAndThrowAsync(input, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = input.Email.Trim(),
            Email = input.Email.Trim(),
            FirstName = input.FirstName.Trim(),
            LastName = input.LastName.Trim(),
        };
        var result = await users.CreateAsync(user, input.Password);
        if (!result.Succeeded)
            throw new ApiException(
                400,
                "Registration failed.",
                result
                    .Errors.GroupBy(x => x.Code)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.Description).ToArray())
            );
        var org = new Organization { Name = input.OrganizationName.Trim(), Oib = input.Oib };
        db.Organizations.Add(org);
        db.OrganizationUsers.Add(
            new OrganizationUser
            {
                OrganizationId = org.Id,
                UserId = user.Id,
                Role = OrganizationRole.Owner,
            }
        );
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Account registered {UserId}", user.Id);
        return Issue(user, org.Id, OrganizationRole.Owner);
    }

    public async Task<TokenResponse> Login(LoginRequest input, CancellationToken ct)
    {
        await loginValidator.ValidateAndThrowAsync(input, ct);
        var user = await users.FindByEmailAsync(input.Email.Trim());
        if (
            user == null
            || !(
                await signIn.CheckPasswordSignInAsync(user, input.Password, lockoutOnFailure: true)
            ).Succeeded
        )
        {
            logger.LogWarning("Failed authentication attempt");
            throw new ApiException(401, "Invalid email or password.");
        }
        var membership = await db.OrganizationUsers.SingleAsync(x => x.UserId == user.Id, ct);
        logger.LogInformation("Successful authentication {UserId}", user.Id);
        return Issue(user, membership.OrganizationId, membership.Role);
    }

    public async Task<object> Me(CancellationToken ct)
    {
        var user =
            await users.FindByIdAsync(tenant.UserId?.ToString() ?? "")
            ?? throw new ApiException(401, "Session is no longer valid.");
        var membership =
            await db.OrganizationUsers.SingleOrDefaultAsync(
                x => x.UserId == user.Id && x.OrganizationId == tenant.OrganizationId,
                ct
            ) ?? throw new ApiException(403, "Organization membership required.");
        var org = await db.Organizations.SingleAsync(x => x.Id == membership.OrganizationId, ct);
        return new
        {
            user.Id,
            user.Email,
            user.FirstName,
            user.LastName,
            organization = new { org.Id, org.Name },
            role = membership.Role,
        };
    }

    private TokenResponse Issue(ApplicationUser user, Guid organizationId, OrganizationRole role)
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(
            config.GetValue("Jwt:AccessTokenMinutes", 60)
        );
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Secret"]!));
        var jwt = new JwtSecurityToken(
            config["Jwt:Issuer"],
            config["Jwt:Audience"],
            [
                new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new("organization_id", organizationId.ToString()),
                new(ClaimTypes.Role, role.ToString()),
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            ],
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        );
        return new(new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }
}
