using System.Threading.RateLimiting;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.RateLimiting;

namespace AskVest.Api;

internal static class RateLimitingExtensions
{
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(RateLimitingOptions.SectionName).Get<RateLimitingOptions>() ?? new RateLimitingOptions();
        services.Configure<RateLimitingOptions>(configuration.GetSection(RateLimitingOptions.SectionName));
        services.AddRateLimiter(limiterOptions =>
        {
            limiterOptions.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var path = context.Request.Path.Value ?? string.Empty;
                if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/scalar", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase))
                {
                    return RateLimitPartition.GetNoLimiter("exempt");
                }

                var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(
                    key,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.Global.PermitLimit,
                        Window = TimeSpan.FromSeconds(options.Global.WindowSeconds),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    });
            });
            limiterOptions.AddPolicy("strict", context =>
            {
                var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(
                    $"strict:{key}",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.Strict.PermitLimit,
                        Window = TimeSpan.FromSeconds(options.Strict.WindowSeconds),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    });
            });
            limiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiterOptions.OnRejected = async (context, cancellationToken) =>
            {
                var httpContext = context.HttpContext;
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry)
                    ? retry.TotalSeconds
                    : options.Global.WindowSeconds;
                httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter)).ToString();
                await ApiResults.WriteAsync(
                    httpContext,
                    StatusCodes.Status429TooManyRequests,
                    "Too many requests.",
                    null,
                    new ApiError(ErrorCodes.RateLimitExceeded));
            };
        });
        return services;
    }
}
