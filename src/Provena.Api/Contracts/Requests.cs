using System.Text.Json;
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
public sealed record ProducerInput(string Name, string? ExternalReference, string? CountryCode, string? Notes, bool? IsActive = null);
public sealed record ProductionPlotInput(Guid ProducerId, string Name, string? ExternalReference, string? Notes, PlotRevisionInput InitialRevision);
public sealed record ProductionPlotUpdateInput(string Name, string? ExternalReference, string? Notes, bool? IsActive = null);
public sealed record PlotRevisionInput(string CountryCode, JsonElement Geometry, decimal? AreaHectares, string? Note);
public sealed record ProducerDto(Guid Id, string Name, string? ExternalReference, string? CountryCode, string? Notes, bool IsActive, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record PlotRevisionSummaryDto(Guid Id, int Number, string CountryCode, PlotGeometryType GeometryType, decimal? AreaHectares, string? Note, Guid? CreatedByUserId, DateTimeOffset CreatedAtUtc, DateTimeOffset? SupersededAtUtc);
public sealed record PlotRevisionDto(Guid Id, Guid ProductionPlotId, int Number, string CountryCode, PlotGeometryType GeometryType, JsonElement Geometry, decimal? AreaHectares, string? Note, Guid? CreatedByUserId, DateTimeOffset CreatedAtUtc, DateTimeOffset? SupersededAtUtc);
public sealed record ProductionPlotListItem(Guid Id, Guid ProducerId, string Name, string? ExternalReference, bool IsActive, DateTimeOffset UpdatedAtUtc, PlotRevisionSummaryDto CurrentRevision);
public sealed record ProductionPlotDto(Guid Id, Guid ProducerId, string Name, string? ExternalReference, string? Notes, bool IsActive, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, PlotRevisionDto CurrentRevision);
// Case dossier: the PUT body is the complete factual state of the case's one lot (header, producer contributions, plot/revision links).
public sealed record CaseLotInput(string LotReference, decimal Quantity, QuantityUnit Unit, string? Note, List<LotContributionInput> Contributions);
public sealed record LotContributionInput(Guid ProducerId, decimal Quantity, QuantityUnit Unit, List<ContributionPlotInput> Plots);
public sealed record ContributionPlotInput(Guid ProductionPlotId, Guid PlotRevisionId, DateOnly? ProductionStartDate, DateOnly? ProductionEndDate);
public enum QuantityReconciliationStatus { UnderAllocated, Reconciled }
public sealed record QuantityReconciliationDto(decimal AllocatedQuantity, decimal RemainingQuantity, QuantityReconciliationStatus Status);
public sealed record ContributionPlotDto(Guid Id, Guid ProductionPlotId, string PlotName, bool PlotIsActive, Guid PlotRevisionId, int RevisionNumber, string CountryCode, PlotGeometryType GeometryType, decimal? AreaHectares, DateTimeOffset? RevisionSupersededAtUtc, DateOnly? ProductionStartDate, DateOnly? ProductionEndDate);
public sealed record LotContributionDto(Guid Id, Guid ProducerId, string ProducerName, bool ProducerIsActive, decimal Quantity, QuantityUnit Unit, List<ContributionPlotDto> Plots);
public sealed record CaseLotDto(Guid Id, int Number, string LotReference, decimal Quantity, QuantityUnit Unit, string? Note, QuantityReconciliationDto Reconciliation, List<LotContributionDto> Contributions, Guid? CreatedByUserId, DateTimeOffset CreatedAtUtc, DateTimeOffset? SupersededAtUtc);
public sealed record CaseDossierDto(Guid Id, Guid EudrCaseId, DateTimeOffset CreatedAtUtc, decimal ShipmentQuantity, QuantityUnit ShipmentUnit, CaseLotDto CurrentLot);
public static class DtoMaps
{
    public static DocumentDto ToDto(this CaseDocument d) => new(d.Id, d.OriginalFileName, d.ContentType, d.SizeBytes, d.Category, d.UploadedByType, d.UploadedAtUtc, d.WithdrawnAtUtc);
    public static GeoDto ToDto(this GeoSubmission g) => new(g.Id, g.OriginalFileName, g.FeatureCount, g.GeometryTypes, g.ValidationStatus, g.ValidationMessage, g.UploadedAtUtc, g.SupersededAtUtc);
    public static SupplierSubmissionVersionDto ToVersionDto(this SupplierSubmission s) => new(s.Id, s.CountryOfProductionCode, s.ProductionPeriodFrom, s.ProductionPeriodTo, s.ConfirmedQuantity, s.Notes, s.SubmittedAtUtc, s.SupersededAtUtc, s.SupplierRequestId, s.CreatedAtUtc);
    public static SupplierRequestDto ToDto(this SupplierRequest r) => new(r.Id, r.RevokedAtUtc != null ? "Revoked" : r.ExpiresAtUtc <= DateTimeOffset.UtcNow ? "Expired" : r.CompletedAtUtc != null ? "Submitted" : "Active", r.ExpiresAtUtc, r.SentAtUtc, r.OpenedAtUtc, r.CompletedAtUtc, r.RevokedAtUtc);
    public static ProducerDto ToDto(this Producer p) => new(p.Id, p.Name, p.ExternalReference, p.CountryCode, p.Notes, p.IsActive, p.CreatedAtUtc, p.UpdatedAtUtc);
    public static PlotRevisionSummaryDto ToSummaryDto(this PlotRevision r) => new(r.Id, r.Number, r.CountryCode, r.GeometryType, r.AreaHectares, r.Note, r.CreatedByUserId, r.CreatedAtUtc, r.SupersededAtUtc);
    // Geometry is emitted as a JSON object (parsed from the stored jsonb text), never as a string.
    public static PlotRevisionDto ToDto(this PlotRevision r) { using var geometry = JsonDocument.Parse(r.GeometryJson); return new(r.Id, r.ProductionPlotId, r.Number, r.CountryCode, r.GeometryType, geometry.RootElement.Clone(), r.AreaHectares, r.Note, r.CreatedByUserId, r.CreatedAtUtc, r.SupersededAtUtc); }
    public static ProductionPlotDto ToDto(this ProductionPlot p, PlotRevision current) => new(p.Id, p.ProducerId, p.Name, p.ExternalReference, p.Notes, p.IsActive, p.CreatedAtUtc, p.UpdatedAtUtc, current.ToDto());
    public static ProductionPlotListItem ToListItem(this ProductionPlot p, PlotRevision current) => new(p.Id, p.ProducerId, p.Name, p.ExternalReference, p.IsActive, p.UpdatedAtUtc, current.ToSummaryDto());
}
