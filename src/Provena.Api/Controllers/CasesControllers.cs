using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Provena.Api.Contracts;
using Provena.Api.Domain;
using Provena.Api.Services;
namespace Provena.Api.Controllers;
[ApiController, Authorize, Route("api/shipments")]
public sealed class ShipmentsController(ShipmentService service) : ControllerBase
{
    [HttpGet] public Task<List<Shipment>> List(CancellationToken ct) => service.List(ct);
    [HttpGet("{id:guid}")] public Task<object> Get(Guid id, CancellationToken ct) => service.Get(id, ct);
    [HttpPost] public async Task<IActionResult> Create(ShipmentInput input, CancellationToken ct) => StatusCode(201, await service.Save(null, input, ct));
    [HttpPut("{id:guid}")] public Task<object> Update(Guid id, ShipmentInput input, CancellationToken ct) => service.Save(id, input, ct);
    // Archival is deliberately used even before the first request, preserving append-only audit history.
    [HttpDelete("{id:guid}")] public async Task<IActionResult> Archive(Guid id, CancellationToken ct) { await service.Archive(id, ct); return NoContent(); }
}
[ApiController, Authorize, Route("api/eudr-cases")]
public sealed class CasesController(CaseService service, SupplierRequestService requests, ExportService exports) : ControllerBase
{
    [HttpGet] public Task<PageResult<CaseListItem>> List([FromQuery] EudrCaseStatus? status, [FromQuery] Guid? supplierId, [FromQuery] Guid? productId, [FromQuery] string? search, CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 25) => service.List(status, supplierId, productId, search, page, pageSize, ct);
    [HttpGet("{id:guid}")] public Task<CaseDetails> Details(Guid id, CancellationToken ct) => service.Details(id, ct);
    [HttpPatch("{caseId:guid}/requirements/{requirementId:guid}")] public async Task<IActionResult> Review(Guid caseId, Guid requirementId, ReviewInput input, CancellationToken ct) { await service.Review(caseId, requirementId, input, ct); return NoContent(); }
    [HttpPost("{caseId:guid}/supplier-request")] public Task<RequestLink> CreateSupplierRequest(Guid caseId, CancellationToken ct) => requests.Create(caseId, ct);
    [HttpPost("{caseId:guid}/complete")] public async Task<IActionResult> Complete(Guid caseId, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] CompleteInput? input, CancellationToken ct) { await service.Complete(caseId, input ?? new(null), ct); return NoContent(); }
    [HttpGet("{caseId:guid}/export")] public async Task<IActionResult> Export(Guid caseId, CancellationToken ct) { var file = await exports.Export(caseId, ct); return File(file.Stream, "application/zip", file.FileName); }
}
[ApiController, Authorize, Route("api/dashboard")]
public sealed class DashboardController(CaseService service) : ControllerBase
{
    [HttpGet] public Task<object> Get(CancellationToken ct) => service.Dashboard(ct);
}
[ApiController, Authorize, Route("api/supplier-requests")]
public sealed class SupplierRequestsController(SupplierRequestService service) : ControllerBase
{
    [HttpPost("{id:guid}/revoke")] public async Task<IActionResult> Revoke(Guid id, CancellationToken ct) { await service.Revoke(id, ct); return NoContent(); }
    [HttpPost("{id:guid}/regenerate")] public Task<RequestLink> Regenerate(Guid id, CancellationToken ct) => service.Regenerate(id, ct);
}
