using System.Text.Json;
using Provena.Api.Domain;
namespace Provena.Api.Contracts;
public sealed record RegisterRequest(string Email, string Password, string FirstName, string LastName, string OrganizationName, string? Oib);
public sealed record LoginRequest(string Email, string Password);
public sealed record SupplierInput(string Name, string? ExternalReference, string? Email, string? ContactPerson, string? CountryCode, string? Notes, string? PostalAddress = null);
public sealed record ProductInput(string Name, string? ExternalReference, string CnCode, CommodityType CommodityType, string? Description, bool IsActive = true);
public sealed record ShipmentInput(string ReferenceNumber, Guid SupplierId, Guid ProductId, decimal Quantity, QuantityUnit Unit, string? OriginCountryCode, DateOnly? ExpectedArrivalDate, DateOnly? ReceivedDate, string? Notes);
public sealed record SubmissionInput(string? CountryOfProductionCode, DateOnly? ProductionPeriodFrom, DateOnly? ProductionPeriodTo, decimal? ConfirmedQuantity, string? Notes);
public sealed record ReviewInput(RequirementStatus Status, string? ReviewerNote);
public sealed record PageResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
public sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAtUtc);
public sealed record RequestLink(Guid RequestId, string PublicUrl, DateTimeOffset ExpiresAtUtc);
public sealed record ImportError(int Row, string Message);
public sealed record ImportResult(int TotalRows, int Imported, int Failed, IReadOnlyList<ImportError> Errors);
public sealed record CaseListItem(Guid Id, string ShipmentReference, string SupplierName, string ProductName, EudrCaseStatus Status, int MissingRequiredCount, int SubmittedForReviewCount, DateTimeOffset UpdatedAtUtc, bool Archived, CaseClosureOutcome? ClosureOutcome);
public sealed record DocumentDto(Guid Id, string OriginalFileName, string ContentType, long SizeBytes, DocumentCategory Category, UploadedByType UploadedByType, DateTimeOffset UploadedAtUtc, DateTimeOffset? WithdrawnAtUtc = null);
public sealed record GeoDto(Guid Id, string OriginalFileName, int FeatureCount, string GeometryTypes, GeoValidationStatus ValidationStatus, string? ValidationMessage, DateTimeOffset UploadedAtUtc, DateTimeOffset? SupersededAtUtc = null);
public sealed record SupplierSubmissionVersionDto(Guid Id, string? CountryOfProductionCode, DateOnly? ProductionPeriodFrom, DateOnly? ProductionPeriodTo, decimal? ConfirmedQuantity, string? Notes, DateTimeOffset? SubmittedAtUtc, DateTimeOffset? SupersededAtUtc, Guid? SupplierRequestId, DateTimeOffset CreatedAtUtc);
public sealed record EvidenceHistory(List<DocumentDto> Documents, List<GeoDto> Geolocations, List<SupplierSubmissionVersionDto> SupplierSubmissions);
public sealed record SupplierRequestDto(Guid Id, string Status, DateTimeOffset ExpiresAtUtc, DateTimeOffset? SentAtUtc, DateTimeOffset? OpenedAtUtc, DateTimeOffset? CompletedAtUtc, DateTimeOffset? RevokedAtUtc);
public sealed record CaseDetails(Guid Id, EudrCaseStatus Status, Shipment Shipment, Supplier Supplier, Product Product,
    SupplierSubmission? SupplierSubmission, List<EudrRequirement> Requirements, List<DocumentDto> Documents,
    GeoDto? Geolocation, SupplierRequestDto? SupplierRequest, List<AuditEvent> AuditEvents,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, CaseClosureDto? Closure, CaseArchivalDto? Archive);
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
// Quantity Reconciliation v1 (completeness policy v2): derived on read and never stored. Exact decimal equality, same unit only, no conversion, no tolerance.
public enum QuantityCheckStatus { Matched, MissingData, UnitMismatch, QuantityMismatch, NotApplicable }
public enum QuantityReconciliationOutcome { Matched, MissingData, UnitMismatch, QuantityMismatch }
public sealed record QuantityOperandDto(string Source, decimal? Quantity, QuantityUnit? Unit, bool UnitImplied);
public sealed record QuantityCheckDto(string Code, QuantityCheckStatus Status, QuantityOperandDto Expected, QuantityOperandDto Actual, decimal? Difference, IReadOnlyList<string> MissingFields, string Reason);
public sealed record QuantityReconciliationResultDto(QuantityReconciliationOutcome Outcome, IReadOnlyList<QuantityCheckDto> Checks);
public sealed record ContributionPlotDto(Guid Id, Guid ProductionPlotId, string PlotName, bool PlotIsActive, Guid PlotRevisionId, int RevisionNumber, string CountryCode, PlotGeometryType GeometryType, decimal? AreaHectares, DateTimeOffset? RevisionSupersededAtUtc, DateOnly? ProductionStartDate, DateOnly? ProductionEndDate);
public sealed record LotContributionDto(Guid Id, Guid ProducerId, string ProducerName, bool ProducerIsActive, decimal Quantity, QuantityUnit Unit, List<ContributionPlotDto> Plots);
public sealed record CaseLotDto(Guid Id, int Number, string LotReference, decimal Quantity, QuantityUnit Unit, string? Note, QuantityReconciliationDto Reconciliation, List<LotContributionDto> Contributions, Guid? CreatedByUserId, DateTimeOffset CreatedAtUtc, DateTimeOffset? SupersededAtUtc);
public sealed record CaseDossierDto(Guid Id, Guid EudrCaseId, DateTimeOffset CreatedAtUtc, decimal ShipmentQuantity, QuantityUnit ShipmentUnit, CaseLotDto CurrentLot);
// Completeness facts (ADR 0008): operator profile, case master-data copy, applicability, recipients, evidence links. No DTO carries OrganizationId.
public sealed record OperatorProfileInput(string LegalName, string PostalAddress, string Email, string EstablishmentCountryCode, string? EoriNumber);
public sealed record OperatorProfileDto(Guid Id, string LegalName, string PostalAddress, string Email, string EstablishmentCountryCode, string? EoriNumber, Guid? UpdatedByUserId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record MasterDataOperatorDto(string? LegalName, string? PostalAddress, string? Email, string? EstablishmentCountryCode, string? Eori);
public sealed record MasterDataSupplierDto(Guid SupplierId, string Name, string? PostalAddress, string? Email, string? CountryCode);
public sealed record MasterDataProductDto(Guid ProductId, string CnCode, CommodityType Commodity, string Name, string? CatalogueDescription);
public sealed record CaseMasterDataVersionDto(Guid Id, int Number, MasterDataOperatorDto Operator, MasterDataSupplierDto Supplier, MasterDataProductDto Product, Guid? CreatedByUserId, DateTimeOffset CreatedAtUtc, DateTimeOffset? SupersededAtUtc);
public enum MasterDataArea { Operator, Supplier, Product }
public sealed record MasterDataDriftDto(MasterDataArea Area, List<string> Fields);
public sealed record CaseMasterDataDto(CaseMasterDataVersionDto Current, bool ShipmentMatch, List<MasterDataDriftDto> Drift);
public sealed record CaseApplicabilityInput(string? ProductDescription, bool SingleRelevantCommodity, ApplicabilityActivityType ActivityType, OperatorRole OperatorRole, string? OperatorRoleDescription,
    DateOnly? ActivityDate, ActivityDateKind ActivityDateKind, string? ScopeCitation, string? Qualifications, string? LegalBasisVersion, DateOnly? EffectiveDate, string? Reasoning, RegulatoryScope RegulatoryScope, FlowSupport FlowSupport);
public sealed record CaseApplicabilityVersionDto(Guid Id, int Number, Guid AssessedProductId, string AssessedCnCode, CommodityType AssessedCommodity, string? ProductDescription, bool SingleRelevantCommodity, ApplicabilityActivityType ActivityType,
    OperatorRole OperatorRole, string? OperatorRoleDescription, DateOnly? ActivityDate, ActivityDateKind ActivityDateKind, string? ScopeCitation, string? Qualifications, string? LegalBasisVersion, DateOnly? EffectiveDate, string? Reasoning,
    RegulatoryScope RegulatoryScope, FlowSupport FlowSupport, Guid? DecidedByUserId, DateTimeOffset? DecidedAtUtc, Guid? CreatedByUserId, DateTimeOffset CreatedAtUtc, DateTimeOffset? SupersededAtUtc);
public sealed record CaseApplicabilityDto(ApplicabilityStatus Status, CaseApplicabilityVersionDto Current);
public sealed record CaseRecipientInput(string Name, string PostalAddress, string Email);
public sealed record CaseRecipientsInput(RecipientResolution Resolution, string? NotApplicableBasis, List<CaseRecipientInput>? Recipients);
public sealed record CaseRecipientDto(Guid Id, string Name, string PostalAddress, string Email);
public sealed record CaseRecipientStatementDto(Guid Id, int Number, RecipientResolution Resolution, string? NotApplicableBasis, List<CaseRecipientDto> Recipients, Guid? CreatedByUserId, DateTimeOffset CreatedAtUtc, DateTimeOffset? SupersededAtUtc);
public sealed record CaseEvidenceLinkInput(Guid CaseDocumentId, EvidencePurpose Purpose, string? Note);
public sealed record CaseEvidenceLinkDto(Guid Id, Guid CaseDocumentId, EvidencePurpose Purpose, string? Note, string DocumentFileName, DateTimeOffset? DocumentWithdrawnAtUtc, Guid? CreatedByUserId, DateTimeOffset CreatedAtUtc, DateTimeOffset? WithdrawnAtUtc, Guid? WithdrawnByUserId);
// Completeness read model (ADR 0008). Computed on every read, never stored. LatestReview is null until a review exists.
public sealed record CompletenessPolicyDto(string Id, string Version, IReadOnlyList<string> Limitations);
public sealed record CompletenessApplicabilityDto(ApplicabilityStatus Status, Guid? CurrentVersionId);
public sealed record CompletenessItemDto(string Code, CompletenessGroup Group, string? SubjectType, Guid? SubjectId, CompletenessItemStatus Status, string Reason);
public sealed record CompletenessAdvisoryDto(string Code, string? Area, Guid? SubjectId, string Message);
public sealed record CompletenessDto(CompletenessPolicyDto Policy, DateTimeOffset EvaluatedAtUtc, string Fingerprint, int FingerprintVersion, CompletenessApplicabilityDto Applicability,
    DataCoverage DataCoverage, EvidenceReadiness EvidenceReadiness, ReviewFreshness ReviewFreshness, LatestReviewDto? LatestReview, IReadOnlyList<CompletenessAdvisoryDto> Advisories, IReadOnlyList<CompletenessItemDto> Items,
    QuantityReconciliationResultDto QuantityReconciliation);
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
    public static OperatorProfileDto ToDto(this OperatorProfile p) => new(p.Id, p.LegalName, p.PostalAddress, p.Email, p.EstablishmentCountryCode, p.EoriNumber, p.UpdatedByUserId, p.CreatedAtUtc, p.UpdatedAtUtc);
    public static CaseMasterDataVersionDto ToDto(this CaseMasterDataVersion v) => new(v.Id, v.Number,
        new(v.OperatorLegalName, v.OperatorPostalAddress, v.OperatorEmail, v.OperatorEstablishmentCountryCode, v.OperatorEori),
        new(v.SupplierId, v.SupplierName, v.SupplierPostalAddress, v.SupplierEmail, v.SupplierCountryCode),
        new(v.ProductId, v.ProductCnCode, v.ProductCommodity, v.ProductName, v.ProductCatalogueDescription), v.CreatedByUserId, v.CreatedAtUtc, v.SupersededAtUtc);
    public static CaseApplicabilityVersionDto ToDto(this CaseApplicability a) => new(a.Id, a.Number, a.AssessedProductId, a.AssessedCnCode, a.AssessedCommodity, a.ProductDescription, a.SingleRelevantCommodity, a.ActivityType,
        a.OperatorRole, a.OperatorRoleDescription, a.ActivityDate, a.ActivityDateKind, a.ScopeCitation, a.Qualifications, a.LegalBasisVersion, a.EffectiveDate, a.Reasoning,
        a.RegulatoryScope, a.FlowSupport, a.DecidedByUserId, a.DecidedAtUtc, a.CreatedByUserId, a.CreatedAtUtc, a.SupersededAtUtc);
    public static CaseRecipientStatementDto ToDto(this CaseRecipientStatement s, IEnumerable<CaseRecipient> recipients) => new(s.Id, s.Number, s.Resolution, s.NotApplicableBasis,
        recipients.OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.PostalAddress, StringComparer.Ordinal).ThenBy(r => r.Email, StringComparer.Ordinal).ThenBy(r => r.Id)
            .Select(r => new CaseRecipientDto(r.Id, r.Name, r.PostalAddress, r.Email)).ToList(), s.CreatedByUserId, s.CreatedAtUtc, s.SupersededAtUtc);
}
