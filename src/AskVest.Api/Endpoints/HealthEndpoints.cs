using AskVest.Api;
using AskVest.SharedKernel;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AskVest.Api.Endpoints;

internal static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health/live", (HttpContext httpContext) =>
            ApiResults.Ok(new { status = "Healthy" }, "Service is live.", httpContext));

        app.MapGet("/health/ready", async (HealthCheckService healthCheckService, HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            var report = await healthCheckService.CheckHealthAsync(cancellationToken);
            var checks = report.Entries
                .Select(entry => new { name = entry.Key, status = entry.Value.Status.ToString() })
                .ToList();
            var healthy = report.Status == HealthStatus.Healthy;
            var data = new { status = report.Status.ToString(), checks };
            if (healthy)
            {
                return ApiResults.Ok(data, "Service is ready.", httpContext);
            }

            return Results.Json(
                new ApiResponse<object?>(false, "The service is not ready.", data, new ApiError(ErrorCodes.ServiceUnavailable), CorrelationIdMiddleware.Get(httpContext)),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        });
    }
}
