using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Valid;
using Provena.Api.Common;
using Provena.Api.Domain;
namespace Provena.Api.Services;
public sealed record GeoValidationResult(string NormalizedJson, int FeatureCount, string GeometryTypes);
public sealed record PlotGeometryResult(string NormalizedJson, PlotGeometryType GeometryType, Geometry Geometry);
public sealed class GeoJsonValidationService
{
    private const int MaxPlotGeometryBytes = 5 * 1024 * 1024;
    private readonly GeometryFactory factory = new(new PrecisionModel(), 4326);
    public GeoValidationResult Validate(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var types = new HashSet<string>(); var count = 0;
            void Geometry(JsonElement value)
            {
                var type = value.GetProperty("type").GetString();
                var coordinates = value.GetProperty("coordinates");
                NetTopologySuite.Geometries.Geometry geometry = type switch
                {
                    "Polygon" => Polygon(coordinates),
                    "MultiPolygon" => factory.CreateMultiPolygon(coordinates.EnumerateArray().Select(Polygon).ToArray()),
                    _ => throw new ApiException(400, "Only Polygon and MultiPolygon geometries are supported.")
                };
                if (geometry.IsEmpty) throw new ApiException(400, "Empty geometry is not allowed.");
                var valid = new IsValidOp(geometry);
                if (!valid.IsValid) throw new ApiException(400, $"Invalid polygon: {valid.ValidationError?.Message}");
                types.Add(type!); count++;
            }
            void Feature(JsonElement value)
            {
                if (value.GetProperty("type").GetString() != "Feature") throw new ApiException(400, "FeatureCollection must contain Feature objects.");
                if (!value.TryGetProperty("properties", out var properties) || properties.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                    throw new ApiException(400, "Feature properties must be an object or null.");
                Geometry(value.GetProperty("geometry"));
            }
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "FeatureCollection": foreach (var f in root.GetProperty("features").EnumerateArray()) Feature(f); break;
                case "Feature": Feature(root); break;
                case "Polygon": case "MultiPolygon": Geometry(root); break;
                default: throw new ApiException(400, "Expected FeatureCollection, Feature, Polygon or MultiPolygon.");
            }
            if (count == 0) throw new ApiException(400, "GeoJSON must contain at least one geometry.");
            return new(JsonSerializer.Serialize(root), count, string.Join(",", types.Order()));
        }
        catch (ApiException) { throw; }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or OverflowException)
        { throw new ApiException(400, "Invalid GeoJSON structure or coordinates."); }
    }
    // Production plot geometry: one bare Point/Polygon/MultiPolygon geometry object (no Feature wrappers). Structural validity only; Point stays rejected by Validate (case uploads).
    public PlotGeometryResult ValidatePlotGeometry(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxPlotGeometryBytes) throw new ApiException(400, "Plot geometry is too large.");
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ApiException(400, "Expected a GeoJSON geometry object.");
            var type = root.GetProperty("type").GetString();
            var coordinates = root.GetProperty("coordinates");
            (PlotGeometryType Kind, Geometry Value) parsed = type switch
            {
                "Point" => (PlotGeometryType.Point, factory.CreatePoint(Position(coordinates))),
                "Polygon" => (PlotGeometryType.Polygon, Polygon(coordinates)),
                "MultiPolygon" => (PlotGeometryType.MultiPolygon, factory.CreateMultiPolygon(coordinates.EnumerateArray().Select(Polygon).ToArray())),
                _ => throw new ApiException(400, "Plot geometry must be a Point, Polygon or MultiPolygon geometry object.")
            };
            if (parsed.Value.IsEmpty) throw new ApiException(400, "Empty geometry is not allowed.");
            var valid = new IsValidOp(parsed.Value);
            if (!valid.IsValid) throw new ApiException(400, $"Invalid geometry: {valid.ValidationError?.Message}");
            return new(JsonSerializer.Serialize(root), parsed.Kind, parsed.Value);
        }
        catch (ApiException) { throw; }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or OverflowException)
        { throw new ApiException(400, "Invalid GeoJSON structure or coordinates."); }
    }
    // A revision is only created for a material change: topological geometry equality (ignores ring start, orientation, component order and
    // single-component MultiPolygon vs Polygon), exact country and exact declared area. The revision Note is never material.
    public bool IsMaterialChange(PlotRevision current, string countryCode, decimal? areaHectares, Geometry geometry) =>
        current.CountryCode != countryCode || current.AreaHectares != areaHectares ||
        !ValidatePlotGeometry(current.GeometryJson).Geometry.EqualsTopologically(geometry);
    private Coordinate Position(JsonElement value) => Coordinate(value.EnumerateArray().Select(x => x.GetDouble()).ToArray());
    private static Coordinate Coordinate(double[] c)
    {
        if (c.Length is < 2 or > 3 || c.Any(x => !double.IsFinite(x)) || c[0] is < -180 or > 180 || c[1] is < -90 or > 90)
            throw new ApiException(400, "Coordinates require longitude [-180,180] and latitude [-90,90].");
        return new Coordinate(c[0], c[1]);
    }
    private Polygon Polygon(JsonElement value)
    {
        var rings = value.EnumerateArray().Select(Ring).ToArray();
        if (rings.Length == 0) throw new ApiException(400, "Polygon cannot be empty.");
        return factory.CreatePolygon(rings[0], rings.Skip(1).ToArray());
    }
    private LinearRing Ring(JsonElement value)
    {
        var points = value.EnumerateArray().Select(Position).ToArray();
        if (points.Length < 4 || !points[0].Equals2D(points[^1])) throw new ApiException(400, "Polygon rings require at least four coordinates and must be closed.");
        return factory.CreateLinearRing(points);
    }
}
