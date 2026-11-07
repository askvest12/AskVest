using System.Text;
using AskVest.Modules.Compliance.Services;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Compliance.Endpoints;

/// <summary>
/// Provider decision webhook. Anonymous because the HMAC signature is the
/// credential; deduplication uses the provider event id instead of the
/// generic idempotency store.
/// </summary>
public static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/webhooks/kyc/{provider}", ReceiveWebhookAsync)
            .WithName("ReceiveKycWebhook")
            .WithSummary("Receive a signed KYC provider decision.")
            .WithTags("Webhooks")
            .WithMetadata(new IdempotencyExemptAttribute())
            .AllowAnonymous()
            .RequireRateLimiting("strict");
    }

    private static async Task<IResult> ReceiveWebhookAsync(
        string provider,
        HttpRequest request,
        KycWebhookService webhookService,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > KycWebhookService.MaxBodyBytes)
        {
            return ComplianceResults.Error(
                new Error(
                    ErrorCodes.RequestTooLarge,
                    "The request body is too large.",
                    ErrorType.Validation),
                request.HttpContext,
                StatusCodes.Status413PayloadTooLarge);
        }

        var body = await ReadBodyCappedAsync(request, cancellationToken);
        if (body is null)
        {
            return ComplianceResults.Error(
                new Error(
                    ErrorCodes.RequestTooLarge,
                    "The request body is too large.",
                    ErrorType.Validation),
                request.HttpContext,
                StatusCodes.Status413PayloadTooLarge);
        }

        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "X-Webhook-Timestamp", "X-Webhook-Signature" })
        {
            if (request.Headers.TryGetValue(name, out var value))
            {
                headers[name] = value.ToString();
            }
        }

        var result = await webhookService.ProcessAsync(
            provider,
            body,
            headers,
            request.HttpContext.TraceIdentifier,
            cancellationToken);
        return result.IsSuccess
            ? ComplianceResults.Ok<object?>(null, "Webhook received.", request.HttpContext)
            : ComplianceResults.Error(result.Error!, request.HttpContext);
    }

    private static async Task<string?> ReadBodyCappedAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (memory.Length + read > KycWebhookService.MaxBodyBytes)
            {
                return null;
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }
}
