namespace Provena.Api.Domain;

public enum CommodityType { Coffee, Cocoa, Wood, Rubber, Soy, OilPalm, Cattle, Other }

public sealed class Product : TenantEntity
{
    public string Name { get; set; } = "";
    public string? ExternalReference { get; set; }
    public string CnCode { get; set; } = "";
    public CommodityType CommodityType { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}
