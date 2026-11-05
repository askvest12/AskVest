using System.Diagnostics;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Api;

internal sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();
            if (logger.IsEnabled(LogLevel.Information))
            {
                var endpoint = context.GetEndpoint() as RouteEndpoint;
                var route = endpoint?.RoutePattern.RawText ?? "unmatched";
                logger.LogInformation(
                    "HTTP {Method} {Route} responded {StatusCode} in {ElapsedMilliseconds}ms",
                    context.Request.Method,
                    route,
                    context.Response.StatusCode,
                    stopwatch.ElapsedMilliseconds);
            }
        }
    }
}
