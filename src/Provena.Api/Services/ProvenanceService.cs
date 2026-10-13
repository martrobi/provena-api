using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Provena.Api.Auth;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;
// Internal producer / production plot / plot revision provenance. Plot locations live only in immutable PlotRevisions; a revision is created
// only for a material change. Audit text carries identifiers only: coordinates and geometry are never logged or placed in audit events.
public sealed class ProvenanceService(AppDbContext db, TenantContext tenant, IValidator<ProducerInput> producers, IValidator<ProductionPlotInput> plots,
    IValidator<ProductionPlotUpdateInput> plotUpdates, IValidator<PlotRevisionInput> revisions, GeoJsonValidationService geo, AuditService audit)
{
    private const string ProducerNotFound = "Producer was not found in your organization.";
    public Task<PageResult<ProducerDto>> Producers(string? search, bool? isActive, int page, int pageSize, CancellationToken ct)
    {
        if (search?.Length > 200) throw new ApiException(400, "Search is limited to 200 characters.");
        var query = db.Producers.AsNoTracking();
        if (isActive.HasValue) query = query.Where(x => x.IsActive == isActive.Value);
        if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim().ToLowerInvariant(); query = query.Where(x => x.Name.ToLower().Contains(term) || (x.ExternalReference != null && x.ExternalReference.ToLower().Contains(term))); }
        return query.OrderBy(x => x.Name).ThenBy(x => x.Id).ToPageAsync(x => new ProducerDto(x.Id, x.Name, x.ExternalReference, x.CountryCode, x.Notes, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc), page, pageSize, ct);
    }
    public async Task<ProducerDto> Producer(Guid id, CancellationToken ct) => (await db.Producers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound()).ToDto();
    public async Task<ProducerDto> SaveProducer(Guid? id, ProducerInput input, CancellationToken ct)
    {
        var p = id.HasValue ? await db.Producers.SingleOrDefaultAsync(x => x.Id == id.Value, ct) ?? throw ApiException.NotFound() : new Producer { OrganizationId = tenant.OrganizationId, CreatedByUserId = tenant.UserId };
        await producers.ValidateAndThrowAsync(input, ct); // After the route resource is resolved: unknown or foreign ids stay 404 whatever the body.
        if (id.HasValue && input.IsActive is null) throw RequiredIsActive(); // PUT must state isActive explicitly so an omission can never reactivate.
        p.Name = input.Name.Trim(); p.ExternalReference = NullIfBlank(input.ExternalReference); p.CountryCode = NullIfBlank(input.CountryCode); p.Notes = input.Notes;
        if (id.HasValue) p.IsActive = input.IsActive!.Value; else db.Producers.Add(p); // A new producer is always active.
        audit.Add(null, id.HasValue ? "ProducerUpdated" : "ProducerCreated", $"Producer {p.Id} saved.");
        await db.SaveChangesAsync(ct); return p.ToDto();
    }

    public async Task<PageResult<ProductionPlotListItem>> Plots(Guid? producerId, string? search, bool? isActive, int page, int pageSize, CancellationToken ct)
    {
        if (search?.Length > 200) throw new ApiException(400, "Search is limited to 200 characters.");
        var query = db.ProductionPlots.AsNoTracking();
        if (producerId.HasValue) query = query.Where(x => x.ProducerId == producerId.Value); // Unknown or foreign ids simply match nothing.
        if (isActive.HasValue) query = query.Where(x => x.IsActive == isActive.Value);
        if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim().ToLowerInvariant(); query = query.Where(x => x.Name.ToLower().Contains(term) || (x.ExternalReference != null && x.ExternalReference.ToLower().Contains(term))); }
        var result = await query.OrderBy(x => x.Name).ThenBy(x => x.Id).ToPageAsync(page, pageSize, ct);
        var ids = result.Items.Select(x => x.Id).ToList();
        var current = await db.PlotRevisions.AsNoTracking().Current().Where(x => ids.Contains(x.ProductionPlotId)).ToDictionaryAsync(x => x.ProductionPlotId, ct);
        return new(result.Items.Select(x => x.ToListItem(current[x.Id])).ToList(), result.Page, result.PageSize, result.TotalCount);
    }
    public async Task<ProductionPlotDto> Plot(Guid id, CancellationToken ct)
    {
        var plot = await db.ProductionPlots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound();
        return plot.ToDto(await db.PlotRevisions.AsNoTracking().Current().SingleAsync(x => x.ProductionPlotId == id, ct));
    }
    public async Task<ProductionPlotDto> CreatePlot(ProductionPlotInput input, CancellationToken ct)
    {
        await plots.ValidateAndThrowAsync(input, ct);
        var geometry = geo.ValidatePlotGeometry(input.InitialRevision.Geometry.GetRawText());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Existence and activity are decided on the locked row, so a concurrent producer deactivation (same row) is serialized behind this transaction.
        var producer = await LockProducer(input.ProducerId, ct) ?? throw new ApiException(400, ProducerNotFound);
        if (!producer.IsActive) throw ApiException.Conflict("Producer is inactive; reactivate it before registering plots.");
        var plot = new ProductionPlot { OrganizationId = tenant.OrganizationId, ProducerId = producer.Id, Name = input.Name.Trim(), ExternalReference = NullIfBlank(input.ExternalReference), Notes = input.Notes, CreatedByUserId = tenant.UserId };
        var revision = NewRevision(plot.Id, 1, input.InitialRevision, geometry);
        db.ProductionPlots.Add(plot); db.PlotRevisions.Add(revision);
        audit.Add(null, "ProductionPlotCreated", $"Production plot {plot.Id} created for producer {producer.Id} with revision {revision.Id}.");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return plot.ToDto(revision);
    }
    public async Task<ProductionPlotDto> UpdatePlot(Guid id, ProductionPlotUpdateInput input, CancellationToken ct)
    {
        var plot = await db.ProductionPlots.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound();
        await plotUpdates.ValidateAndThrowAsync(input, ct);
        plot.Name = input.Name.Trim(); plot.ExternalReference = NullIfBlank(input.ExternalReference); plot.Notes = input.Notes; plot.IsActive = input.IsActive!.Value; // ProducerId is not editable.
        audit.Add(null, "ProductionPlotUpdated", $"Production plot {plot.Id} saved.");
        await db.SaveChangesAsync(ct);
        return plot.ToDto(await db.PlotRevisions.AsNoTracking().Current().SingleAsync(x => x.ProductionPlotId == id, ct));
    }

    public async Task<PlotRevisionDto> CreateRevision(Guid plotId, PlotRevisionInput input, CancellationToken ct)
    {
        await revisions.ValidateAndThrowAsync(input, ct);
        var geometry = geo.ValidatePlotGeometry(input.Geometry.GetRawText());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var plot = await db.ProductionPlots
            .FromSqlInterpolated($"SELECT * FROM \"ProductionPlots\" WHERE \"Id\" = {plotId} AND \"OrganizationId\" = {tenant.OrganizationId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound();
        if (!plot.IsActive) throw ApiException.Conflict("Production plot is inactive; reactivate it before adding revisions."); // The producer's activity is deliberately not checked.
        var current = await db.PlotRevisions.Current().SingleAsync(x => x.ProductionPlotId == plotId, ct);
        if (!geo.IsMaterialChange(current, input.CountryCode, input.AreaHectares, geometry.Geometry)) throw ApiException.Conflict("No material change: geometry, country and declared area are unchanged.");
        // Supersede first and save before inserting the successor: EF cannot order writes around the filtered unique index.
        current.SupersededAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        var revision = NewRevision(plotId, current.Number + 1, input, geometry);
        db.PlotRevisions.Add(revision);
        audit.Add(null, "PlotRevisionCreated", $"Plot revision {revision.Id} (number {revision.Number}) created for production plot {plotId}; superseded revision {current.Id}.");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return revision.ToDto();
    }
    public async Task<List<PlotRevisionSummaryDto>> RevisionHistory(Guid plotId, CancellationToken ct)
    {
        if (!await db.ProductionPlots.AnyAsync(x => x.Id == plotId, ct)) throw ApiException.NotFound();
        return await db.PlotRevisions.AsNoTracking().Where(x => x.ProductionPlotId == plotId).OrderByDescending(x => x.Number)
            .Select(r => new PlotRevisionSummaryDto(r.Id, r.Number, r.CountryCode, r.GeometryType, r.AreaHectares, r.Note, r.CreatedByUserId, r.CreatedAtUtc, r.SupersededAtUtc)).ToListAsync(ct);
    }
    public async Task<PlotRevisionDto> Revision(Guid plotId, Guid revisionId, CancellationToken ct) =>
        (await db.PlotRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == revisionId && x.ProductionPlotId == plotId, ct) ?? throw ApiException.NotFound()).ToDto();

    private Task<Producer?> LockProducer(Guid id, CancellationToken ct) => db.Producers
        .FromSqlInterpolated($"SELECT * FROM \"Producers\" WHERE \"Id\" = {id} AND \"OrganizationId\" = {tenant.OrganizationId} FOR UPDATE")
        .AsNoTracking().SingleOrDefaultAsync(ct);
    private PlotRevision NewRevision(Guid plotId, int number, PlotRevisionInput input, PlotGeometryResult geometry) => new()
    {
        OrganizationId = tenant.OrganizationId, ProductionPlotId = plotId, Number = number, CountryCode = input.CountryCode, GeometryType = geometry.GeometryType,
        GeometryJson = geometry.NormalizedJson, AreaHectares = input.AreaHectares, Note = input.Note, CreatedByUserId = tenant.UserId
    };
    private static ValidationException RequiredIsActive() => new([new FluentValidation.Results.ValidationFailure("isActive", "isActive is required.")]);
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
