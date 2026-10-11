namespace Provena.Api.Domain;

public sealed class SupplierRequest : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public Guid SupplierId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? SentAtUtc { get; set; }
    public DateTimeOffset? OpenedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
}
