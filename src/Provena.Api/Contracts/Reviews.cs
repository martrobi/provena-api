using System.Text.Json.Serialization;
using Provena.Api.Domain;
namespace Provena.Api.Contracts;

// Reviewed dossier history (ADR 0008). The snapshot types below are the PERSISTED contract of CaseRevision.SnapshotJson (schema version 1): explicit
// records, never EF entities. They are deliberately independent of the working DTOs so a later DTO change cannot reinterpret a stored review.
// No type here carries OrganizationId.

// ---- requests ---------------------------------------------------------------------------------------------------------------------------
public sealed record CaseReviewInput(ReviewDecision Decision, string Rationale, string ExpectedFingerprint, Guid? ExpectedLatestReviewId);

// ---- snapshot v1: facts (fingerprinted content), context (frozen, not fingerprinted), completeness (the evaluation relied upon) ------------------------
public sealed record SnapshotShipmentV1(string Reference, Guid SupplierId, Guid ProductId, decimal Quantity, QuantityUnit Unit, string? OriginCountryCode, DateOnly? ExpectedArrivalDate, DateOnly? ReceivedDate);
public sealed record SnapshotOperatorV1(string? LegalName, string? PostalAddress, string? Email, string? EstablishmentCountryCode, string? Eori);
public sealed record SnapshotSupplierV1(Guid SupplierId, string Name, string? PostalAddress, string? Email, string? CountryCode);
public sealed record SnapshotProductV1(Guid ProductId, string CnCode, CommodityType Commodity);
public sealed record SnapshotMasterDataV1(SnapshotOperatorV1 Operator, SnapshotSupplierV1 Supplier, SnapshotProductV1 Product);
public sealed record SnapshotApplicabilityV1(Guid Id, int Number, Guid AssessedProductId, string AssessedCnCode, CommodityType AssessedCommodity, string? ProductDescription, bool SingleRelevantCommodity,
    ApplicabilityActivityType ActivityType, OperatorRole OperatorRole, string? OperatorRoleDescription, DateOnly? ActivityDate, ActivityDateKind ActivityDateKind, string? ScopeCitation, string? Qualifications,
    string? LegalBasisVersion, DateOnly? EffectiveDate, string? Reasoning, RegulatoryScope RegulatoryScope, FlowSupport FlowSupport, Guid? DecidedByUserId, DateTimeOffset? DecidedAtUtc);
public sealed record SnapshotRecipientV1(string Name, string PostalAddress, string Email);
public sealed record SnapshotRecipientsV1(Guid Id, int Number, RecipientResolution Resolution, string? NotApplicableBasis, IReadOnlyList<SnapshotRecipientV1> Recipients);
public sealed record SnapshotPlotLinkV1(Guid PlotId, Guid RevisionId, int RevisionNumber, string CountryCode, PlotGeometryType GeometryType, decimal? AreaHectares, string GeometrySha256, DateOnly? ProductionStartDate, DateOnly? ProductionEndDate);
public sealed record SnapshotContributionV1(Guid ProducerId, decimal Quantity, QuantityUnit Unit, IReadOnlyList<SnapshotPlotLinkV1> Plots);
public sealed record SnapshotLotV1(Guid? DossierId, Guid Id, int Number, string LotReference, decimal Quantity, QuantityUnit Unit, string? Note, QuantityReconciliationDto Reconciliation, IReadOnlyList<SnapshotContributionV1> Contributions);
public sealed record SnapshotDocumentV1(Guid Id, string OriginalFileName, string ContentType, long SizeBytes, DocumentCategory Category, UploadedByType UploadedByType, DateTimeOffset UploadedAtUtc);
public sealed record SnapshotGeolocationV1(Guid Id, string OriginalFileName, int FeatureCount, string GeometryTypes, GeoValidationStatus ValidationStatus, DateTimeOffset UploadedAtUtc);
public sealed record SnapshotSubmissionV1(Guid Id, string? CountryOfProductionCode, DateOnly? ProductionPeriodFrom, DateOnly? ProductionPeriodTo, decimal? ConfirmedQuantity, string? Notes, DateTimeOffset? SubmittedAtUtc);
public sealed record SnapshotEvidenceLinkV1(Guid Id, Guid DocumentId, EvidencePurpose Purpose, string? Note);
public sealed record SnapshotEvidenceV1(IReadOnlyList<SnapshotDocumentV1> Documents, SnapshotGeolocationV1? Geolocation, SnapshotSubmissionV1? SupplierSubmission, IReadOnlyList<SnapshotEvidenceLinkV1> EvidenceLinks);
public sealed record SnapshotRequirementV1(RequirementType Type, bool IsRequired, RequirementStatus Status, string? Note, Guid? ReviewedByUserId, DateTimeOffset? ReviewedAtUtc);
public sealed record SnapshotFactsV1(SnapshotShipmentV1 Shipment, SnapshotMasterDataV1? MasterData, SnapshotApplicabilityV1? Applicability, SnapshotRecipientsV1? Recipients, SnapshotLotV1? Lot,
    SnapshotEvidenceV1 Evidence, IReadOnlyList<SnapshotRequirementV1> Requirements);

public sealed record SnapshotProducerContextV1(Guid Id, string Name, string? ExternalReference, string? CountryCode);
public sealed record SnapshotPlotContextV1(Guid Id, Guid ProducerId, string Name, string? ExternalReference);
public sealed record SnapshotPinnedRevisionContextV1(Guid RevisionId, Guid PlotId, bool SupersededAtReviewTime);
public sealed record SnapshotContextV1(string? ProductName, string? ProductCatalogueDescription, IReadOnlyList<SnapshotProducerContextV1> Producers, IReadOnlyList<SnapshotPlotContextV1> Plots,
    IReadOnlyList<SnapshotPinnedRevisionContextV1> PinnedRevisions, IReadOnlyList<MasterDataDriftDto> CatalogueDrift);

// The completeness evaluation relied upon at review time. It never embeds review freshness or the latest review (those are dynamic).
public sealed record SnapshotCompletenessV1(string PolicyId, string PolicyVersion, ApplicabilityStatus ApplicabilityStatus, DataCoverage DataCoverage, EvidenceReadiness EvidenceReadiness, IReadOnlyList<CompletenessItemDto> Items);

// A stored snapshot of any supported schema version. Serialization is polymorphic WITHOUT a discriminator, so each version keeps exactly its stored shape; the view members are explicit
// (not serialized) and let consumers read shipment and lot facts without caring about the version.
[JsonDerivedType(typeof(CaseRevisionSnapshotV1))]
[JsonDerivedType(typeof(CaseRevisionSnapshotV2))]
public interface IReviewSnapshot
{
    int SchemaVersion { get; }
    string ShipmentReference { get; }
    SnapshotLotV2? LotView { get; }
}

public sealed record CaseRevisionSnapshotV1(int SchemaVersion, SnapshotFactsV1 Facts, SnapshotContextV1 Context, SnapshotCompletenessV1 Completeness) : IReviewSnapshot
{
    string IReviewSnapshot.ShipmentReference => Facts.Shipment.Reference;
    SnapshotLotV2? IReviewSnapshot.LotView => Facts.Lot?.ToV2();
}

// ---- snapshot v2: schema v1 plus the retained per-plot AllocatedQuantity (Source Quantity Allocation v1). v1 types above stay readable exactly as stored. --------------
public sealed record SnapshotPlotLinkV2(Guid PlotId, Guid RevisionId, int RevisionNumber, string CountryCode, PlotGeometryType GeometryType, decimal? AreaHectares, string GeometrySha256, DateOnly? ProductionStartDate, DateOnly? ProductionEndDate, decimal? AllocatedQuantity);
public sealed record SnapshotContributionV2(Guid ProducerId, decimal Quantity, QuantityUnit Unit, IReadOnlyList<SnapshotPlotLinkV2> Plots);
public sealed record SnapshotLotV2(Guid? DossierId, Guid Id, int Number, string LotReference, decimal Quantity, QuantityUnit Unit, string? Note, QuantityReconciliationDto Reconciliation, IReadOnlyList<SnapshotContributionV2> Contributions);
public sealed record SnapshotFactsV2(SnapshotShipmentV1 Shipment, SnapshotMasterDataV1? MasterData, SnapshotApplicabilityV1? Applicability, SnapshotRecipientsV1? Recipients, SnapshotLotV2? Lot,
    SnapshotEvidenceV1 Evidence, IReadOnlyList<SnapshotRequirementV1> Requirements);
public sealed record CaseRevisionSnapshotV2(int SchemaVersion, SnapshotFactsV2 Facts, SnapshotContextV1 Context, SnapshotCompletenessV1 Completeness) : IReviewSnapshot
{
    string IReviewSnapshot.ShipmentReference => Facts.Shipment.Reference;
    SnapshotLotV2? IReviewSnapshot.LotView => Facts.Lot;
}

public static class SnapshotLotExtensions
{
    // A schema v1 lot carries no allocation: every plot link reads as "not recorded".
    public static SnapshotLotV2 ToV2(this SnapshotLotV1 lot) => new(lot.DossierId, lot.Id, lot.Number, lot.LotReference, lot.Quantity, lot.Unit, lot.Note, lot.Reconciliation,
        lot.Contributions.Select(c => new SnapshotContributionV2(c.ProducerId, c.Quantity, c.Unit,
            c.Plots.Select(p => new SnapshotPlotLinkV2(p.PlotId, p.RevisionId, p.RevisionNumber, p.CountryCode, p.GeometryType, p.AreaHectares, p.GeometrySha256, p.ProductionStartDate, p.ProductionEndDate, null)).ToList())).ToList());
}

// ---- responses --------------------------------------------------------------------------------------------------------------------------
public sealed record LatestReviewDto(Guid ReviewId, Guid CaseRevisionId, ReviewDecision Decision, DateTimeOffset ReviewedAtUtc, string PolicyVersion, bool PolicyChanged, IReadOnlyList<string> ChangedAreas);
public sealed record ReviewSummaryDto(Guid ReviewId, Guid CaseRevisionId, int RevisionNumber, ReviewDecision Decision, Guid ReviewerUserId, DateTimeOffset ReviewedAtUtc, string PolicyId, string PolicyVersion, string Fingerprint, int FingerprintVersion);
public sealed record ReviewPinsDto(Guid? CaseDossierId, Guid? CaseLotId, Guid? CaseApplicabilityId, Guid? CaseRecipientStatementId, Guid? CaseMasterDataVersionId, Guid? GeoSubmissionId, Guid? SupplierSubmissionId,
    IReadOnlyList<Guid> DocumentIds, IReadOnlyList<Guid> EvidenceLinkIds);
public sealed record ReviewDetailDto(Guid ReviewId, Guid CaseRevisionId, int RevisionNumber, ReviewDecision Decision, string Rationale, Guid ReviewerUserId, DateTimeOffset ReviewedAtUtc, string PolicyId, string PolicyVersion,
    int SnapshotSchemaVersion, int FingerprintVersion, string Fingerprint, IReadOnlyDictionary<string, string> SectionHashes, ReviewPinsDto Pins, IReviewSnapshot Snapshot);
public sealed record ReviewExportDto(Guid ReviewId, Guid CaseRevisionId, int RevisionNumber, ReviewDecision Decision, string Rationale, Guid ReviewerUserId, DateTimeOffset ReviewedAtUtc, string PolicyId, string PolicyVersion,
    string Fingerprint, int FingerprintVersion, int SnapshotSchemaVersion, IReadOnlyDictionary<string, string> SectionHashes, IReviewSnapshot Snapshot, DateTimeOffset ExportedAtUtc, string Notice,
    ProductionCoverageDto ProductionCoverage, string ProductionCoverageScope, SourceAllocationDto? SourceAllocation, string SourceAllocationScope);
