namespace AskVest.Api;

internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private static readonly string[] DocsPrefixes = ["/scalar", "/openapi"];

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            var isDocs = DocsPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (!isDocs)
            {
                var headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Cache-Control"] = "no-store";
                headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            }

            return Task.CompletedTask;
        });

        await next(context);
    }
}
