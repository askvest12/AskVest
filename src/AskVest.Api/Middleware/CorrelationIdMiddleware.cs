using System.Text.RegularExpressions;
using Serilog.Context;

namespace AskVest.Api;

internal sealed partial class CorrelationIdMiddleware(RequestDelegate next)
{
    internal const string ItemKey = "AskVest.CorrelationId";
    private const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var inbound = context.Request.Headers[HeaderName].FirstOrDefault();
        var correlationId = IsValid(inbound) ? inbound! : Guid.CreateVersion7().ToString("N");

        context.Items[ItemKey] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }

    public static string Get(HttpContext context)
        => (string)context.Items[ItemKey]!;

    private static bool IsValid(string? value)
        => value is not null && CorrelationIdPattern().IsMatch(value);

    [GeneratedRegex("^[A-Za-z0-9._-]{8,64}$")]
    private static partial Regex CorrelationIdPattern();
}
