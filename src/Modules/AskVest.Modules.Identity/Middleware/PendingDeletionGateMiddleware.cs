using System.Security.Claims;
using System.Text.Json;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AskVest.Modules.Identity.Middleware;

/// <summary>
/// Restricts accounts pending deletion to the endpoints marked with
/// <see cref="AllowPendingDeletionAttribute"/> (profile read, deletion cancel
/// and logout). Every other authenticated endpoint answers
/// <c>auth.account_pending_deletion</c>.
/// </summary>
public sealed class PendingDeletionGateMiddleware(RequestDelegate next)
{
    private const string PendingStatus = "DeletionRequested";

    public async Task InvokeAsync(HttpContext context)
    {
        var status = context.User.FindFirst("account_status")?.Value;
        if (status == PendingStatus
            && context.GetEndpoint()?.Metadata.GetMetadata<AllowPendingDeletionAttribute>() is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            var jsonOptions = context.RequestServices
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                .Value.SerializerOptions;
            var payload = new
            {
                success = false,
                message = "The account is pending deletion.",
                data = (object?)null,
                error = new { code = ErrorCodes.AuthAccountPendingDeletion, details = (object?)null },
                correlationId = context.TraceIdentifier,
            };
            await JsonSerializer.SerializeAsync(context.Response.Body, payload, jsonOptions, context.RequestAborted);
            return;
        }

        await next(context);
    }
}
