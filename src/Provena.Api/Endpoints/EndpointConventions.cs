using Microsoft.AspNetCore.Http.Metadata;

namespace Provena.Api.Endpoints;

internal sealed record ManuallyReadJsonBody(Type BodyType, string[] OptionalProperties);

internal static class EndpointConventions
{
    // Multipart envelope limit; FileValidationService enforces the configured 1-20 MiB per-file limit.
    public const long UploadRequestLimitBytes = 21 * 1024 * 1024;

    public static RouteGroupBuilder WithUploadLimits(this RouteGroupBuilder group) =>
        group
            .WithMetadata(new RequestSizeLimit(UploadRequestLimitBytes))
            .WithFormOptions(multipartBodyLengthLimit: UploadRequestLimitBytes);

    // MVC answered 415 to a request with neither body nor Content-Type when a JSON body is required; Minimal APIs answer 400.
    // Wraps the final delegate (before parameter binding) so the former status is kept; optional bodies are unaffected.
    public static TBuilder WithRequiredJsonBodyContentTypeCheck<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Finally(endpoint =>
        {
            var accepts = endpoint.Metadata.OfType<IAcceptsMetadata>().FirstOrDefault();
            if (
                endpoint is not RouteEndpointBuilder { RequestDelegate: { } inner } route
                || accepts is not { IsOptional: false }
                || !accepts.ContentTypes.Contains("application/json")
            )
                return;
            route.RequestDelegate = context =>
                context.Request.ContentType is null && !(context.Request.ContentLength > 0)
                    ? throw new BadHttpRequestException(
                        "Expected a JSON request body with Content-Type application/json.",
                        StatusCodes.Status415UnsupportedMediaType
                    )
                    : inner(context);
        });
        return builder;
    }

    // Query/form enums keep the former MVC binding: names are case-insensitive and numeric strings are accepted;
    // services still reject undefined values. Minimal API enum binding alone is case-sensitive.
    public static TEnum? ParseEnum<TEnum>(string? value, string parameter)
        where TEnum : struct, Enum =>
        string.IsNullOrEmpty(value) ? null
        : Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed
        : throw new BadHttpRequestException($"Invalid value for parameter \"{parameter}\".");

    // Marks a route whose handler reads its JSON body itself (after authorization) instead of using model binding, so the framework cannot reject the body first.
    // Unlike Accepts(), it adds no content-type routing constraint (which would answer 415 before the handler runs); OpenAPI advertises the body through an operation transformer.
    public static TBuilder WithManuallyReadJsonBody<TBody, TBuilder>(this TBuilder builder, params string[] optionalProperties) where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(new ManuallyReadJsonBody(typeof(TBody), optionalProperties));

    private sealed record RequestSizeLimit(long? MaxRequestBodySize) : IRequestSizeLimitMetadata;
}
