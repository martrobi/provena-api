using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Provena.Api.Contracts;
using Provena.Api.Domain;
using Provena.Api.Services;
namespace Provena.Api.Controllers;
[ApiController, Authorize, Route("api/eudr-cases/{caseId:guid}")]
[RequestSizeLimit(21 * 1024 * 1024), RequestFormLimits(MultipartBodyLengthLimit = 21 * 1024 * 1024)]
public sealed class FilesController(CaseFileService service) : ControllerBase
{
    [HttpPost("documents"), Consumes("multipart/form-data")]
    public Task<object> Upload(Guid caseId, IFormFile file, [FromForm] DocumentCategory category, CancellationToken ct) => service.Upload(caseId, null, file, category, false, ct);
    [HttpGet("documents")] public Task<List<DocumentDto>> List(Guid caseId, CancellationToken ct) => service.List(caseId, ct);
    [HttpGet("documents/{documentId:guid}")] public async Task<IActionResult> Download(Guid caseId, Guid documentId, CancellationToken ct) { var d = await service.Download(caseId, documentId, ct); Response.Headers["X-Content-Type-Options"] = "nosniff"; return File(d.Stream, d.ContentType, d.FileName); }
    [HttpDelete("documents/{documentId:guid}")] public async Task<IActionResult> Delete(Guid caseId, Guid documentId, CancellationToken ct) { await service.Delete(caseId, documentId, ct); return NoContent(); }
    [HttpPost("geolocation"), Consumes("multipart/form-data")]
    public Task<object> GeoUpload(Guid caseId, IFormFile file, CancellationToken ct) => service.Upload(caseId, null, file, DocumentCategory.Other, true, ct);
    [HttpGet("geolocation")] public Task<GeoDto?> Geo(Guid caseId, CancellationToken ct) => service.Geo(caseId, ct);
}
[ApiController, Authorize, Route("api/import")]
[RequestSizeLimit(21 * 1024 * 1024), RequestFormLimits(MultipartBodyLengthLimit = 21 * 1024 * 1024)]
public sealed class ImportsController(ImportService service) : ControllerBase
{
    [HttpPost("suppliers"), Consumes("multipart/form-data")] public Task<ImportResult> Suppliers(IFormFile file, CancellationToken ct) => service.Import("suppliers", file, ct);
    [HttpPost("products"), Consumes("multipart/form-data")] public Task<ImportResult> Products(IFormFile file, CancellationToken ct) => service.Import("products", file, ct);
    [HttpPost("shipments"), Consumes("multipart/form-data")] public Task<ImportResult> Shipments(IFormFile file, CancellationToken ct) => service.Import("shipments", file, ct);
}
