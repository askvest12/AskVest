using Microsoft.AspNetCore.Http;

namespace AskVest.Api;

internal interface IIdempotencyScopeResolver
{
    string Resolve(HttpContext context);
}

internal sealed class DefaultIdempotencyScopeResolver : IIdempotencyScopeResolver
{
    public string Resolve(HttpContext context) => "api";
}
