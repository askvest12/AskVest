using System.Text.Json;
using AskVest.Modules.Compliance.Contracts;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Compliance.Authorization;

/// <summary>
/// Replaces the default authorization result handler so an eligibility failure
/// answers 403 compliance.not_eligible with one detail per blocker. Every other
/// outcome keeps the stock challenge/forbid behavior.
/// </summary>
internal sealed class ComplianceAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    public const string BlockersItemKey = "AskVest.Compliance.EligibilityBlockers";

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Succeeded)
        {
            await next(context);
            return;
        }

        if (authorizeResult.Forbidden
            && policy.Requirements.OfType<EligibilityRequirement>().Any()
            && context.Items.TryGetValue(BlockersItemKey, out var value)
            && value is IReadOnlyList<EligibilityBlocker> blockers)
        {
            await WriteNotEligibleAsync(context, blockers);
            return;
        }

        if (authorizeResult.Challenged)
        {
            if (context.User.Identity?.IsAuthenticated == true)
            {
                await context.ForbidAsync();
            }
            else
            {
                await context.ChallengeAsync();
            }

            return;
        }

        await context.ForbidAsync();
    }

    private static async Task WriteNotEligibleAsync(HttpContext context, IReadOnlyList<EligibilityBlocker> blockers)
    {
        var details = blockers
            .Select(blocker => (object)new { field = blocker.Code, message = blocker.Message })
            .ToList();
        var payload = new
        {
            success = false,
            message = "The account is not eligible for this mode.",
            data = (object?)null,
            error = new { code = ErrorCodes.ComplianceNotEligible, details },
            correlationId = context.TraceIdentifier,
        };
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        var jsonOptions = context.RequestServices
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        await JsonSerializer.SerializeAsync(context.Response.Body, payload, jsonOptions, context.RequestAborted);
    }
}
