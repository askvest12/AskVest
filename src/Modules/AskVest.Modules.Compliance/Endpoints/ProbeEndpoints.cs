using AskVest.Modules.Compliance.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Compliance.Endpoints;

/// <summary>
/// Diagnostic pings inside the gated route groups. Mapped only when the Phase 0
/// probe flag is on so gating can be exercised over real HTTP.
/// </summary>
public static class ProbeEndpoints
{
    public static void MapProbeEndpoints(this RouteGroupBuilder group, bool enabled, string mode)
    {
        if (!enabled)
        {
            return;
        }

        group.MapGet("/diagnostics/ping", (HttpContext httpContext) =>
                ComplianceResults.Ok(new { mode }, "Probe received.", httpContext))
            .WithName($"{char.ToUpperInvariant(mode[0])}{mode[1..]}EligibilityProbe")
            .WithSummary("Diagnostic ping behind the eligibility gate.")
            .WithTags("Diagnostics")
            .ExcludeFromDescription();
    }
}
