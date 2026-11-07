using AskVest.Api;
using AskVest.Api.Endpoints;
using AskVest.Modules.Compliance;
using AskVest.Modules.Identity;
using AskVest.Platform;
using FluentValidation;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
});

builder.Services.AddSerilog((_, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter()));

builder.Services.AddPlatformPersistence(builder.Configuration);
builder.Services.AddIdentityModule(builder.Configuration, builder.Environment);
builder.Services.AddComplianceModule(builder.Configuration);
builder.Services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();
builder.Services.AddSingleton<IIdempotencyScopeResolver, AuthenticatedIdempotencyScopeResolver>();
builder.Services.AddHostedService<IdempotencyCleanupService>();

builder.Services.ConfigureHttpJsonOptions(JsonOptionsConfiguration.Configure);
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
ValidatorOptions.Global.PropertyNameResolver = static (_, member, _) =>
    member is null ? null : System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(member.Name);
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddAuthentication(AskVest.Modules.Identity.Services.IdentityBearerAuthenticationHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, AskVest.Modules.Identity.Services.IdentityBearerAuthenticationHandler>(
        AskVest.Modules.Identity.Services.IdentityBearerAuthenticationHandler.SchemeName,
        _ => { });
builder.Services.AddAuthorization();
builder.Services.AddRequestTimeouts();
builder.Services.AddApiRateLimiting(builder.Configuration);
builder.Services.Configure<Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutOptions>(options =>
{
    options.DefaultPolicy = new Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutPolicy
    {
        Timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue<int?>("RequestLimits:TimeoutSeconds") ?? 30),
    };
});
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddOptions<IdempotencyOptions>()
    .Bind(builder.Configuration.GetSection(IdempotencyOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddOptions<RequestLimitsOptions>()
    .Bind(builder.Configuration.GetSection(RequestLimitsOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddOptions<CorsOptions>()
    .Bind(builder.Configuration.GetSection(CorsOptions.SectionName));
builder.Services.AddOptions<DocsOptions>()
    .Bind(builder.Configuration.GetSection(DocsOptions.SectionName));
builder.Services.AddOptions<DiagnosticsOptions>()
    .Bind(builder.Configuration.GetSection(DiagnosticsOptions.SectionName));
builder.Services.AddOptions<ForwardedHeadersSettings>()
    .Bind(builder.Configuration.GetSection(ForwardedHeadersSettings.SectionName));
builder.Services.AddOptions<RateLimitingOptions>()
    .Bind(builder.Configuration.GetSection(RateLimitingOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddOpenApi(options => options.ConfigureOpenApi());

var app = builder.Build();

var diagnostics = app.Services.GetRequiredService<IOptions<DiagnosticsOptions>>().Value;

var docs = app.Services.GetRequiredService<IOptions<DocsOptions>>().Value;
var cors = app.Services.GetRequiredService<IOptions<CorsOptions>>().Value;
var forwarded = app.Services.GetRequiredService<IOptions<ForwardedHeadersSettings>>().Value;

var forwardedOptions = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
        | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
};
foreach (var proxy in forwarded.KnownProxies)
{
    forwardedOptions.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
}

foreach (var network in forwarded.KnownNetworks)
{
    forwardedOptions.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
}

app.UseForwardedHeaders(forwardedOptions);

app.UseExceptionHandler();
app.UseApiStatusCodePages();
app.UseMiddleware<SecurityHeadersMiddleware>();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseRequestTimeouts();
app.UseRateLimiter();

if (cors.AllowedOrigins.Length > 0)
{
    app.UseCors(policy => policy
        .WithOrigins(cors.AllowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod());
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseMiddleware<DuplicatePropertyCheckMiddleware>();
app.UseAuthentication();
app.UseMiddleware<AskVest.Modules.Identity.Middleware.PendingDeletionGateMiddleware>();
app.UseMiddleware<IdempotencyMiddleware>();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapDiagnosticsEndpoints(diagnostics.EnableProbes);

var api = app.MapGroup("/api/v1").RequireAuthorization().AddEndpointFilter<ValidationEndpointFilter>();
api.MapSystemEndpoints();
api.MapIdentityEndpoints(
    builder.Configuration.GetValue<bool>("Identity:PhoneVerificationEnabled"));
api.MapComplianceEndpoints();

if (docs.Enabled)
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}

app.Run();
