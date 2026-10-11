using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Valid;
using Provena.Api.Common;
namespace Provena.Api.Services;
public sealed record GeoValidationResult(string NormalizedJson, int FeatureCount, string GeometryTypes);
public sealed class GeoJsonValidationService
{
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
    private Polygon Polygon(JsonElement value)
    {
        var rings = value.EnumerateArray().Select(Ring).ToArray();
        if (rings.Length == 0) throw new ApiException(400, "Polygon cannot be empty.");
        return factory.CreatePolygon(rings[0], rings.Skip(1).ToArray());
    }
    private LinearRing Ring(JsonElement value)
    {
        var points = value.EnumerateArray().Select(p =>
        {
            var coordinates = p.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            if (coordinates.Length is < 2 or > 3 || coordinates.Any(x => !double.IsFinite(x)) || coordinates[0] is < -180 or > 180 || coordinates[1] is < -90 or > 90)
                throw new ApiException(400, "Coordinates require longitude [-180,180] and latitude [-90,90].");
            return new Coordinate(coordinates[0], coordinates[1]);
        }).ToArray();
        if (points.Length < 4 || !points[0].Equals2D(points[^1])) throw new ApiException(400, "Polygon rings require at least four coordinates and must be closed.");
        return factory.CreateLinearRing(points);
    }
}
