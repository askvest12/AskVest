using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Api;

internal static class AnonymousEndpointGuard
{
    public static IReadOnlyList<string> FindViolations(IEnumerable<Endpoint> endpoints)
    {
        var violations = new List<string>();
        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var route = endpoint.RoutePattern.RawText ?? string.Empty;
            var isAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null
                || endpoint.Metadata.GetMetadata<AllowAnonymousAttribute>() is not null;
            var requiresAuthorization = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any()
                || endpoint.Metadata.GetMetadata<AuthorizeAttribute>() is not null;

            if (isAnonymous)
            {
                if (!AnonymousEndpointCatalog.IsAnonymous(route))
                {
                    violations.Add($"anonymous endpoint not in allow-list: {endpoint.DisplayName} ({route})");
                }
            }
            else if (!requiresAuthorization)
            {
                violations.Add($"endpoint has no authorization policy and is not on the anonymous allow-list: {endpoint.DisplayName} ({route})");
            }
        }

        return violations;
    }
}
