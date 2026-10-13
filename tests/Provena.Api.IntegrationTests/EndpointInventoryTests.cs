using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace Provena.Api.IntegrationTests;
// Implementation-agnostic HTTP surface inventory: route/method set, authorization, rate limiting and upload limits.
// Adding, removing or re-protecting an endpoint must update this list deliberately.
[Collection("Api")]
public sealed class EndpointInventoryTests(ApiFactory factory)
{
    private const long UploadLimit = 21 * 1024 * 1024;
    private enum Access { Authenticated, Anonymous }
    private sealed record Expected(string Method, string Route, Access Access, string? RateLimit = null, bool UploadLimits = false);
    private static readonly Expected[] Surface =
    [
        new("POST", "api/auth/register", Access.Anonymous, "auth"), new("POST", "api/auth/login", Access.Anonymous, "auth"), new("GET", "api/auth/me", Access.Authenticated),
        new("GET", "api/suppliers", Access.Authenticated), new("GET", "api/suppliers/{id:guid}", Access.Authenticated), new("POST", "api/suppliers", Access.Authenticated),
        new("PUT", "api/suppliers/{id:guid}", Access.Authenticated), new("DELETE", "api/suppliers/{id:guid}", Access.Authenticated),
        new("GET", "api/products", Access.Authenticated), new("GET", "api/products/{id:guid}", Access.Authenticated), new("POST", "api/products", Access.Authenticated),
        new("PUT", "api/products/{id:guid}", Access.Authenticated), new("DELETE", "api/products/{id:guid}", Access.Authenticated),
        new("GET", "api/shipments", Access.Authenticated), new("GET", "api/shipments/{id:guid}", Access.Authenticated), new("POST", "api/shipments", Access.Authenticated),
        new("PUT", "api/shipments/{id:guid}", Access.Authenticated), new("DELETE", "api/shipments/{id:guid}", Access.Authenticated),
        new("GET", "api/eudr-cases", Access.Authenticated), new("GET", "api/eudr-cases/{id:guid}", Access.Authenticated),
        new("PATCH", "api/eudr-cases/{caseId:guid}/requirements/{requirementId:guid}", Access.Authenticated),
        new("POST", "api/eudr-cases/{caseId:guid}/supplier-request", Access.Authenticated), new("POST", "api/eudr-cases/{caseId:guid}/complete", Access.Authenticated),
        new("GET", "api/eudr-cases/{caseId:guid}/export", Access.Authenticated),
        new("GET", "api/dashboard", Access.Authenticated),
        new("POST", "api/supplier-requests/{id:guid}/revoke", Access.Authenticated), new("POST", "api/supplier-requests/{id:guid}/regenerate", Access.Authenticated),
        new("POST", "api/eudr-cases/{caseId:guid}/documents", Access.Authenticated, UploadLimits: true), new("GET", "api/eudr-cases/{caseId:guid}/documents", Access.Authenticated, UploadLimits: true),
        new("GET", "api/eudr-cases/{caseId:guid}/documents/{documentId:guid}", Access.Authenticated, UploadLimits: true),
        new("DELETE", "api/eudr-cases/{caseId:guid}/documents/{documentId:guid}", Access.Authenticated, UploadLimits: true),
        new("POST", "api/eudr-cases/{caseId:guid}/geolocation", Access.Authenticated, UploadLimits: true), new("GET", "api/eudr-cases/{caseId:guid}/geolocation", Access.Authenticated, UploadLimits: true),
        new("GET", "api/eudr-cases/{caseId:guid}/geolocation/{geolocationId:guid}/file", Access.Authenticated, UploadLimits: true), new("GET", "api/eudr-cases/{caseId:guid}/evidence-history", Access.Authenticated, UploadLimits: true),
        new("POST", "api/import/suppliers", Access.Authenticated, UploadLimits: true), new("POST", "api/import/products", Access.Authenticated, UploadLimits: true),
        new("POST", "api/import/shipments", Access.Authenticated, UploadLimits: true),
        new("GET", "api/producers", Access.Authenticated), new("GET", "api/producers/{id:guid}", Access.Authenticated), new("POST", "api/producers", Access.Authenticated),
        new("PUT", "api/producers/{id:guid}", Access.Authenticated),
        new("GET", "api/production-plots", Access.Authenticated), new("GET", "api/production-plots/{id:guid}", Access.Authenticated), new("POST", "api/production-plots", Access.Authenticated),
        new("PUT", "api/production-plots/{id:guid}", Access.Authenticated), new("POST", "api/production-plots/{plotId:guid}/revisions", Access.Authenticated),
        new("GET", "api/production-plots/{plotId:guid}/revisions", Access.Authenticated), new("GET", "api/production-plots/{plotId:guid}/revisions/{revisionId:guid}", Access.Authenticated),
        new("GET", "api/eudr-cases/{caseId:guid}/dossier", Access.Authenticated), new("PUT", "api/eudr-cases/{caseId:guid}/dossier/lot", Access.Authenticated),
        new("GET", "api/eudr-cases/{caseId:guid}/dossier/lot-history", Access.Authenticated),
        new("GET", "public/supplier-requests/{token}", Access.Anonymous, "supplier", true), new("PUT", "public/supplier-requests/{token}/details", Access.Anonymous, "supplier", true),
        new("POST", "public/supplier-requests/{token}/documents", Access.Anonymous, "supplier", true), new("POST", "public/supplier-requests/{token}/geolocation", Access.Anonymous, "supplier", true),
        new("POST", "public/supplier-requests/{token}/complete", Access.Anonymous, "supplier", true),
    ];
    private static string Normalize(string raw) => raw.Trim('/');
    private List<(string Method, string Route, RouteEndpoint Endpoint)> Actual() =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => (Route: Normalize(e.RoutePattern.RawText ?? ""), Endpoint: e))
            .Where(x => x.Route.StartsWith("api/", StringComparison.Ordinal) || x.Route.StartsWith("public/", StringComparison.Ordinal))
            .SelectMany(x => (x.Endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Select(m => (m, x.Route, x.Endpoint)))
            .ToList();
    [Fact]
    public void Route_and_method_set_is_unchanged()
    {
        var actual = Actual().Select(x => $"{x.Method} {x.Route}").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(Surface.Select(x => $"{x.Method} {x.Route}").Order(StringComparer.Ordinal), actual);
    }
    [Fact]
    public void Authorization_rate_limit_and_upload_metadata_match_the_security_boundary()
    {
        var actual = Actual().ToDictionary(x => $"{x.Method} {x.Route}", x => x.Endpoint);
        foreach (var e in Surface)
        {
            var endpoint = actual[$"{e.Method} {e.Route}"]; var metadata = endpoint.Metadata; var name = $"{e.Method} {e.Route}";
            var anonymous = metadata.GetMetadata<IAllowAnonymous>() != null;
            if (e.Access == Access.Anonymous) Assert.True(anonymous, $"{name} must allow anonymous access.");
            else { Assert.False(anonymous, $"{name} must not allow anonymous access."); Assert.NotEmpty(metadata.GetOrderedMetadata<IAuthorizeData>()); }
            Assert.Equal(e.RateLimit, metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);
            Assert.True(metadata.GetMetadata<DisableRateLimitingAttribute>() == null, name);
            var size = metadata.GetMetadata<IRequestSizeLimitMetadata>(); var form = metadata.GetMetadata<IFormOptionsMetadata>();
            if (e.UploadLimits) { Assert.Equal(UploadLimit, size?.MaxRequestBodySize); Assert.Equal(UploadLimit, form?.MultipartBodyLengthLimit); }
            else { Assert.Null(size); Assert.Null(form); }
            // Bearer/capability-only API: no cookie authentication, so no endpoint requires antiforgery tokens.
            Assert.NotEqual(true, metadata.GetMetadata<IAntiforgeryMetadata>()?.RequiresValidation);
        }
    }
    [Fact]
    public void Http_layer_uses_minimal_api_endpoints_only()
    {
        Assert.DoesNotContain(typeof(Program).Assembly.GetTypes(), t => t.IsAssignableTo(typeof(Microsoft.AspNetCore.Mvc.ControllerBase)));
        Assert.All(Actual(), x => Assert.Null(x.Endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>()));
    }
}
