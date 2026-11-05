using AskVest.Modules.Identity.Contracts;
using AskVest.Modules.Identity.Services;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Identity.Endpoints;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/account/me", MeAsync)
            .WithName("GetMe")
            .WithSummary("Get the authenticated account profile.")
            .WithTags("Account")
            .WithMetadata(new AllowPendingDeletionAttribute());

        group.MapGet("/account/sessions", ListSessionsAsync)
            .WithName("ListSessions")
            .WithSummary("List the caller's active sessions.")
            .WithTags("Account");

        group.MapPost("/account/sessions/{sessionId:guid}/revoke", RevokeSessionAsync)
            .WithName("RevokeSession")
            .WithSummary("Revoke one of the caller's sessions.")
            .WithTags("Account")
            .WithMetadata(new RequiresIdempotencyAttribute());

        group.MapPost("/account/sessions/revoke-others", RevokeOtherSessionsAsync)
            .WithName("RevokeOtherSessions")
            .WithSummary("Revoke every session except the current one.")
            .WithTags("Account")
            .WithMetadata(new RequiresIdempotencyAttribute());

        group.MapGet("/account/devices", ListDevicesAsync)
            .WithName("ListDevices")
            .WithSummary("List the caller's devices with trust state.")
            .WithTags("Account");

        group.MapPost("/account/devices/{deviceId:guid}/revoke", RevokeDeviceAsync)
            .WithName("RevokeDevice")
            .WithSummary("Revoke a device and all of its sessions.")
            .WithTags("Account")
            .WithMetadata(new RequiresIdempotencyAttribute());

        group.MapGet("/account/security-events", SecurityEventsAsync)
            .WithName("ListSecurityEvents")
            .WithSummary("List the caller's security history, newest first.")
            .WithTags("Account");

        group.MapPost("/account/deletion-request", DeletionRequestAsync)
            .WithName("RequestAccountDeletion")
            .WithSummary("Request account deletion after password and step-up confirmation.")
            .WithTags("Account")
            .WithMetadata(new RequiresIdempotencyAttribute());

        group.MapPost("/account/deletion-cancel", DeletionCancelAsync)
            .WithName("CancelAccountDeletion")
            .WithSummary("Cancel a pending account deletion.")
            .WithTags("Account")
            .WithMetadata(new RequiresIdempotencyAttribute(), new AllowPendingDeletionAttribute());
    }

    private static async Task<IResult> DeletionRequestAsync(
        DeletionRequest request,
        HttpContext httpContext,
        DeletionService deletionService,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(httpContext, out var userId, out var sessionId))
        {
            return Unauthorized(httpContext);
        }

        var result = await deletionService.RequestAsync(
            userId, sessionId, request.Password, request.StepUpToken,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString(),
            httpContext.TraceIdentifier,
            cancellationToken);
        return IdentityResults.FromResult(result, httpContext, "Account deletion scheduled.");
    }

    private static async Task<IResult> DeletionCancelAsync(
        HttpContext httpContext,
        DeletionService deletionService,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(httpContext, out var userId, out _))
        {
            return Unauthorized(httpContext);
        }

        var result = await deletionService.CancelAsync(
            userId,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString(),
            httpContext.TraceIdentifier,
            cancellationToken);
        return IdentityResults.FromResult(result, httpContext, "Account deletion cancelled.");
    }

    private static async Task<IResult> MeAsync(
        HttpContext httpContext,
        AccountService accountService,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(httpContext, out var userId, out var sessionId))
        {
            return Unauthorized(httpContext);
        }

        var result = await accountService.MeAsync(userId, cancellationToken);
        return IdentityResults.FromResult(result, httpContext, "Profile loaded.");
    }

    private static async Task<IResult> ListSessionsAsync(
        HttpContext httpContext,
        AccountService accountService,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(httpContext, out var userId, out var sessionId))
        {
            return Unauthorized(httpContext);
        }

        var items = await accountService.ListSessionsAsync(userId, sessionId, cancellationToken);
        return IdentityResults.Ok(items, "Sessions loaded.", httpContext);
    }

    private static async Task<IResult> RevokeSessionAsync(
        Guid sessionId,
        HttpContext httpContext,
        AccountService accountService,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(httpContext, out var userId, out _))
        {
            return Unauthorized(httpContext);
        }

        var result = await accountService.RevokeSessionAsync(
            userId, sessionId,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString(),
            httpContext.TraceIdentifier,
            cancellationToken);
        return IdentityResults.FromResult(result, httpContext, "Session revoked.");
    }

    private static async Task<IResult> RevokeOtherSessionsAsync(
        HttpContext httpContext,
        AccountService accountService,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(httpContext, out var userId, out var sessionId))
        {
            return Unauthorized(httpContext);
        }

        var result = await accountService.RevokeOtherSessionsAsync(
            userId, sessionId,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString(),
            httpContext.TraceIdentifier,
            cancellationToken);
        return IdentityResults.FromResult(result, httpContext, "Other sessions revoked.");
    }

    private static async Task<IResult> ListDevicesAsync(
        HttpContext httpContext,
        AccountService accountService,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(httpContext, out var userId, out var sessionId))
        {
            return Unauthorized(httpContext);
        }

        var items = await accountService.ListDevicesAsync(userId, sessionId, cancellationToken);
        return IdentityResults.Ok(items, "Devices loaded.", httpContext);
    }

    private static async Task<IResult> RevokeDeviceAsync(
        Guid deviceId,
        HttpContext httpContext,
        AccountService accountService,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(httpContext, out var userId, out _))
        {
            return Unauthorized(httpContext);
        }

        var result = await accountService.RevokeDeviceAsync(
            userId, deviceId,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString(),
            httpContext.TraceIdentifier,
            cancellationToken);
        return IdentityResults.FromResult(result, httpContext, "Device revoked.");
    }

    private static async Task<IResult> SecurityEventsAsync(
        HttpContext httpContext,
        AccountService accountService,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(httpContext, out var userId, out _))
        {
            return Unauthorized(httpContext);
        }

        var page = await accountService.SecurityEventsAsync(
            userId,
            Math.Clamp(limit ?? PageRequest.DefaultLimit, 1, PageRequest.MaxLimit),
            cursor,
            cancellationToken);
        return IdentityResults.Ok(
            new { items = page.Items, nextCursor = page.NextCursor, hasMore = page.HasMore, limit = page.Limit },
            "Security history loaded.",
            httpContext);
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
}
