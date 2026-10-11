namespace Provena.Api.Domain;

public sealed class Supplier : TenantEntity
{
    public string Name { get; set; } = "";
    public string? ExternalReference { get; set; }
    public string? Email { get; set; }
    public string? ContactPerson { get; set; }
    public string? CountryCode { get; set; }
    public string? Notes { get; set; }
}
