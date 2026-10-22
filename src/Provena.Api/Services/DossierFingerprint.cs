using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Provena.Api.Domain;
namespace Provena.Api.Services;
public sealed record FingerprintResult(int Version, string Fingerprint, IReadOnlyDictionary<string, string> SectionHashes);
// Canonical fingerprint of the FACTS of a case (ADR 0008). Pure and deterministic. Canonical form: compact UTF-8 JSON with a fixed property order,
// collections sorted by id (ordinal, lower-case "D" guids), decimals as invariant strings without trailing zeros (1.5 == 1.500000), ISO dates and
// UTC timestamps with 7 fractional digits, enums as their names. Hashes are SHA-256 rendered as LOWER-CASE hexadecimal.
// Section hashes cover Shipment, MasterData, Applicability, Recipients, Lot, Requirements and Evidence; the fingerprint hashes the envelope that embeds them.
// Excluded on purpose: master-data version id and context fields (ProductName, ProductCatalogueDescription), producer/plot names and active flags,
// RevisionSupersededAtUtc, case status, audit events, export timestamps and live catalogue drift.
// A change to the canonical form adds a new version; old versions stay implemented so reviews made under them remain comparable.
// Version 2 (Source Quantity Allocation v1) is version 1 byte for byte plus the canonical allocatedQuantity (null when not recorded) of every plot link in the Lot section and
// fingerprintVersion 2 in the envelope. Version 1 output is frozen: a review made under it keeps recomputing with version 1 (and still goes Stale on any allocation change, because the lot id changes).
public static class DossierFingerprint
{
    public const int CurrentVersion = 2;
    public static readonly IReadOnlyList<int> SupportedVersions = [1, 2];
    public static readonly IReadOnlyList<string> Sections = ["Shipment", "MasterData", "Applicability", "Recipients", "Lot", "Requirements", "Evidence"];

    public static FingerprintResult Compute(DossierFacts facts, int version = CurrentVersion)
    {
        if (!SupportedVersions.Contains(version)) throw new ArgumentOutOfRangeException(nameof(version), "Unsupported fingerprint version.");
        return ComputeVersioned(facts, version);
    }

    private static FingerprintResult ComputeVersioned(DossierFacts f, int version)
    {
        var sections = new Dictionary<string, byte[]>
        {
            ["Shipment"] = Write(w => Shipment(w, f.Shipment)),
            ["MasterData"] = Write(w => MasterData(w, f.MasterData)),
            ["Applicability"] = Write(w => Applicability(w, f.Applicability)),
            ["Recipients"] = Write(w => Recipients(w, f.Recipients, f.RecipientEntries)),
            ["Lot"] = Write(w => Lot(w, f, version)),
            ["Requirements"] = Write(w => Requirements(w, f.Requirements)),
            ["Evidence"] = Write(w => Evidence(w, f)),
        };
        var envelope = Write(w =>
        {
            w.WriteStartObject(); w.WriteNumber("fingerprintVersion", version);
            foreach (var name in Sections) { w.WritePropertyName(char.ToLowerInvariant(name[0]) + name[1..]); w.WriteRawValue(sections[name], skipInputValidation: true); }
            w.WriteEndObject();
        });
        return new(version, Hex(envelope), Sections.ToDictionary(n => n, n => Hex(sections[n])));
    }

    public static string CanonicalDecimal(decimal value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        if (text.Contains('.')) text = text.TrimEnd('0').TrimEnd('.');
        return text == "-0" ? "0" : text;
    }
    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    // SHA-256 (lower-case hex) of a pinned revision's stored normalized geometry; the same value is a fingerprinted fact and is stored in review snapshots.
    public static string GeometrySha256(string geometryJson) => Hex(Encoding.UTF8.GetBytes(geometryJson));
    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) body(writer);
        return stream.ToArray();
    }
    private static string Id(Guid id) => id.ToString("D");
    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Stamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static void Str(Utf8JsonWriter w, string name, string? value) { if (value == null) w.WriteNull(name); else w.WriteString(name, value); }
    private static void Dec(Utf8JsonWriter w, string name, decimal? value) { if (value == null) w.WriteNull(name); else w.WriteString(name, CanonicalDecimal(value.Value)); }
    private static void Day(Utf8JsonWriter w, string name, DateOnly? value) { if (value == null) w.WriteNull(name); else w.WriteString(name, Date(value.Value)); }
    private static void At(Utf8JsonWriter w, string name, DateTimeOffset? value) { if (value == null) w.WriteNull(name); else w.WriteString(name, Stamp(value.Value)); }
    private static void Guid_(Utf8JsonWriter w, string name, Guid? value) { if (value == null) w.WriteNull(name); else w.WriteString(name, Id(value.Value)); }
    private static void Enum_<T>(Utf8JsonWriter w, string name, T value) where T : struct, Enum => w.WriteString(name, value.ToString());

    private static void Shipment(Utf8JsonWriter w, Shipment s)
    {
        w.WriteStartObject(); Str(w, "reference", s.ReferenceNumber); Guid_(w, "supplierId", s.SupplierId); Guid_(w, "productId", s.ProductId); Dec(w, "quantity", s.Quantity);
        Enum_(w, "unit", s.Unit); Str(w, "originCountryCode", s.OriginCountryCode); Day(w, "expectedArrivalDate", s.ExpectedArrivalDate); Day(w, "receivedDate", s.ReceivedDate); w.WriteEndObject();
    }

    // Fact-bearing content only: not the version id/number, not ProductName, not ProductCatalogueDescription.
    private static void MasterData(Utf8JsonWriter w, CaseMasterDataVersion? m)
    {
        if (m == null) { w.WriteNullValue(); return; }
        w.WriteStartObject();
        w.WriteStartObject("operator"); Str(w, "legalName", m.OperatorLegalName); Str(w, "postalAddress", m.OperatorPostalAddress); Str(w, "email", m.OperatorEmail);
        Str(w, "establishmentCountryCode", m.OperatorEstablishmentCountryCode); Str(w, "eori", m.OperatorEori); w.WriteEndObject();
        w.WriteStartObject("supplier"); Guid_(w, "id", m.SupplierId); Str(w, "name", m.SupplierName); Str(w, "postalAddress", m.SupplierPostalAddress); Str(w, "email", m.SupplierEmail); Str(w, "countryCode", m.SupplierCountryCode); w.WriteEndObject();
        w.WriteStartObject("product"); Guid_(w, "id", m.ProductId); Str(w, "cnCode", m.ProductCnCode); Enum_(w, "commodity", m.ProductCommodity); w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void Applicability(Utf8JsonWriter w, CaseApplicability? a)
    {
        if (a == null) { w.WriteNullValue(); return; }
        w.WriteStartObject(); Guid_(w, "id", a.Id); w.WriteNumber("number", a.Number); Guid_(w, "assessedProductId", a.AssessedProductId); Str(w, "assessedCnCode", a.AssessedCnCode);
        Enum_(w, "assessedCommodity", a.AssessedCommodity); Str(w, "productDescription", a.ProductDescription); w.WriteBoolean("singleRelevantCommodity", a.SingleRelevantCommodity);
        Enum_(w, "activityType", a.ActivityType); Enum_(w, "operatorRole", a.OperatorRole); Str(w, "operatorRoleDescription", a.OperatorRoleDescription);
        Day(w, "activityDate", a.ActivityDate); Enum_(w, "activityDateKind", a.ActivityDateKind); Str(w, "scopeCitation", a.ScopeCitation); Str(w, "qualifications", a.Qualifications);
        Str(w, "legalBasisVersion", a.LegalBasisVersion); Day(w, "effectiveDate", a.EffectiveDate); Str(w, "reasoning", a.Reasoning);
        Enum_(w, "regulatoryScope", a.RegulatoryScope); Enum_(w, "flowSupport", a.FlowSupport); Guid_(w, "decidedByUserId", a.DecidedByUserId); At(w, "decidedAtUtc", a.DecidedAtUtc); w.WriteEndObject();
    }

    private static void Recipients(Utf8JsonWriter w, CaseRecipientStatement? s, IReadOnlyList<CaseRecipient> entries)
    {
        if (s == null) { w.WriteNullValue(); return; }
        w.WriteStartObject(); Guid_(w, "id", s.Id); w.WriteNumber("number", s.Number); Enum_(w, "resolution", s.Resolution); Str(w, "notApplicableBasis", s.NotApplicableBasis);
        w.WriteStartArray("recipients");
        foreach (var r in entries.OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.PostalAddress, StringComparer.Ordinal).ThenBy(r => r.Email, StringComparer.Ordinal))
        { w.WriteStartObject(); Str(w, "name", r.Name); Str(w, "postalAddress", r.PostalAddress); Str(w, "email", r.Email); w.WriteEndObject(); }
        w.WriteEndArray(); w.WriteEndObject();
    }

    // Producer/plot labels, active flags and RevisionSupersededAtUtc are not facts. The pinned revision id is, with its factual metadata and the geometry hash.
    private static void Lot(Utf8JsonWriter w, DossierFacts f, int version)
    {
        if (f.Lot == null) { w.WriteNullValue(); return; }
        var lot = f.Lot; var allocated = f.Contributions.Sum(c => c.Contribution.Quantity);
        w.WriteStartObject(); Guid_(w, "dossierId", f.Dossier?.Id); Guid_(w, "id", lot.Id); w.WriteNumber("number", lot.Number); Str(w, "lotReference", lot.LotReference);
        Dec(w, "quantity", lot.Quantity); Enum_(w, "unit", lot.Unit); Str(w, "note", lot.Note);
        w.WriteStartObject("reconciliation"); Dec(w, "allocatedQuantity", allocated); Dec(w, "remainingQuantity", lot.Quantity - allocated); w.WriteString("status", allocated == lot.Quantity ? "Reconciled" : "UnderAllocated"); w.WriteEndObject();
        w.WriteStartArray("contributions");
        foreach (var c in f.Contributions.OrderBy(c => Id(c.Contribution.ProducerId), StringComparer.Ordinal))
        {
            w.WriteStartObject(); Guid_(w, "producerId", c.Contribution.ProducerId); Dec(w, "quantity", c.Contribution.Quantity); Enum_(w, "unit", c.Contribution.Unit);
            w.WriteStartArray("plots");
            foreach (var l in c.Links.OrderBy(l => Id(l.Link.ProductionPlotId), StringComparer.Ordinal).ThenBy(l => Id(l.Link.PlotRevisionId), StringComparer.Ordinal))
            {
                w.WriteStartObject(); Guid_(w, "plotId", l.Link.ProductionPlotId); Guid_(w, "revisionId", l.Link.PlotRevisionId); w.WriteNumber("revisionNumber", l.Revision.Number);
                Str(w, "countryCode", l.Revision.CountryCode); Enum_(w, "geometryType", l.Revision.GeometryType); Dec(w, "areaHectares", l.Revision.AreaHectares);
                Str(w, "geometrySha256", GeometrySha256(l.Revision.GeometryJson)); Day(w, "productionStartDate", l.Link.ProductionStartDate); Day(w, "productionEndDate", l.Link.ProductionEndDate);
                if (version >= 2) Dec(w, "allocatedQuantity", l.Link.AllocatedQuantity);
                w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteEndObject();
    }

    private static void Requirements(Utf8JsonWriter w, IReadOnlyList<EudrRequirement> requirements)
    {
        w.WriteStartArray();
        foreach (var r in requirements.OrderBy(r => r.Type))
        {
            w.WriteStartObject(); Enum_(w, "type", r.Type); w.WriteBoolean("isRequired", r.IsRequired); Enum_(w, "status", r.Status); Str(w, "note", r.ReviewerNote);
            Guid_(w, "reviewedByUserId", r.ReviewedByUserId); At(w, "reviewedAtUtc", r.ReviewedAtUtc); w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void Evidence(Utf8JsonWriter w, DossierFacts f)
    {
        w.WriteStartObject(); w.WriteStartArray("documents");
        foreach (var d in f.Documents.OrderBy(d => Id(d.Id), StringComparer.Ordinal))
        {
            w.WriteStartObject(); Guid_(w, "id", d.Id); Str(w, "originalFileName", d.OriginalFileName); Str(w, "contentType", d.ContentType); w.WriteNumber("sizeBytes", d.SizeBytes);
            Enum_(w, "category", d.Category); Enum_(w, "uploadedByType", d.UploadedByType); At(w, "uploadedAtUtc", d.UploadedAtUtc); w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WritePropertyName("geolocation");
        if (f.Geolocation == null) w.WriteNullValue();
        else
        {
            var g = f.Geolocation; w.WriteStartObject(); Guid_(w, "id", g.Id); Str(w, "originalFileName", g.OriginalFileName); w.WriteNumber("featureCount", g.FeatureCount); Str(w, "geometryTypes", g.GeometryTypes);
            Enum_(w, "validationStatus", g.ValidationStatus); At(w, "uploadedAtUtc", g.UploadedAtUtc); w.WriteEndObject();
        }
        w.WritePropertyName("supplierSubmission");
        if (f.Submission == null) w.WriteNullValue();
        else
        {
            var s = f.Submission; w.WriteStartObject(); Guid_(w, "id", s.Id); Str(w, "countryOfProductionCode", s.CountryOfProductionCode); Day(w, "productionPeriodFrom", s.ProductionPeriodFrom);
            Day(w, "productionPeriodTo", s.ProductionPeriodTo); Dec(w, "confirmedQuantity", s.ConfirmedQuantity); Str(w, "notes", s.Notes); At(w, "submittedAtUtc", s.SubmittedAtUtc); w.WriteEndObject();
        }
        w.WriteStartArray("evidenceLinks");
        foreach (var l in f.EvidenceLinks.Where(l => l.WithdrawnAtUtc == null).OrderBy(l => Id(l.Id), StringComparer.Ordinal))
        { w.WriteStartObject(); Guid_(w, "id", l.Id); Guid_(w, "documentId", l.CaseDocumentId); Enum_(w, "purpose", l.Purpose); Str(w, "note", l.Note); w.WriteEndObject(); }
        w.WriteEndArray(); w.WriteEndObject();
    }
}
