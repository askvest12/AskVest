using AskVest.Modules.Identity.Contracts;
using AskVest.Modules.Identity.Services;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Identity.Endpoints;

public static class PinStepUpEndpoints
{
    public static void MapPinStepUpEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/account/pin", SetPinAsync)
            .WithName("SetPin")
            .WithSummary("Set the transaction PIN. Requires the account password.")
            .WithTags("Account")
            .WithMetadata(new RequiresIdempotencyAttribute());

        group.MapPost("/account/pin/change", ChangePinAsync)
            .WithName("ChangePin")
            .WithSummary("Change the transaction PIN using the current PIN.")
            .WithTags("Account")
            .WithMetadata(new RequiresIdempotencyAttribute());

        group.MapPost("/account/pin/reset-request", ResetPinRequestAsync)
            .WithName("RequestPinReset")
            .WithSummary("Send a PIN reset code to the account email.")
            .WithTags("Account")
            .WithMetadata(new RequiresIdempotencyAttribute());

        group.MapPost("/account/pin/reset", ResetPinAsync)
            .WithName("ResetPin")
            .WithSummary("Consume a PIN reset code, verify the password and set a new PIN.")
            .WithTags("Account")
            .WithMetadata(new RequiresIdempotencyAttribute());

        group.MapPost("/auth/step-up", StepUpAsync)
            .WithName("StepUp")
            .WithSummary("Verify the PIN and receive a single-use step-up grant token.")
            .WithTags("Auth")
            .WithMetadata(new IdempotencyExemptAttribute(), new ReturnsSecretsAttribute());
    }

    private static async Task<IResult> SetPinAsync(
        SetPinRequest request,
        HttpContext httpContext,
        PinService pinService,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(httpContext, out var userId, out _))
        {
            return Unauthorized(httpContext);
        }

        var result = await pinService.SetAsync(
            userId, request, Ip(httpContext), UserAgent(httpContext), httpContext.TraceIdentifier, cancellationToken);
        return IdentityResults.FromResult(result, httpContext, "PIN set.");
    }

    private static async Task<IResult> ChangePinAsync(
        ChangePinRequest request,
        HttpContext httpContext,
        PinService pinService,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(httpContext, out var userId, out _))
        {
            return Unauthorized(httpContext);
        }

        var attempt = await pinService.ChangeAsync(
            userId, request, Ip(httpContext), UserAgent(httpContext), httpContext.TraceIdentifier, cancellationToken);
        if (attempt.Failure is { } failure)
        {
            return IdentityResults.Error(failure, httpContext, attempt.RetryAfter);
        }

        return IdentityResults.Ok<object?>(null, "PIN changed.", httpContext);
    }

    private static async Task<IResult> ResetPinRequestAsync(
        HttpContext httpContext,
        PinService pinService,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(httpContext, out var userId, out _))
        {
            return Unauthorized(httpContext);
        }

        var result = await pinService.RequestResetAsync(
            userId, Ip(httpContext), UserAgent(httpContext), httpContext.TraceIdentifier, cancellationToken);
        return IdentityResults.FromResult(result, httpContext, "If a PIN reset is available, a code has been sent.");
    }

    private static async Task<IResult> ResetPinAsync(
        ResetPinRequest request,
        HttpContext httpContext,
        PinService pinService,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(httpContext, out var userId, out _))
        {
            return Unauthorized(httpContext);
        }

        var result = await pinService.ResetAsync(
            userId, request, Ip(httpContext), UserAgent(httpContext), httpContext.TraceIdentifier, cancellationToken);
        return IdentityResults.FromResult(result, httpContext, "PIN reset.");
    }

    private static async Task<IResult> StepUpAsync(
        StepUpRequest request,
        HttpContext httpContext,
        StepUpService stepUpService,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(httpContext, out var userId, out var sessionId))
        {
            return Unauthorized(httpContext);
        }

        var result = await stepUpService.IssueAsync(
            userId, sessionId, request, Ip(httpContext), UserAgent(httpContext), httpContext.TraceIdentifier, cancellationToken);
        if (result.Grant is { } grant)
        {
            return IdentityResults.Ok(
                new { grantToken = grant.Token, expiresAt = grant.ExpiresAt },
                "Step-up grant issued.",
                httpContext);
        }

        return IdentityResults.Error(result.Failure!, httpContext, result.RetryAfter);
    }

    private static bool TryGetIdentity(HttpContext httpContext, out Guid userId, out Guid sessionId)
    {
        userId = Guid.Empty;
        sessionId = Guid.Empty;
        var userIdText = httpContext.User.FindFirst("sub")?.Value;
        var sessionIdText = httpContext.User.FindFirst("sid")?.Value;
        return Guid.TryParse(userIdText, out userId) && Guid.TryParse(sessionIdText, out sessionId);
    }

    private static IResult Unauthorized(HttpContext httpContext) => IdentityResults.Error(
        new Error(ErrorCodes.AuthUnauthenticated, "Authentication is required.", ErrorType.Unauthenticated),
        httpContext);

    private static string? Ip(HttpContext httpContext)
        => httpContext.Connection.RemoteIpAddress?.ToString();

    private static string? UserAgent(HttpContext httpContext)
        => httpContext.Request.Headers.UserAgent.ToString();
}
