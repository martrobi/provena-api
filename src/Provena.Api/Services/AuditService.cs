using Provena.Api.Auth;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;
public sealed class AuditService(AppDbContext db, TenantContext tenant, ILogger<AuditService> logger)
{
    public void Add(Guid? caseId, string eventType, string description, Guid? requestId = null)
    {
        db.AuditEvents.Add(new AuditEvent { OrganizationId = tenant.OrganizationId, EudrCaseId = caseId, ActorType = requestId.HasValue ? ActorType.Supplier : tenant.UserId.HasValue ? ActorType.InternalUser : ActorType.System, ActorUserId = requestId.HasValue ? null : tenant.UserId, SupplierRequestId = requestId, EventType = eventType, Description = description });
        logger.LogInformation("{EventType} for case {CaseId} in organization {OrganizationId}", eventType, caseId, tenant.OrganizationId);
    }
}
