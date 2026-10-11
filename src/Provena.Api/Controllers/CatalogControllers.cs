using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Provena.Api.Contracts;
using Provena.Api.Domain;
using Provena.Api.Services;
namespace Provena.Api.Controllers;
[ApiController, Authorize, Route("api/suppliers")]
public sealed class SuppliersController(CatalogService service) : ControllerBase
{
    [HttpGet] public Task<List<Supplier>> List([FromQuery] string? search, CancellationToken ct) => service.Suppliers(search, ct);
    [HttpGet("{id:guid}")] public Task<Supplier> Get(Guid id, CancellationToken ct) => service.Supplier(id, ct);
    [HttpPost] public async Task<IActionResult> Create(SupplierInput input, CancellationToken ct) { var value = await service.SaveSupplier(null, input, ct); return CreatedAtAction(nameof(Get), new { id = value.Id }, value); }
    [HttpPut("{id:guid}")] public Task<Supplier> Update(Guid id, SupplierInput input, CancellationToken ct) => service.SaveSupplier(id, input, ct);
    [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await service.DeleteSupplier(id, ct); return NoContent(); }
}
[ApiController, Authorize, Route("api/products")]
public sealed class ProductsController(CatalogService service) : ControllerBase
{
    [HttpGet] public Task<List<Product>> List([FromQuery] string? search, CancellationToken ct) => service.Products(search, ct);
    [HttpGet("{id:guid}")] public Task<Product> Get(Guid id, CancellationToken ct) => service.Product(id, ct);
    [HttpPost] public async Task<IActionResult> Create(ProductInput input, CancellationToken ct) { var value = await service.SaveProduct(null, input, ct); return CreatedAtAction(nameof(Get), new { id = value.Id }, value); }
    [HttpPut("{id:guid}")] public Task<Product> Update(Guid id, ProductInput input, CancellationToken ct) => service.SaveProduct(id, input, ct);
    [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await service.DeleteProduct(id, ct); return NoContent(); }
}
