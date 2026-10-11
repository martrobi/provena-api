namespace Provena.Api.Domain;

public enum DocumentCategory { SupportingDocument, SupplierDeclaration, Certificate, Invoice, Other }

public enum UploadedByType { InternalUser, Supplier }

public sealed class CaseDocument : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public Guid? SupplierRequestId { get; set; }
    public string OriginalFileName { get; set; } = "";
    public string StoredFileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public DocumentCategory Category { get; set; }
    public string StorageKey { get; set; } = "";
    public UploadedByType UploadedByType { get; set; }
    public DateTimeOffset UploadedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public enum GeoValidationStatus { Valid, Invalid }

public sealed class GeoSubmission : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public Guid? SupplierRequestId { get; set; }
    public string OriginalFileName { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public string RawGeoJson { get; set; } = "{}";
    public int FeatureCount { get; set; }
    public string GeometryTypes { get; set; } = "";
    public GeoValidationStatus ValidationStatus { get; set; }
    public string? ValidationMessage { get; set; }
    public DateTimeOffset UploadedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
