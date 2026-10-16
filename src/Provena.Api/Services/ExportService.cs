using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Provena.Api.Data;
using Provena.Api.Infrastructure;
namespace Provena.Api.Services;
public sealed record ExportResult(Stream Stream, string FileName);
public sealed class ExportService(AppDbContext db, CaseService cases, CaseDossierService dossiers, CompletenessService completeness, CaseLifecycleReadService lifecycle, RiskAssessmentService risk, IFileStorage storage, AuditService audit)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public async Task<ExportResult> Export(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var c = await cases.Lock(id, ct);
        audit.Add(id, "CaseExported", "Case package exported; this is not a DDS submission.");
        await db.SaveChangesAsync(ct);
        var details = await cases.Details(id, ct);
        var org = await db.Organizations.AsNoTracking().SingleAsync(x => x.Id == c.OrganizationId, ct);
        var docs = await db.Documents.AsNoTracking().Current().Where(x => x.EudrCaseId == id).ToListAsync(ct);
        var geo = await db.Geolocations.AsNoTracking().Current().SingleOrDefaultAsync(x => x.EudrCaseId == id, ct);
        var withdrawnDocuments = await db.Documents.CountAsync(x => x.EudrCaseId == id && x.WithdrawnAtUtc != null, ct);
        var supersededGeolocations = await db.Geolocations.CountAsync(x => x.EudrCaseId == id && x.SupersededAtUtc != null, ct);
        var supersededSupplierSubmissions = await db.Submissions.CountAsync(x => x.EudrCaseId == id && x.SupersededAtUtc != null, ct);
        var supersededLotVersions = await db.CaseLots.CountAsync(x => x.SupersededAtUtc != null && db.CaseDossiers.Any(d => d.Id == x.CaseDossierId && d.EudrCaseId == id), ct);
        var dossier = await dossiers.Dossier(id, ct);
        var currentCompleteness = await completeness.Compute(c, ct); // inside the case-locked transaction, so it matches the rest of the package
        var lifecycleHistory = await lifecycle.Build(c, ct); // closures with their reviews and reopenings and the archive periods, read under the same case lock
        var riskOverview = await risk.Build(c, ct); // the same read model (and freshness) as GET risk, built under the same case lock
        // Distinct pinned revisions of the current lot only; the stored normalized geometry is written once per revision.
        var pinnedIds = dossier?.CurrentLot.Contributions.SelectMany(x => x.Plots).Select(x => x.PlotRevisionId).Distinct().ToList() ?? [];
        var pinnedRevisions = pinnedIds.Count == 0 ? [] : await db.PlotRevisions.AsNoTracking().Where(x => pinnedIds.Contains(x.Id)).OrderBy(x => x.Id).ToListAsync(ct);
        var reference = SafeName(details.Shipment.ReferenceNumber);
        // Disk-backed temporary stream avoids loading the entire package into RAM.
        var temp = Path.Combine(Path.GetTempPath(), $"provena-{Guid.NewGuid():N}.zip");
        var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var prefix = $"case-{reference}/";
                var manifest = zip.CreateEntry(prefix + "manifest.json");
                await using (var target = manifest.Open())
                    await JsonSerializer.SerializeAsync(target, new
                    {
                        organization = new { org.Id, org.Name, org.Oib },
                        shipment = details.Shipment, supplier = details.Supplier, product = details.Product,
                        supplierSubmission = details.SupplierSubmission, requirements = details.Requirements,
                        documents = details.Documents, geolocation = details.Geolocation,
                        caseId = details.Id, status = details.Status,
                        createdAtUtc = details.CreatedAtUtc, updatedAtUtc = details.UpdatedAtUtc,
                        auditEvents = details.AuditEvents, lifecycle = lifecycleHistory,
                        risk = new
                        {
                            riskOverview.Freshness, riskOverview.StaleReason, riskOverview.ChangedAreas, riskOverview.LatestReviewId, riskOverview.LatestReviewBacksClosure,
                            latestAssessmentId = riskOverview.LatestAssessment?.AssessmentId,
                            assessments = riskOverview.Assessments.OrderBy(a => a.Number).ToList(), mitigations = riskOverview.Mitigations.OrderBy(m => m.Number).ToList(),
                        },
                        riskScope = "Retained risk decisions and mitigation measures of the working record, oldest first, with their full text; freshness is derived from the latest review. Human-entered records, not a regulatory approval or compliance determination. Not part of the reviewed export.",
                        exportedAtUtc = DateTimeOffset.UtcNow,
                        notice = "Operational documentation only. ReadyForExport is not a legal EUDR compliance determination.",
                        evidenceScope = "Current working evidence at export time. This is not a reviewed dossier snapshot; withdrawn or superseded versions are retained in Provena and are not included.",
                        dossier,
                        dossierScope = "Current operator dossier lot at export time; not a reviewed dossier snapshot and not a completeness or compliance determination.",
                        retainedNotIncluded = new { withdrawnDocuments, supersededGeolocations, supersededSupplierSubmissions, supersededLotVersions },
                        completeness = currentCompleteness,
                        latestReview = currentCompleteness.LatestReview,
                        reviewScope = "Working-current export. Not the reviewed record; use the review export for what was reviewed."
                    }, Json, ct);
                var summary = zip.CreateEntry(prefix + "summary.csv");
                await using (var writer = new StreamWriter(summary.Open(), new UTF8Encoding(true)))
                {
                    await writer.WriteLineAsync("referenceNumber,supplier,product,cnCode,quantity,unit,status".AsMemory(), ct);
                    var line = string.Join(',', new[] { details.Shipment.ReferenceNumber, details.Supplier.Name, details.Product.Name, details.Product.CnCode, details.Shipment.Quantity.ToString(CultureInfo.InvariantCulture), details.Shipment.Unit.ToString(), details.Status.ToString() }.Select(Csv));
                    await writer.WriteLineAsync(line.AsMemory(), ct);
                }
                foreach (var d in docs)
                { await using var input = await storage.OpenReadAsync(d.StorageKey, ct); await using var output = zip.CreateEntry(prefix + $"documents/{d.Id:N}-{SafeName(d.OriginalFileName)}").Open(); await input.CopyToAsync(output, ct); }
                foreach (var revision in pinnedRevisions)
                { await using var output = zip.CreateEntry(prefix + $"plots/{revision.Id:N}.geojson").Open(); await output.WriteAsync(Encoding.UTF8.GetBytes(revision.GeometryJson), ct); }
                if (geo != null)
                { await using var input = await storage.OpenReadAsync(geo.StorageKey, ct); await using var output = zip.CreateEntry(prefix + "geolocation/original.geojson").Open(); await input.CopyToAsync(output, ct); }
            }
            await tx.CommitAsync(ct); stream.Position = 0; return new(stream, $"provena-{reference}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.zip");
        }
        catch { await stream.DisposeAsync(); throw; }
    }
    private static string SafeName(string value)
    { var name = Regex.Replace(value, "[^a-zA-Z0-9._-]", "_").Trim('.'); return string.IsNullOrEmpty(name) ? "case" : name[..Math.Min(120, name.Length)]; }
    private static string Csv(string value)
    { if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value; return "\"" + value.Replace("\"", "\"\"") + "\""; }
}
