using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Domain;
using Provena.Api.Services;
using Xunit;

namespace Provena.Api.UnitTests;

public sealed class DomainTests
{
    [Fact]
    public void Tokens_are_random_url_safe_and_only_hash_is_persistable()
    {
        var service = new SupplierTokenService();
        var a = service.Generate();
        var b = service.Generate();
        Assert.NotEqual(a, b);
        Assert.Equal(43, a.Length);
        Assert.True(service.IsWellFormed(a));
        Assert.Equal(64, service.Hash(a).Length);
        Assert.NotEqual(a, service.Hash(a));
        Assert.Equal(service.Hash(a), service.Hash(a));
        Assert.False(service.IsWellFormed("../bad"));
    }

    [Theory]
    [InlineData(EudrCaseStatus.Completed)]
    [InlineData(EudrCaseStatus.Archived)]
    public void Terminal_cases_are_not_recalculated(EudrCaseStatus status) =>
        Assert.Equal(
            status,
            EudrCaseStatusService.Calculate(
                status,
                true,
                [new() { Status = RequirementStatus.Missing }]
            )
        );

    [Fact]
    public void Status_rules_follow_documented_priority()
    {
        var requirements = Enum.GetValues<RequirementType>()
            .Select(t => new EudrRequirement { Type = t, Status = RequirementStatus.Accepted })
            .ToArray();
        Assert.Equal(
            EudrCaseStatus.Draft,
            EudrCaseStatusService.Calculate(EudrCaseStatus.Draft, false, requirements)
        );
        requirements[0].Status = RequirementStatus.Missing;
        requirements[1].Status = RequirementStatus.Submitted;
        Assert.Equal(
            EudrCaseStatus.WaitingForSupplier,
            EudrCaseStatusService.Calculate(EudrCaseStatus.Draft, true, requirements)
        );
        requirements[0].Status = RequirementStatus.Rejected;
        Assert.Equal(
            EudrCaseStatus.ReviewNeeded,
            EudrCaseStatusService.Calculate(EudrCaseStatus.Draft, true, requirements)
        );
        requirements[0].Status = RequirementStatus.NotRequired;
        requirements[1].Status = RequirementStatus.Accepted;
        Assert.Equal(
            EudrCaseStatus.ReadyForExport,
            EudrCaseStatusService.Calculate(EudrCaseStatus.Draft, true, requirements)
        );
    }

    [Theory]
    [InlineData("0901", true)]
    [InlineData("0901110000", true)]
    [InlineData("123", false)]
    [InlineData("12345678901", false)]
    [InlineData("09AB", false)]
    public void Cn_code_preserves_leading_zeroes_and_validates_format(string code, bool valid)
    {
        Assert.Equal(
            valid,
            new ProductValidator()
                .Validate(new ProductInput("Coffee", null, code, CommodityType.Coffee, null))
                .IsValid
        );
    }

    public const string Polygon =
        """{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,1],[0,0]]]}""";

    [Theory]
    [InlineData(Polygon)]
    [InlineData(
        """{"type":"Feature","properties":{},"geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}"""
    )]
    [InlineData("""{"type":"MultiPolygon","coordinates":[[[[0,0],[1,0],[1,1],[0,0]]]]}""")]
    [InlineData(
        """{"type":"FeatureCollection","features":[{"type":"Feature","properties":null,"geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}]}"""
    )]
    public void Allowed_geojson_roots_are_accepted(string json) =>
        Assert.Equal(1, new GeoJsonValidationService().Validate(json).FeatureCount);

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"type":"FeatureCollection","features":[]}""")]
    [InlineData("""{"type":"Point","coordinates":[0,0]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[[181,0],[1,0],[1,1],[181,0]]]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[[0,91],[1,0],[1,1],[0,91]]]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,1]]]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[[0,0],[1,1],[0,1],[1,0],[0,0]]]}""")]
    public void Invalid_geojson_is_rejected(string json) =>
        Assert.Equal(
            400,
            Assert.Throws<ApiException>(() => new GeoJsonValidationService().Validate(json)).Status
        );

    private static readonly GeoJsonValidationService Geo = new();
    private const string Square = """{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,1],[0,0]]]}""";

    [Theory]
    [InlineData("""{"type":"Point","coordinates":[10.5,-20.25]}""", PlotGeometryType.Point)]
    [InlineData("""{"type":"Point","coordinates":[10.5,-20.25,100]}""", PlotGeometryType.Point)]
    [InlineData(Square, PlotGeometryType.Polygon)]
    [InlineData("""{"type":"MultiPolygon","coordinates":[[[[0,0],[1,0],[1,1],[0,0]]],[[[5,5],[6,5],[6,6],[5,5]]]]}""", PlotGeometryType.MultiPolygon)]
    public void Plot_geometry_accepts_point_polygon_and_multipolygon(string json, PlotGeometryType expected) =>
        Assert.Equal(expected, Geo.ValidatePlotGeometry(json).GeometryType);

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"type":"Feature","properties":{},"geometry":{"type":"Point","coordinates":[0,0]}}""")]
    [InlineData("""{"type":"FeatureCollection","features":[]}""")]
    [InlineData("""{"type":"LineString","coordinates":[[0,0],[1,1]]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[]}""")]
    [InlineData("""{"type":"MultiPolygon","coordinates":[]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,1]]]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[[0,0],[1,1],[0,1],[1,0],[0,0]]]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[[181,0],[1,0],[1,1],[181,0]]]}""")]
    [InlineData("""{"type":"Point","coordinates":[0,91]}""")]
    [InlineData("""{"type":"Point","coordinates":[1e999,0]}""")]
    [InlineData("""{"type":"Point","coordinates":[0]}""")]
    [InlineData("""{"type":"Point","coordinates":[0,0,0,0]}""")]
    [InlineData("""{"type":"Point","coordinates":[]}""")]
    [InlineData("""{"type":"Point"}""")]
    public void Plot_geometry_rejects_unsupported_or_invalid_input(string json) =>
        Assert.Equal(400, Assert.Throws<ApiException>(() => Geo.ValidatePlotGeometry(json)).Status);

    [Fact]
    public void Plot_geometry_rejects_oversize_payload() =>
        Assert.Equal(400, Assert.Throws<ApiException>(() => Geo.ValidatePlotGeometry($$"""{"type":"Point","coordinates":[0,0],"pad":"{{new string('x', 5 * 1024 * 1024)}}"}""")).Status);

    [Fact]
    public void Case_geometry_validation_still_rejects_point() =>
        Assert.Equal(400, Assert.Throws<ApiException>(() => Geo.Validate("""{"type":"Point","coordinates":[0,0]}""")).Status);

    private static bool Material(string currentJson, string country, decimal? area, string nextJson, string nextCountry, decimal? nextArea)
    {
        var current = new PlotRevision { CountryCode = country, AreaHectares = area, GeometryJson = Geo.ValidatePlotGeometry(currentJson).NormalizedJson };
        return Geo.IsMaterialChange(current, nextCountry, nextArea, Geo.ValidatePlotGeometry(nextJson).Geometry);
    }

    [Theory]
    [InlineData("""{"type":"Polygon","coordinates":[[[1,0],[1,1],[0,1],[0,0],[1,0]]]}""")] // shifted ring start
    [InlineData("""{"type":"Polygon","coordinates":[[[0,0],[0,1],[1,1],[1,0],[0,0]]]}""")] // reversed orientation
    [InlineData("""{"type":"MultiPolygon","coordinates":[[[[0,0],[1,0],[1,1],[0,1],[0,0]]]]}""")] // single-component MultiPolygon vs Polygon
    public void Equivalent_polygon_representations_are_not_a_material_change(string next) =>
        Assert.False(Material(Square, "BR", 1.5m, next, "BR", 1.5m));

    [Fact]
    public void Reordered_multipolygon_components_are_not_a_material_change()
    {
        const string ab = """{"type":"MultiPolygon","coordinates":[[[[0,0],[1,0],[1,1],[0,0]]],[[[5,5],[6,5],[6,6],[5,5]]]]}""";
        const string ba = """{"type":"MultiPolygon","coordinates":[[[[5,5],[6,5],[6,6],[5,5]]],[[[0,0],[1,0],[1,1],[0,0]]]]}""";
        Assert.False(Material(ab, "BR", null, ba, "BR", null));
    }

    [Fact]
    public void Moved_vertex_or_different_point_is_a_material_change()
    {
        Assert.True(Material(Square, "BR", null, """{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1.5],[0,1],[0,0]]]}""", "BR", null));
        Assert.True(Material(Square, "BR", null, """{"type":"Point","coordinates":[0,0]}""", "BR", null));
        const string point = """{"type":"Point","coordinates":[10,10]}""";
        Assert.False(Material(point, "BR", null, point, "BR", null));
        Assert.True(Material(point, "BR", null, """{"type":"Point","coordinates":[10,10.000001]}""", "BR", null));
    }

    [Fact]
    public void Country_and_declared_area_changes_are_material_but_scale_differences_are_not()
    {
        Assert.True(Material(Square, "BR", null, Square, "CO", null));
        Assert.False(Material(Square, "BR", 1.5000m, Square, "BR", 1.5m));
        Assert.True(Material(Square, "BR", null, Square, "BR", 1.5m));
        Assert.True(Material(Square, "BR", 1.5m, Square, "BR", null));
        Assert.True(Material(Square, "BR", 1.5m, Square, "BR", 1.6m));
    }

    [Fact]
    public void File_size_and_mime_are_checked()
    {
        var service = new FileValidationService(new ConfigurationBuilder().Build());
        var file = new FormFile(
            new MemoryStream(Encoding.UTF8.GetBytes("hello")),
            0,
            5,
            "file",
            "test.txt"
        )
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/plain",
        };
        Assert.Equal(".txt", service.Validate(file));
        file.ContentType = "application/octet-stream";
        Assert.Throws<ApiException>(() => service.Validate(file));
        var large = new FormFile(Stream.Null, 0, 21 * 1024 * 1024, "file", "test.txt")
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/plain",
        };
        Assert.Equal(413, Assert.Throws<ApiException>(() => service.Validate(large)).Status);
    }
}
