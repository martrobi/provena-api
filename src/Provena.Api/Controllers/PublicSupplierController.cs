using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Provena.Api.Contracts;
using Provena.Api.Domain;
using Provena.Api.Services;
namespace Provena.Api.Controllers;
[ApiController, AllowAnonymous, Route("public/supplier-requests/{token}"), Tags("Public supplier API"), EnableRateLimiting("supplier")]
[RequestSizeLimit(21 * 1024 * 1024), RequestFormLimits(MultipartBodyLengthLimit = 21 * 1024 * 1024)]
public sealed class PublicSupplierController(SupplierRequestService service, CaseFileService files) : ControllerBase
{
    [HttpGet] public Task<object> Get(string token, CancellationToken ct) => service.PublicDetails(token, ct);
    [HttpPut("details")] public async Task<IActionResult> Details(string token, SubmissionInput input, CancellationToken ct) { await service.SaveDetails(token, input, ct); return NoContent(); }
    [HttpPost("documents"), Consumes("multipart/form-data")]
    public Task<object> Document(string token, IFormFile file, CancellationToken ct) => files.Upload(null, token, file, DocumentCategory.SupportingDocument, false, ct);
    [HttpPost("geolocation"), Consumes("multipart/form-data")]
    public Task<object> Geo(string token, IFormFile file, CancellationToken ct) => files.Upload(null, token, file, DocumentCategory.Other, true, ct);
    [HttpPost("complete")] public async Task<IActionResult> Complete(string token, CancellationToken ct) { await service.Complete(token, ct); return NoContent(); }
}
