namespace AskVest.Api;

internal sealed record AnonymousEndpoint(string Route, string Reason);

internal static class AnonymousEndpointCatalog
{
    public static readonly IReadOnlyList<AnonymousEndpoint> Entries =
    [
        new("/api/v1/auth/sign-up", "Account creation must work before authentication exists."),
        new("/api/v1/auth/verify-email", "Email verification precedes first login."),
        new("/api/v1/auth/resend-verification", "Resend must work for unauthenticated pending accounts."),
        new("/api/v1/auth/login", "Credential exchange is the entry point."),
        new("/api/v1/auth/verify-device", "Device challenge is exchanged before a session exists."),
        new("/api/v1/auth/refresh", "The refresh token itself is the credential."),
        new("/api/v1/auth/forgot-password", "Password recovery precedes authentication."),
        new("/api/v1/auth/reset-password", "Reset consumes a code, not a session."),
        new("/api/v1/system/info", "Service metadata for load balancers and clients."),
        new("/health/live", "Liveness probe must not require auth."),
        new("/health/ready", "Readiness probe must not require auth."),
        new("/diagnostics/probe", "Diagnostics probe, only mapped when Diagnostics:EnableProbes is set."),
        new("/openapi/{documentName}.json", "API documentation is public when enabled."),
        new("/scalar/{documentName}", "API documentation UI is public when enabled."),
    ];

    public static bool IsAnonymous(string route)
        => Entries.Any(entry => RouteMatches(entry.Route, route));

    private static bool RouteMatches(string pattern, string route)
    {
        var patternParts = pattern.Trim('/').Split('/');
        var routeParts = route.Trim('/').Split('/');
        if (patternParts.Length != routeParts.Length)
        {
            return false;
        }

        return patternParts.Zip(routeParts).All(pair =>
            pair.First.StartsWith('{') || pair.First == pair.Second);
    }
}
