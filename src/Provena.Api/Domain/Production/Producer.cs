namespace Provena.Api.Domain;

// Stable producer identity. Not a Supplier and never inferred from one (ADR 0002/0006). Never deleted; deactivate instead.
public sealed class Producer : TenantEntity
{
    public string Name { get; set; } = "";
    public string? ExternalReference { get; set; }
    // The producer's own country; the country of production lives on each PlotRevision.
    public string? CountryCode { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? CreatedByUserId { get; set; }
}
