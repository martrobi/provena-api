using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Provena.Api.Auth;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
using Provena.Api.Endpoints;
using Provena.Api.Infrastructure;
using Provena.Api.Services;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
// Framework request-start logs include token-bearing URL paths. Application logs use IDs only.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Http.RequestDelegateFactory", LogLevel.Warning);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<TenantContext>();
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddIdentityCore<ApplicationUser>(options =>
{
    options.User.RequireUniqueEmail = true;
    options.Password.RequiredLength = 12; options.Password.RequireDigit = true; options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true; options.Password.RequireNonAlphanumeric = true;
    options.Lockout.MaxFailedAccessAttempts = 5; options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
}).AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AppDbContext>().AddSignInManager();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"], ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Secret"] ?? "")), ClockSkew = TimeSpan.FromSeconds(30)
    };
});
builder.Services.AddAuthorization();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = c =>
{
    c.ProblemDetails.Instance = null; // Never echo token-bearing URLs in diagnostics.
    c.ProblemDetails.Extensions["traceId"] = c.HttpContext.TraceIdentifier;
});
builder.Services.AddExceptionHandler<ExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
    options.SerializerOptions.MaxDepth = 32; // Input depth bound carried over from the former MVC JSON defaults.
});
// Parameter-binding failures (malformed JSON, unparsable values, wrong content type) flow through ExceptionHandler.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddValidatorsFromAssemblyContaining<RegisterValidator>();
builder.Services.AddScoped<AuthService>(); builder.Services.AddScoped<CatalogService>(); builder.Services.AddScoped<ShipmentService>();
builder.Services.AddScoped<CaseService>(); builder.Services.AddScoped<EudrCaseStatusService>(); builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<SupplierRequestService>(); builder.Services.AddSingleton<SupplierTokenService>();
builder.Services.AddScoped<CaseFileService>(); builder.Services.AddSingleton<FileValidationService>(); builder.Services.AddSingleton<GeoJsonValidationService>();
builder.Services.AddScoped<ProvenanceService>(); builder.Services.AddScoped<CaseDossierService>();
builder.Services.AddScoped<DossierFactsBuilder>(); builder.Services.AddScoped<CompletenessService>(); builder.Services.AddScoped<OperatorProfileService>(); builder.Services.AddScoped<CaseMasterDataService>(); builder.Services.AddScoped<CaseApplicabilityService>(); builder.Services.AddScoped<CaseRecipientService>(); builder.Services.AddScoped<CaseEvidenceLinkService>(); builder.Services.AddScoped<ExportService>(); builder.Services.AddScoped<ImportService>(); builder.Services.AddScoped<DevelopmentSeeder>();
builder.Services.AddSingleton<IFileStorage, LocalFileStorage>(); builder.Services.AddScoped<IEmailSender, DevelopmentEmailSender>();
var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (origins.Any(x => x == "*")) throw new InvalidOperationException("CORS requires explicit origins.");
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Content-Disposition")));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.StatusCode = 429;
        await Results.Problem(statusCode: 429, title: "Too many requests").ExecuteAsync(context.HttpContext);
    };
    options.AddPolicy("supplier", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5), QueueLimit = 0, AutoReplenishment = true }));
});
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "Provena MVP API"; document.Info.Version = "v1";
    document.Components ??= new();
    document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme> { ["Bearer"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" } };
    foreach (var path in document.Paths.Where(x => x.Key.StartsWith("/api/", StringComparison.Ordinal) && x.Key is not ("/api/auth/login" or "/api/auth/register")))
        foreach (var operation in path.Value.Operations?.Values.AsEnumerable()
            ?? Enumerable.Empty<OpenApiOperation>())
        { operation.Security ??= []; operation.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] }); }
    return Task.CompletedTask;
}));
var app = builder.Build();
var secret = app.Configuration["Jwt:Secret"];
if (string.IsNullOrWhiteSpace(secret) || Encoding.UTF8.GetByteCount(secret) < 32 || secret.Contains("SET-USING", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Set Jwt:Secret to a unique secret of at least 32 bytes through user-secrets or environment configuration.");
if (string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString("Default")))
    throw new InvalidOperationException("Configure ConnectionStrings:Default.");
if (!Uri.TryCreate(app.Configuration["Frontend:BaseUrl"], UriKind.Absolute, out var frontendUri) || frontendUri.Scheme is not ("http" or "https"))
    throw new InvalidOperationException("Configure a valid Frontend:BaseUrl.");

if (app.Configuration.GetValue("Jwt:AccessTokenMinutes", 60) is < 1 or > 1440)
    throw new InvalidOperationException("Jwt:AccessTokenMinutes must be 1-1440.");
if (app.Configuration.GetValue("SupplierRequests:ExpiryDays", 30) is < 1 or > 90)
    throw new InvalidOperationException("SupplierRequests:ExpiryDays must be 1-90.");
if (app.Configuration.GetValue("FileStorage:MaxFileSizeMb", 20) is < 1 or > 20)
    throw new InvalidOperationException("FileStorage:MaxFileSizeMb must be 1-20.");
app.UseExceptionHandler(); app.UseStatusCodePages();
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing")) { app.UseHsts(); app.UseHttpsRedirection(); }
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Cache-Control"] = "no-store";
    await next();
});
app.UseRouting(); app.UseCors(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
app.MapProvenaEndpoints();
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{ app.MapOpenApi(); app.MapScalarApiReference("/scalar", options => options.WithTitle("Provena API").AddPreferredSecuritySchemes("Bearer")); }
if (app.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("SeedDemoData"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<DevelopmentSeeder>().Seed(CancellationToken.None);
}
app.Run();
public partial class Program { }
