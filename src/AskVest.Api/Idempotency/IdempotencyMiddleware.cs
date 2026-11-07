using System.Text;
using AskVest.SharedKernel;
using Microsoft.Extensions.Options;

namespace AskVest.Api;

internal sealed class IdempotencyMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> _coveredMethods = new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null
            || !_coveredMethods.Contains(context.Request.Method)
            || endpoint.Metadata.GetMetadata<IdempotencyExemptAttribute>() is not null)
        {
            await next(context);
            return;
        }

        if (endpoint.Metadata.GetMetadata<RequiresIdempotencyAttribute>() is null)
        {
            await next(context);
            return;
        }

        var store = context.RequestServices.GetRequiredService<IIdempotencyStore>();
        var options = context.RequestServices.GetRequiredService<IOptions<IdempotencyOptions>>().Value;
        var scopeResolver = context.RequestServices.GetRequiredService<IIdempotencyScopeResolver>();

        var key = context.Request.Headers[IdempotencyKey.HeaderName].FirstOrDefault();
        if (key is null)
        {
            await ApiResults.WriteErrorAsync(context, new Error(ErrorCodes.IdempotencyKeyMissing, "The Idempotency-Key header is required.", ErrorType.Validation));
            return;
        }

        if (!IdempotencyKey.IsValid(key))
        {
            await ApiResults.WriteErrorAsync(context, new Error(ErrorCodes.IdempotencyKeyInvalid, "The Idempotency-Key header is invalid.", ErrorType.Validation));
            return;
        }

        context.Request.EnableBuffering();
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        var body = buffer.ToArray();
        context.Request.Body.Position = 0;

        var scope = scopeResolver.Resolve(context);
        var path = context.Request.Path.Value ?? string.Empty;
        var fingerprint = RequestFingerprint.Compute(context.Request.Method, path, body);
        var now = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var expiresAt = now.AddHours(options.RetentionHours);

        var begin = await store.BeginAsync(
            scope,
            key,
            context.Request.Method,
            path,
            fingerprint,
            TimeSpan.FromSeconds(options.LockSeconds),
            expiresAt,
            context.RequestAborted);

        switch (begin)
        {
            case IdempotencyBeginResult.KeyReused:
                await ApiResults.WriteErrorAsync(context, new Error(ErrorCodes.IdempotencyKeyReused, "The Idempotency-Key was already used with a different request.", ErrorType.Conflict), StatusCodes.Status422UnprocessableEntity);
                return;
            case IdempotencyBeginResult.InProgress:
                await ApiResults.WriteErrorAsync(context, new Error(ErrorCodes.IdempotencyRequestInProgress, "A request with this Idempotency-Key is still in progress.", ErrorType.Conflict));
                return;
            case IdempotencyBeginResult.Replay:
                var stored = await store.GetCompletedAsync(scope, key, context.RequestAborted);
                if (stored is null)
                {
                    await ApiResults.WriteErrorAsync(context, new Error(ErrorCodes.IdempotencyReplayUnavailable, "The stored response for this Idempotency-Key is unavailable.", ErrorType.Conflict));
                    return;
                }

                await WriteReplayAsync(context, stored);
                return;
        }

        var originalBody = context.Response.Body;
        await using var captured = new MemoryStream();
        context.Response.Body = captured;
        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        captured.Position = 0;
        string responseBody;
        using (var reader = new StreamReader(captured, Encoding.UTF8, leaveOpen: true))
        {
            responseBody = await reader.ReadToEndAsync(context.RequestAborted);
        }

        if (context.Response.StatusCode >= 500)
        {
            await store.ReleaseAsync(scope, key, context.RequestAborted);
        }
        else
        {
            var tooLarge = Encoding.UTF8.GetByteCount(responseBody) > options.MaxStoredResponseBytes;
            await store.CompleteAsync(
                scope,
                key,
                new IdempotencyStoredResponse(
                    context.Response.StatusCode,
                    context.Response.ContentType,
                    tooLarge ? null : responseBody,
                    tooLarge),
                context.RequestAborted);
        }

        captured.Position = 0;
        await captured.CopyToAsync(originalBody, context.RequestAborted);
        await originalBody.FlushAsync(context.RequestAborted);
    }

    private static async Task WriteReplayAsync(HttpContext context, IdempotencyStoredResponse stored)
    {
        context.Response.StatusCode = stored.StatusCode;
        context.Response.ContentType = stored.ContentType ?? "application/json";
        context.Response.Headers["Idempotent-Replay"] = "true";
        if (stored.BodyOmitted)
        {
            context.Response.Headers["Idempotent-Replay-Omitted"] = "true";
            return;
        }

        await context.Response.WriteAsync(stored.Body ?? string.Empty, context.RequestAborted);
    }
}
