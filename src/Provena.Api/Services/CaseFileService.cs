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
        else { c = await cases.Lock(caseId!.Value, ct); CaseService.EnsureEditable(c); }
        FileStorageResult? saved = null; string? obsoleteKey = null;
        try
        {
            await using var input = file.OpenReadStream(); saved = await storage.SaveAsync(input, extension, ct);
            object result;
            var originalName = Path.GetFileName(file.FileName.Replace('\\', '/'));
            if (geo)
            {
                var g = await db.Geolocations.SingleOrDefaultAsync(x => x.EudrCaseId == c.Id, ct);
                if (g == null) { g = new GeoSubmission { OrganizationId = tenant.OrganizationId, EudrCaseId = c.Id }; db.Geolocations.Add(g); }
                else obsoleteKey = g.StorageKey;
                g.OriginalFileName = originalName; g.StorageKey = saved.StorageKey; g.RawGeoJson = validation!.NormalizedJson; g.FeatureCount = validation.FeatureCount;
                g.GeometryTypes = validation.GeometryTypes; g.ValidationStatus = GeoValidationStatus.Valid; g.UploadedAtUtc = DateTimeOffset.UtcNow; g.SupplierRequestId = request?.Id;
                await requests.SetRequirement(c.Id, RequirementType.Geolocation, true, ct); audit.Add(c.Id, "GeoJsonUploaded", "Valid GeoJSON uploaded.", request?.Id); result = g.ToDto();
            }
            else
            {
                var d = new CaseDocument { OrganizationId = tenant.OrganizationId, EudrCaseId = c.Id, SupplierRequestId = request?.Id, OriginalFileName = originalName, StoredFileName = saved.StoredFileName, StorageKey = saved.StorageKey, ContentType = file.ContentType.Split(';')[0], SizeBytes = saved.SizeBytes, Category = category, UploadedByType = request == null ? UploadedByType.InternalUser : UploadedByType.Supplier };
                db.Documents.Add(d); await requests.SetRequirement(c.Id, RequirementType.SupportingDocuments, true, ct); audit.Add(c.Id, "DocumentUploaded", $"Document {d.Id} uploaded ({d.SizeBytes} bytes).", request?.Id); result = d.ToDto();
            }
            if (request != null) { request.CompletedAtUtc = null; c.SupplierSubmissionCompletedAtUtc = null; }
            await statuses.Recalculate(c, ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            if (obsoleteKey != null) await TryDelete(obsoleteKey);
            return result;
        }
        catch { if (saved != null) await TryDelete(saved.StorageKey); throw; }
    }
    public async Task<List<DocumentDto>> List(Guid caseId, CancellationToken ct)
    { if (!await db.Cases.AnyAsync(x => x.Id == caseId, ct)) throw ApiException.NotFound(); return (await db.Documents.AsNoTracking().Where(x => x.EudrCaseId == caseId).ToListAsync(ct)).Select(x => x.ToDto()).ToList(); }
    public async Task<GeoDto?> Geo(Guid caseId, CancellationToken ct)
    { if (!await db.Cases.AnyAsync(x => x.Id == caseId, ct)) throw ApiException.NotFound(); return (await db.Geolocations.AsNoTracking().SingleOrDefaultAsync(x => x.EudrCaseId == caseId, ct))?.ToDto(); }
    public async Task<(Stream Stream, string ContentType, string FileName)> Download(Guid caseId, Guid documentId, CancellationToken ct)
    { var d = await db.Documents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == documentId && x.EudrCaseId == caseId, ct) ?? throw ApiException.NotFound(); return (await storage.OpenReadAsync(d.StorageKey, ct), d.ContentType, d.OriginalFileName); }
    public async Task Delete(Guid caseId, Guid documentId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var c = await cases.Lock(caseId, ct); CaseService.EnsureEditable(c);
        var d = await db.Documents.SingleOrDefaultAsync(x => x.Id == documentId && x.EudrCaseId == caseId, ct) ?? throw ApiException.NotFound();
        db.Documents.Remove(d); var hasOther = await db.Documents.AnyAsync(x => x.EudrCaseId == caseId && x.Id != documentId, ct);
        await requests.SetRequirement(caseId, RequirementType.SupportingDocuments, hasOther, ct);
        audit.Add(caseId, "DocumentDeleted", $"Document {documentId} deleted."); await statuses.Recalculate(c, ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); await TryDelete(d.StorageKey);
    }
    private async Task TryDelete(string key)
    { try { await storage.DeleteAsync(key, CancellationToken.None); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { logger.LogError("File cleanup failed for generated key {StorageKey}", key); } }
}
