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
