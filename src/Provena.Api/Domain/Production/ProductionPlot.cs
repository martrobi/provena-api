namespace Provena.Api.Domain;

public enum PlotGeometryType { Point, Polygon, MultiPolygon }

// Stable producer-scoped plot registration; not a canonical identity of the physical parcel. Holds no location data (see PlotRevision).
// ProducerId is immutable after insert; a producer or tenure change creates a new ProductionPlot. Never deleted; deactivate instead.
public sealed class ProductionPlot : TenantEntity
{
    public Guid ProducerId { get; set; }
    public string Name { get; set; } = "";
    public string? ExternalReference { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? CreatedByUserId { get; set; }
}

// Immutable material location data of a plot. The only allowed update is the one-way SupersededAtUtc retirement; the current revision has it null.
public sealed class PlotRevision : TenantEntity
{
    public Guid ProductionPlotId { get; set; }
    public int Number { get; set; }
    public string CountryCode { get; set; } = "";
    public PlotGeometryType GeometryType { get; set; }
    public string GeometryJson { get; set; } = "{}";
    public decimal? AreaHectares { get; set; }
    public string? Note { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? SupersededAtUtc { get; set; }
}
