using AskVest.Api;
using AskVest.Api.Endpoints;
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
builder.Services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();
builder.Services.AddSingleton<IIdempotencyScopeResolver, DefaultIdempotencyScopeResolver>();
builder.Services.AddHostedService<IdempotencyCleanupService>();

builder.Services.ConfigureHttpJsonOptions(JsonOptionsConfiguration.Configure);
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();
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
if (diagnostics.EnableProbes && app.Environment.IsProduction())
{
    throw new InvalidOperationException("Diagnostics:EnableProbes cannot be enabled in Production.");
}

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
app.UseMiddleware<IdempotencyMiddleware>();

app.MapHealthEndpoints();
app.MapDiagnosticsEndpoints(diagnostics.EnableProbes);

var api = app.MapGroup("/api/v1");
api.MapSystemEndpoints();

if (docs.Enabled)
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference();
}

app.Run();
