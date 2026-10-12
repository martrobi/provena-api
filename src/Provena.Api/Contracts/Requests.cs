using Provena.Api.Domain;
namespace Provena.Api.Contracts;
public sealed record RegisterRequest(string Email, string Password, string FirstName, string LastName, string OrganizationName, string? Oib);
public sealed record LoginRequest(string Email, string Password);
public sealed record SupplierInput(string Name, string? ExternalReference, string? Email, string? ContactPerson, string? CountryCode, string? Notes);
public sealed record ProductInput(string Name, string? ExternalReference, string CnCode, CommodityType CommodityType, string? Description, bool IsActive = true);
public sealed record ShipmentInput(string ReferenceNumber, Guid SupplierId, Guid ProductId, decimal Quantity, QuantityUnit Unit, string? OriginCountryCode, DateOnly? ExpectedArrivalDate, DateOnly? ReceivedDate, string? Notes);
public sealed record SubmissionInput(string? CountryOfProductionCode, DateOnly? ProductionPeriodFrom, DateOnly? ProductionPeriodTo, decimal? ConfirmedQuantity, string? Notes);
public sealed record ReviewInput(RequirementStatus Status, string? ReviewerNote);
public sealed record CompleteInput(string? ExternalDdsReference);
public sealed record PageResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
public sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAtUtc);
public sealed record RequestLink(Guid RequestId, string PublicUrl, DateTimeOffset ExpiresAtUtc);
public sealed record ImportError(int Row, string Message);
public sealed record ImportResult(int TotalRows, int Imported, int Failed, IReadOnlyList<ImportError> Errors);
public sealed record CaseListItem(Guid Id, string ShipmentReference, string SupplierName, string ProductName, EudrCaseStatus Status, int MissingRequiredCount, int SubmittedForReviewCount, DateTimeOffset UpdatedAtUtc);
public sealed record DocumentDto(Guid Id, string OriginalFileName, string ContentType, long SizeBytes, DocumentCategory Category, UploadedByType UploadedByType, DateTimeOffset UploadedAtUtc, DateTimeOffset? WithdrawnAtUtc = null);
public sealed record GeoDto(Guid Id, string OriginalFileName, int FeatureCount, string GeometryTypes, GeoValidationStatus ValidationStatus, string? ValidationMessage, DateTimeOffset UploadedAtUtc, DateTimeOffset? SupersededAtUtc = null);
public sealed record SupplierSubmissionVersionDto(Guid Id, string? CountryOfProductionCode, DateOnly? ProductionPeriodFrom, DateOnly? ProductionPeriodTo, decimal? ConfirmedQuantity, string? Notes, DateTimeOffset? SubmittedAtUtc, DateTimeOffset? SupersededAtUtc, Guid? SupplierRequestId, DateTimeOffset CreatedAtUtc);
public sealed record EvidenceHistory(List<DocumentDto> Documents, List<GeoDto> Geolocations, List<SupplierSubmissionVersionDto> SupplierSubmissions);
public sealed record SupplierRequestDto(Guid Id, string Status, DateTimeOffset ExpiresAtUtc, DateTimeOffset? SentAtUtc, DateTimeOffset? OpenedAtUtc, DateTimeOffset? CompletedAtUtc, DateTimeOffset? RevokedAtUtc);
public sealed record CaseDetails(Guid Id, EudrCaseStatus Status, Shipment Shipment, Supplier Supplier, Product Product,
    SupplierSubmission? SupplierSubmission, List<EudrRequirement> Requirements, List<DocumentDto> Documents,
    GeoDto? Geolocation, SupplierRequestDto? SupplierRequest, List<AuditEvent> AuditEvents, string? ExternalDdsReference,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset? CompletedAtUtc);
public static class DtoMaps
{
    public static DocumentDto ToDto(this CaseDocument d) => new(d.Id, d.OriginalFileName, d.ContentType, d.SizeBytes, d.Category, d.UploadedByType, d.UploadedAtUtc, d.WithdrawnAtUtc);
    public static GeoDto ToDto(this GeoSubmission g) => new(g.Id, g.OriginalFileName, g.FeatureCount, g.GeometryTypes, g.ValidationStatus, g.ValidationMessage, g.UploadedAtUtc, g.SupersededAtUtc);
    public static SupplierSubmissionVersionDto ToVersionDto(this SupplierSubmission s) => new(s.Id, s.CountryOfProductionCode, s.ProductionPeriodFrom, s.ProductionPeriodTo, s.ConfirmedQuantity, s.Notes, s.SubmittedAtUtc, s.SupersededAtUtc, s.SupplierRequestId, s.CreatedAtUtc);
    public static SupplierRequestDto ToDto(this SupplierRequest r) => new(r.Id, r.RevokedAtUtc != null ? "Revoked" : r.ExpiresAtUtc <= DateTimeOffset.UtcNow ? "Expired" : r.CompletedAtUtc != null ? "Submitted" : "Active", r.ExpiresAtUtc, r.SentAtUtc, r.OpenedAtUtc, r.CompletedAtUtc, r.RevokedAtUtc);
}
