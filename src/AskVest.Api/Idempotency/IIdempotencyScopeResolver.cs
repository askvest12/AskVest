using Microsoft.AspNetCore.Http;

namespace AskVest.Api;

internal interface IIdempotencyScopeResolver
{
    string Resolve(HttpContext context);
}

internal sealed class AuthenticatedIdempotencyScopeResolver : IIdempotencyScopeResolver
{
    public string Resolve(HttpContext context)
        => context.User.FindFirst("sub")?.Value ?? "anonymous";
}
