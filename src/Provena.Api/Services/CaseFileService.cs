using Microsoft.EntityFrameworkCore;
using Provena.Api.Auth;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
using Provena.Api.Infrastructure;
namespace Provena.Api.Services;
public sealed class CaseFileService(AppDbContext db, TenantContext tenant, CaseService cases, SupplierRequestService requests,
    EudrCaseStatusService statuses, IFileStorage storage, FileValidationService files, GeoJsonValidationService geos, AuditService audit, ILogger<CaseFileService> logger)
{
    public async Task<object> Upload(Guid? caseId, string? token, IFormFile file, DocumentCategory category, bool geo, CancellationToken ct)
    {
        var extension = files.Validate(file, geo);
        if (!Enum.IsDefined(category)) throw new ApiException(400, "Invalid document category.");
        GeoValidationResult? validation = null;
        if (geo)
        {
            using var reader = new StreamReader(file.OpenReadStream());
            try { validation = geos.Validate(await reader.ReadToEndAsync(ct)); }
            catch (ApiException) { logger.LogWarning("GeoJSON validation failed; content omitted"); throw; }
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        SupplierRequest? request = null; EudrCase c;
        if (token != null) { (request, c) = await requests.LockPublic(token, ct); }
        else { c = await cases.Lock(caseId!.Value, ct); await cases.EnsureMutable(c, ct); }
        FileStorageResult? saved = null; var commitAttempted = false;
        try
        {
            await using var input = file.OpenReadStream(); saved = await storage.SaveAsync(input, extension, ct);
            object result;
            var originalName = Path.GetFileName(file.FileName.Replace('\\', '/'));
            if (geo)
            {
                // Append-only: retire the current geometry (row and bytes are retained), then insert the new one. The retirement is saved first so the partial unique index is never violated.
                var previous = await db.Geolocations.Current().SingleOrDefaultAsync(x => x.EudrCaseId == c.Id, ct);
                if (previous != null) { previous.SupersededAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); }
                var g = new GeoSubmission
                {
                    OrganizationId = tenant.OrganizationId, EudrCaseId = c.Id, OriginalFileName = originalName, StorageKey = saved.StorageKey, RawGeoJson = validation!.NormalizedJson,
                    FeatureCount = validation.FeatureCount, GeometryTypes = validation.GeometryTypes, ValidationStatus = GeoValidationStatus.Valid, UploadedAtUtc = DateTimeOffset.UtcNow,
                    SupplierRequestId = request?.Id, UploadedByUserId = request == null ? tenant.UserId : null
                };
                db.Geolocations.Add(g);
                await requests.SetRequirement(c.Id, RequirementType.Geolocation, true, ct); audit.Add(c.Id, "GeoJsonUploaded", "Valid GeoJSON uploaded.", request?.Id); result = g.ToDto();
            }
            else
            {
                var d = new CaseDocument { OrganizationId = tenant.OrganizationId, EudrCaseId = c.Id, SupplierRequestId = request?.Id, OriginalFileName = originalName, StoredFileName = saved.StoredFileName, StorageKey = saved.StorageKey, ContentType = file.ContentType.Split(';')[0], SizeBytes = saved.SizeBytes, Category = category, UploadedByType = request == null ? UploadedByType.InternalUser : UploadedByType.Supplier, UploadedByUserId = request == null ? tenant.UserId : null };
                db.Documents.Add(d); await requests.SetRequirement(c.Id, RequirementType.SupportingDocuments, true, ct); audit.Add(c.Id, "DocumentUploaded", $"Document {d.Id} uploaded ({d.SizeBytes} bytes).", request?.Id); result = d.ToDto();
            }
            if (request != null) { request.CompletedAtUtc = null; c.SupplierSubmissionCompletedAtUtc = null; }
            await statuses.Recalculate(c, ct); await db.SaveChangesAsync(ct); commitAttempted = true; await tx.CommitAsync(ct);
            return result;
        }
        // Cleanup only for failures before the commit was attempted. After that the outcome may be ambiguous (the server could have committed), so bytes are kept: orphaned bytes are safer than deleting committed evidence.
        catch { if (saved != null && !commitAttempted) await TryDelete(saved.StorageKey); throw; }
    }
    public async Task<List<DocumentDto>> List(Guid caseId, CancellationToken ct)
    { if (!await db.Cases.AnyAsync(x => x.Id == caseId, ct)) throw ApiException.NotFound(); return (await db.Documents.AsNoTracking().Current().Where(x => x.EudrCaseId == caseId).ToListAsync(ct)).Select(x => x.ToDto()).ToList(); }
    public async Task<GeoDto?> Geo(Guid caseId, CancellationToken ct)
    { if (!await db.Cases.AnyAsync(x => x.Id == caseId, ct)) throw ApiException.NotFound(); return (await db.Geolocations.AsNoTracking().Current().SingleOrDefaultAsync(x => x.EudrCaseId == caseId, ct))?.ToDto(); }
    // Internal, read-only: every retained version of the case's evidence, newest first (unpaginated: pilot-scale assumption, see API.md). Tenant filter applies; a foreign or unknown case is 404.
    public async Task<EvidenceHistory> History(Guid caseId, CancellationToken ct)
    {
        if (!await db.Cases.AnyAsync(x => x.Id == caseId, ct)) throw ApiException.NotFound();
        var docs = await db.Documents.AsNoTracking().Where(x => x.EudrCaseId == caseId).OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).ToListAsync(ct);
        var geos = await db.Geolocations.AsNoTracking().Where(x => x.EudrCaseId == caseId).OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).ToListAsync(ct);
        var subs = await db.Submissions.AsNoTracking().Where(x => x.EudrCaseId == caseId).OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).ToListAsync(ct);
        return new(docs.Select(x => x.ToDto()).ToList(), geos.Select(x => x.ToDto()).ToList(), subs.Select(x => x.ToVersionDto()).ToList());
    }
    // Original bytes of any retained geometry version of this case (current or superseded).
    public async Task<(Stream Stream, string FileName)> GeoFile(Guid caseId, Guid geolocationId, CancellationToken ct)
    { var g = await db.Geolocations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == geolocationId && x.EudrCaseId == caseId, ct) ?? throw ApiException.NotFound(); return (await storage.OpenReadAsync(g.StorageKey, ct), g.OriginalFileName); }
    public async Task<(Stream Stream, string ContentType, string FileName)> Download(Guid caseId, Guid documentId, CancellationToken ct)
    { var d = await db.Documents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == documentId && x.EudrCaseId == caseId, ct) ?? throw ApiException.NotFound(); return (await storage.OpenReadAsync(d.StorageKey, ct), d.ContentType, d.OriginalFileName); }
    public async Task Delete(Guid caseId, Guid documentId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var c = await cases.Lock(caseId, ct); await cases.EnsureMutable(c, ct);
        var d = await db.Documents.SingleOrDefaultAsync(x => x.Id == documentId && x.EudrCaseId == caseId, ct) ?? throw ApiException.NotFound();
        if (d.WithdrawnAtUtc != null) return; // Already withdrawn: idempotent no-op (no audit event, no requirement change).
        // Withdrawal, not deletion: the row and the stored bytes are retained as evidence.
        d.WithdrawnAtUtc = DateTimeOffset.UtcNow; d.WithdrawnByUserId = tenant.UserId;
        var hasOther = await db.Documents.Current().AnyAsync(x => x.EudrCaseId == caseId && x.Id != documentId, ct);
        await requests.SetRequirement(caseId, RequirementType.SupportingDocuments, hasOther, ct);
        audit.Add(caseId, "DocumentWithdrawn", $"Document {documentId} withdrawn from the current dossier; file retained."); await statuses.Recalculate(c, ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    private async Task TryDelete(string key)
    { try { await storage.DeleteAsync(key, CancellationToken.None); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { logger.LogError("File cleanup failed for generated key {StorageKey}", key); } }
}
