namespace Provena.Api.Domain;

// Case-owned copy of the operator, supplier and product values a case relies on. Immutable except the one-way SupersededAtUtc retirement;
// at most one current version per case. Shared catalogue edits never change it: a deliberate, audited refresh creates the next version.
// SupplierId/ProductId are taken from the case's CURRENT shipment under the case lock, never from a client. Operator* are null when the
// organization has no (or a partial) profile. ProductName and ProductCatalogueDescription are display context only: they take part in
// refresh no-op detection but never in drift or in any later fact comparison.
public sealed class CaseMasterDataVersion : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public int Number { get; set; }
    public string? OperatorLegalName { get; set; }
    public string? OperatorPostalAddress { get; set; }
    public string? OperatorEmail { get; set; }
    public string? OperatorEstablishmentCountryCode { get; set; }
    public string? OperatorEori { get; set; }
    public Guid SupplierId { get; set; }
    public string SupplierName { get; set; } = "";
    public string? SupplierPostalAddress { get; set; }
    public string? SupplierEmail { get; set; }
    public string? SupplierCountryCode { get; set; }
    public Guid ProductId { get; set; }
    public string ProductCnCode { get; set; } = "";
    public CommodityType ProductCommodity { get; set; }
    public string ProductName { get; set; } = "";
    public string? ProductCatalogueDescription { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? SupersededAtUtc { get; set; }
}
