namespace Provena.Api.Domain;

public enum QuantityUnit { Kg, Ton, Piece, Liter, Other }

public sealed class Shipment : TenantEntity
{
    public string ReferenceNumber { get; set; } = "";
    public Guid SupplierId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Quantity { get; set; }
    public QuantityUnit Unit { get; set; }
    public string? OriginCountryCode { get; set; }
    public DateOnly? ExpectedArrivalDate { get; set; }
    public DateOnly? ReceivedDate { get; set; }
    public string? Notes { get; set; }
}
