using System.Security.Claims;
namespace Provena.Api.Auth;
public sealed class TenantContext(IHttpContextAccessor accessor)
{
    private Guid? publicOrganization;
    public Guid OrganizationId => publicOrganization ??
        (Guid.TryParse(accessor.HttpContext?.User.FindFirstValue("organization_id"), out var id) ? id : Guid.Empty);
    public Guid? UserId => Guid.TryParse(accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    // Called only after a hashed public token has been validated, or by development seeding.
    public void SetOrganization(Guid id) => publicOrganization = id;
}
