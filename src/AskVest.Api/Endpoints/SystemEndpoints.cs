using System.Reflection;
using AskVest.Api;

namespace AskVest.Api.Endpoints;

internal static class SystemEndpoints
{
    public static RouteGroupBuilder MapSystemEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/system/info", (HttpContext httpContext, [Microsoft.AspNetCore.Mvc.FromServices] TimeProvider timeProvider) =>
        {
            var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "unknown";
            return ApiResults.Ok(
                new
                {
                    service = "askvest-api",
                    version,
                    serverTimeUtc = timeProvider.GetUtcNow(),
                },
                "Service information retrieved.",
                httpContext);
        }).WithStandardErrors();
        return group;
    }
}
