using AskVest.Api;

namespace AskVest.Api.Endpoints;

internal static class DiagnosticsEndpoints
{
    public static void MapDiagnosticsEndpoints(this WebApplication app, bool enabled)
    {
        if (!enabled)
        {
            return;
        }

        app.MapPost("/diagnostics/probe", async (HttpContext httpContext, ProbeRequest request, [Microsoft.AspNetCore.Mvc.FromServices] TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            if (string.Equals(request.Value, "slow", StringComparison.Ordinal))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }

            return ApiResults.Ok(
                new { received = request.Value, serverTimeUtc = timeProvider.GetUtcNow() },
                "Probe received.",
                httpContext);
        })
            .WithIdempotency()
            .WithStandardErrors()
            .AllowAnonymous();
    }

    internal sealed record ProbeRequest(string? Value);
}
