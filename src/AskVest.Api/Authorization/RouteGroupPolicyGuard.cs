using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Api;

/// <summary>
/// Enforces that every endpoint under a protected route group carries the
/// named authorization policy. Route groups apply the policy once; this guard
/// proves no endpoint can be added to the group without it.
/// </summary>
internal static class RouteGroupPolicyGuard
{
    public static IReadOnlyList<string> FindViolations(
        IEnumerable<Endpoint> endpoints,
        string routePrefix,
        string requiredPolicy)
    {
        var violations = new List<string>();
        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var route = endpoint.RoutePattern.RawText ?? string.Empty;
            if (!IsUnderPrefix(route, routePrefix))
            {
                continue;
            }

            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Select(data => data.Policy)
                .Where(policy => policy is not null)
                .ToHashSet(StringComparer.Ordinal);
            if (!policies.Contains(requiredPolicy))
            {
                violations.Add($"endpoint under {routePrefix} lacks policy {requiredPolicy}: {endpoint.DisplayName} ({route})");
            }
        }

        return violations;
    }

    private static bool IsUnderPrefix(string route, string prefix)
        => route.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || route.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
}
