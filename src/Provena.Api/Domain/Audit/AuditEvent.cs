namespace Provena.Api.Domain;

public enum ActorType { InternalUser, Supplier, System }

public sealed class AuditEvent : TenantEntity
{
    public Guid? EudrCaseId { get; set; }
    public ActorType ActorType { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? SupplierRequestId { get; set; }
    public string EventType { get; set; } = "";
    public string Description { get; set; } = "";
    public string? MetadataJson { get; set; }
}
