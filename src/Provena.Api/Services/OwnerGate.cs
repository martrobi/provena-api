using Microsoft.EntityFrameworkCore;
using Provena.Api.Auth;
using Provena.Api.Common;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;

// The live Owner check shared by every Owner-only write (reviews and the case lifecycle). It queries OrganizationUsers (user + organization + role Owner) inside the caller's
// transaction, never trusts the JWT role or a cached claim (a removed or demoted Owner is denied at once) and must run BEFORE any case lookup and before the request body is read,
// so a Member learns nothing about a case: 403 for an existing, nonexistent or foreign case and for an invalid body alike.
public sealed class OwnerGate(AppDbContext db, TenantContext tenant)
{
    public async Task Require(string message, CancellationToken ct)
    {
        var userId = tenant.UserId;
        if (userId == null || tenant.OrganizationId == Guid.Empty || !await db.OrganizationUsers.AsNoTracking().AnyAsync(x => x.UserId == userId && x.OrganizationId == tenant.OrganizationId && x.Role == OrganizationRole.Owner, ct))
            throw new ApiException(403, message);
    }
}
