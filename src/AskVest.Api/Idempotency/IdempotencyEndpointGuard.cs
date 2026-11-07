using AskVest.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Api;

internal static class IdempotencyEndpointGuard
{
    private static readonly HashSet<string> _safeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Get, HttpMethods.Head, HttpMethods.Options,
    };

    public static IReadOnlyList<string> FindViolations(IEnumerable<Endpoint> endpoints)
    {
        var violations = new List<string>();
        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods;
            if (methods is null || methods.All(_safeMethods.Contains))
            {
                continue;
            }

            var route = $"{string.Join(',', methods)} {endpoint.RoutePattern.RawText}";
            var requires = endpoint.Metadata.GetMetadata<RequiresIdempotencyAttribute>() is not null;
            var exempt = endpoint.Metadata.GetMetadata<IdempotencyExemptAttribute>() is not null;
            var secrets = endpoint.Metadata.GetMetadata<ReturnsSecretsAttribute>() is not null;

            if (!requires && !exempt)
            {
                violations.Add($"mutating endpoint declares no idempotency policy: {route}");
            }

            if (requires && exempt)
            {
                violations.Add($"endpoint declares contradictory idempotency markers: {route}");
            }

            if (secrets && requires)
            {
                violations.Add($"endpoint returns secrets but still requires idempotent replay: {route}");
            }

            if (secrets && !exempt)
            {
                violations.Add($"endpoint returns secrets without an idempotency exemption: {route}");
            }
        }

        return violations;
    }
}
