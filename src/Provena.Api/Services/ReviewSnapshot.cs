using System.Text.Json;
using System.Text.Json.Serialization;
using Provena.Api.Contracts;
using Provena.Api.Domain;
namespace Provena.Api.Services;
// Pure builder and (de)serializer of the snapshot stored in CaseRevision.SnapshotJson (ADR 0008). New reviews store schema v2 (v1 plus the per-plot AllocatedQuantity); schema v1 stays readable as stored. facts mirror the fingerprinted content,
// context freezes labels and catalogue context that must not change after the review, completeness is the evaluation relied upon. Explicit records only:
// no EF entities, no anonymous objects, no dynamic review state (latest review / freshness) recursively inside a historical snapshot.
public static class ReviewSnapshot
{
    public const int SchemaVersion = 2;
    public static readonly IReadOnlyList<int> SupportedVersions = [1, 2];
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static CaseRevisionSnapshotV2 Build(DossierFacts f, CompletenessEvaluation evaluation, IReadOnlyCollection<Producer> producers, IReadOnlyCollection<ProductionPlot> plots)
    {
        var m = f.MasterData; var a = f.Applicability; var s = f.Shipment;
        var facts = new SnapshotFactsV2(
            new(s.ReferenceNumber, s.SupplierId, s.ProductId, s.Quantity, s.Unit, s.OriginCountryCode, s.ExpectedArrivalDate, s.ReceivedDate),
            m == null ? null : new(new(m.OperatorLegalName, m.OperatorPostalAddress, m.OperatorEmail, m.OperatorEstablishmentCountryCode, m.OperatorEori), new(m.SupplierId, m.SupplierName, m.SupplierPostalAddress, m.SupplierEmail, m.SupplierCountryCode), new(m.ProductId, m.ProductCnCode, m.ProductCommodity)),
            a == null ? null : new(a.Id, a.Number, a.AssessedProductId, a.AssessedCnCode, a.AssessedCommodity, a.ProductDescription, a.SingleRelevantCommodity, a.ActivityType, a.OperatorRole, a.OperatorRoleDescription, a.ActivityDate,
                a.ActivityDateKind, a.ScopeCitation, a.Qualifications, a.LegalBasisVersion, a.EffectiveDate, a.Reasoning, a.RegulatoryScope, a.FlowSupport, a.DecidedByUserId, a.DecidedAtUtc),
            f.Recipients == null ? null : new(f.Recipients.Id, f.Recipients.Number, f.Recipients.Resolution, f.Recipients.NotApplicableBasis,
                f.RecipientEntries.OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.PostalAddress, StringComparer.Ordinal).ThenBy(r => r.Email, StringComparer.Ordinal).Select(r => new SnapshotRecipientV1(r.Name, r.PostalAddress, r.Email)).ToList()),
            Lot(f),
            new(f.Documents.OrderBy(d => d.Id).Select(d => new SnapshotDocumentV1(d.Id, d.OriginalFileName, d.ContentType, d.SizeBytes, d.Category, d.UploadedByType, d.UploadedAtUtc)).ToList(),
                f.Geolocation == null ? null : new(f.Geolocation.Id, f.Geolocation.OriginalFileName, f.Geolocation.FeatureCount, f.Geolocation.GeometryTypes, f.Geolocation.ValidationStatus, f.Geolocation.UploadedAtUtc),
                f.Submission == null ? null : new(f.Submission.Id, f.Submission.CountryOfProductionCode, f.Submission.ProductionPeriodFrom, f.Submission.ProductionPeriodTo, f.Submission.ConfirmedQuantity, f.Submission.Notes, f.Submission.SubmittedAtUtc),
                f.EvidenceLinks.Where(l => l.WithdrawnAtUtc == null).OrderBy(l => l.Id).Select(l => new SnapshotEvidenceLinkV1(l.Id, l.CaseDocumentId, l.Purpose, l.Note)).ToList()),
            f.Requirements.OrderBy(r => r.Type).Select(r => new SnapshotRequirementV1(r.Type, r.IsRequired, r.Status, r.ReviewerNote, r.ReviewedByUserId, r.ReviewedAtUtc)).ToList());

        var pinned = f.Contributions.SelectMany(c => c.Links).Select(l => l.Revision).DistinctBy(r => r.Id).OrderBy(r => r.Id).ToList();
        var context = new SnapshotContextV1(m?.ProductName, m?.ProductCatalogueDescription,
            producers.OrderBy(p => p.Id).Select(p => new SnapshotProducerContextV1(p.Id, p.Name, p.ExternalReference, p.CountryCode)).ToList(),
            plots.OrderBy(p => p.Id).Select(p => new SnapshotPlotContextV1(p.Id, p.ProducerId, p.Name, p.ExternalReference)).ToList(),
            pinned.Select(r => new SnapshotPinnedRevisionContextV1(r.Id, r.ProductionPlotId, r.SupersededAtUtc != null)).ToList(), f.Drift);

        return new(SchemaVersion, facts, context, new(CompletenessPolicy.Id, CompletenessPolicy.Version, evaluation.ApplicabilityStatus, evaluation.DataCoverage, evaluation.EvidenceReadiness, evaluation.Items));
    }

    private static SnapshotLotV2? Lot(DossierFacts f)
    {
        if (f.Lot == null) return null;
        var allocated = f.Contributions.Sum(c => c.Contribution.Quantity);
        return new(f.Dossier?.Id, f.Lot.Id, f.Lot.Number, f.Lot.LotReference, f.Lot.Quantity, f.Lot.Unit, f.Lot.Note,
            new(allocated, f.Lot.Quantity - allocated, allocated == f.Lot.Quantity ? QuantityReconciliationStatus.Reconciled : QuantityReconciliationStatus.UnderAllocated),
            f.Contributions.OrderBy(c => c.Contribution.ProducerId).Select(c => new SnapshotContributionV2(c.Contribution.ProducerId, c.Contribution.Quantity, c.Contribution.Unit,
                c.Links.OrderBy(l => l.Link.ProductionPlotId).ThenBy(l => l.Link.PlotRevisionId).Select(l => new SnapshotPlotLinkV2(l.Link.ProductionPlotId, l.Link.PlotRevisionId, l.Revision.Number, l.Revision.CountryCode,
                    l.Revision.GeometryType, l.Revision.AreaHectares, DossierFingerprint.GeometrySha256(l.Revision.GeometryJson), l.Link.ProductionStartDate, l.Link.ProductionEndDate, l.Link.AllocatedQuantity)).ToList())).ToList());
    }

    // By runtime type, so each schema version is stored exactly in its own shape.
    public static string Serialize(IReviewSnapshot snapshot) => JsonSerializer.Serialize(snapshot, snapshot.GetType(), Options);

    // Stored snapshots stay readable as written: no recomputation under today's policy. An unknown schema version is rejected explicitly.
    public static IReviewSnapshot Deserialize(string json, int schemaVersion)
    {
        IReviewSnapshot? snapshot = schemaVersion switch
        {
            1 => JsonSerializer.Deserialize<CaseRevisionSnapshotV1>(json, Options),
            2 => JsonSerializer.Deserialize<CaseRevisionSnapshotV2>(json, Options),
            _ => throw new NotSupportedException($"Unsupported review snapshot schema version {schemaVersion}."),
        };
        return snapshot ?? throw new InvalidOperationException("The stored review snapshot is empty.");
    }

    public static string SerializeHashes(IReadOnlyDictionary<string, string> hashes) => JsonSerializer.Serialize(hashes.OrderBy(h => h.Key, StringComparer.Ordinal).ToDictionary(h => h.Key, h => h.Value), Options);
    public static IReadOnlyDictionary<string, string> DeserializeHashes(string json) => JsonSerializer.Deserialize<Dictionary<string, string>>(json, Options) ?? [];
}
