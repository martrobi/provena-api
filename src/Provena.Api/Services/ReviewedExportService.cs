using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
using Provena.Api.Infrastructure;
namespace Provena.Api.Services;
// The reviewed export (ADR 0008): built ONLY from the immutable CaseRevision and the rows it pins. It never rebuilds the working dossier, never reads the live
// catalogue, and never filters pinned history through Current() semantics: a document withdrawn after the review, or a plot revision superseded after it, is still
// exported exactly as reviewed. Party and product values come from the stored snapshot (the case master-data copy frozen at review time). No case lock is needed:
// everything read is immutable. The only write is the audit event CaseReviewExported (ids only). Deterministic apart from ZIP timestamps and ExportedAtUtc.
public sealed class ReviewedExportService(AppDbContext db, ReviewReadService reads, IFileStorage storage, AuditService audit)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public async Task<ExportResult> Export(Guid caseId, Guid reviewId, CancellationToken ct)
    {
        var (revision, record) = await reads.Load(caseId, reviewId, ct);
        var snapshot = ReviewSnapshot.Deserialize(revision.SnapshotJson, revision.SnapshotSchemaVersion);
        var documents = await (from pin in db.CaseRevisionDocuments.AsNoTracking() join d in db.Documents.AsNoTracking() on pin.CaseDocumentId equals d.Id
                               where pin.CaseRevisionId == revision.Id orderby d.Id select d).ToListAsync(ct);
        var revisionIds = snapshot.Facts.Lot?.Contributions.SelectMany(c => c.Plots).Select(p => p.RevisionId).Distinct().ToList() ?? [];
        var plotRevisions = revisionIds.Count == 0 ? [] : await db.PlotRevisions.AsNoTracking().Where(x => revisionIds.Contains(x.Id)).OrderBy(x => x.Id).ToListAsync(ct);
        var geolocation = revision.GeoSubmissionId == null ? null : await db.Geolocations.AsNoTracking().SingleAsync(x => x.Id == revision.GeoSubmissionId, ct);
        var dto = new ReviewExportDto(record.Id, revision.Id, revision.Number, record.Decision, record.Rationale, record.ReviewerUserId, record.ReviewedAtUtc, revision.PolicyId, record.PolicyVersion, revision.Fingerprint,
            revision.FingerprintVersion, revision.SnapshotSchemaVersion, ReviewSnapshot.DeserializeHashes(revision.SectionHashes), snapshot, DateTimeOffset.UtcNow,
            $"Reviewed dossier snapshot under policy {revision.PolicyId} version {record.PolicyVersion}; not a legal compliance determination, risk assessment or DDS.");

        audit.Add(caseId, "CaseReviewExported", $"Review {record.Id} (revision {revision.Id}, number {revision.Number}) exported.");
        await db.SaveChangesAsync(ct);

        var temp = Path.Combine(Path.GetTempPath(), $"provena-{Guid.NewGuid():N}.zip");
        var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            var reference = SafeName(snapshot.Facts.Shipment.Reference);
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var prefix = $"case-{reference}-review-{revision.Number}/";
                await using (var target = zip.CreateEntry(prefix + "review.json").Open()) await JsonSerializer.SerializeAsync(target, dto, Json, ct);
                foreach (var d in documents)
                { await using var input = await storage.OpenReadAsync(d.StorageKey, ct); await using var output = zip.CreateEntry(prefix + $"documents/{d.Id:N}-{SafeName(d.OriginalFileName)}").Open(); await input.CopyToAsync(output, ct); }
                foreach (var r in plotRevisions)
                { await using var output = zip.CreateEntry(prefix + $"plots/{r.Id:N}.geojson").Open(); await output.WriteAsync(Encoding.UTF8.GetBytes(r.GeometryJson), ct); }
                if (geolocation != null)
                { await using var input = await storage.OpenReadAsync(geolocation.StorageKey, ct); await using var output = zip.CreateEntry(prefix + "geolocation/original.geojson").Open(); await input.CopyToAsync(output, ct); }
            }
            stream.Position = 0;
            return new(stream, $"provena-{reference}-review-{revision.Number}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.zip");
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    private static string SafeName(string value)
    { var name = Regex.Replace(value, "[^a-zA-Z0-9._-]", "_").Trim('.'); return string.IsNullOrEmpty(name) ? "case" : name[..Math.Min(120, name.Length)]; }
}
