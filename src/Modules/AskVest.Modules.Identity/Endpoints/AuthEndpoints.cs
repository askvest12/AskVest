using AskVest.Modules.Identity.Contracts;
using AskVest.Modules.Identity.Services;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Identity.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/auth/sign-up", SignUpAsync)
            .WithName("SignUp")
            .WithSummary("Register a new account and send an email verification code.")
            .WithTags("Auth")
            .WithMetadata(new RequiresIdempotencyAttribute())
            .AllowAnonymous()
            .RequireRateLimiting("strict");

        group.MapPost("/auth/verify-email", VerifyEmailAsync)
            .WithName("VerifyEmail")
            .WithSummary("Verify an email address with a one-time code.")
            .WithTags("Auth")
            .WithMetadata(new RequiresIdempotencyAttribute())
            .AllowAnonymous()
            .RequireRateLimiting("strict");

        group.MapPost("/auth/resend-verification", ResendVerificationAsync)
            .WithName("ResendVerification")
            .WithSummary("Resend the email verification code if allowed.")
            .WithTags("Auth")
            .WithMetadata(new RequiresIdempotencyAttribute())
            .AllowAnonymous()
            .RequireRateLimiting("strict");

        group.MapPost("/auth/login", LoginAsync)
            .WithName("Login")
            .WithSummary("Sign in with email and password, or receive a device verification challenge.")
            .WithTags("Auth")
            .WithMetadata(new IdempotencyExemptAttribute(), new ReturnsSecretsAttribute())
            .AllowAnonymous()
            .RequireRateLimiting("strict");

        group.MapPost("/auth/verify-device", VerifyDeviceAsync)
            .WithName("VerifyDevice")
            .WithSummary("Complete device verification and receive a session.")
            .WithTags("Auth")
            .WithMetadata(new IdempotencyExemptAttribute(), new ReturnsSecretsAttribute())
            .AllowAnonymous()
            .RequireRateLimiting("strict");

        group.MapPost("/auth/refresh", RefreshAsync)
            .WithName("Refresh")
            .WithSummary("Rotate a refresh token and receive a new token pair.")
            .WithTags("Auth")
            .WithMetadata(new IdempotencyExemptAttribute(), new ReturnsSecretsAttribute())
            .AllowAnonymous()
            .RequireRateLimiting("strict");

        group.MapPost("/auth/logout", LogoutAsync)
            .WithName("Logout")
            .WithSummary("Revoke the current session and its refresh tokens.")
            .WithTags("Auth")
            .WithMetadata(new RequiresIdempotencyAttribute());
    }

    private static async Task<IResult> SignUpAsync(
        SignUpRequest request,
        HttpContext httpContext,
        SignUpService signUpService,
        CancellationToken cancellationToken)
    {
        var result = await signUpService.SignUpAsync(
            request.Email, request.Password,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString(),
            httpContext.TraceIdentifier,
            cancellationToken);
        return IdentityResults.FromResult(result, httpContext, "If this email can be verified, a verification code has been sent.");
    }

    private static async Task<IResult> VerifyEmailAsync(
        VerifyEmailRequest request,
        HttpContext httpContext,
        EmailVerificationService verificationService,
        CancellationToken cancellationToken)
    {
        var result = await verificationService.VerifyAsync(
            request.Email, request.Code,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString(),
            httpContext.TraceIdentifier,
            cancellationToken);
        return IdentityResults.FromResult(result, httpContext, "Email verified.");
    }

    private static async Task<IResult> ResendVerificationAsync(
        ResendVerificationRequest request,
        HttpContext httpContext,
        EmailVerificationService verificationService,
        CancellationToken cancellationToken)
    {
        var result = await verificationService.ResendAsync(
            request.Email,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString(),
            httpContext.TraceIdentifier,
            cancellationToken);
        return IdentityResults.FromResult(result, httpContext, "If this email can be verified, a verification code has been sent.");
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext httpContext,
        LoginService loginService,
        CancellationToken cancellationToken)
    {
        var attempt = await loginService.LoginAsync(
            request,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString(),
            httpContext.TraceIdentifier,
            cancellationToken);
        return ToLoginResult(attempt, httpContext);
    }

    private static async Task<IResult> VerifyDeviceAsync(
        VerifyDeviceRequest request,
        HttpContext httpContext,
        VerifyDeviceService verifyDeviceService,
        CancellationToken cancellationToken)
    {
        var attempt = await verifyDeviceService.VerifyAsync(
            request,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString(),
            httpContext.TraceIdentifier,
            cancellationToken);
        return ToLoginResult(attempt, httpContext);
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        HttpContext httpContext,
        RefreshService refreshService,
        CancellationToken cancellationToken)
    {
        var result = await refreshService.RefreshAsync(
            request,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString(),
            httpContext.TraceIdentifier,
            cancellationToken);
        if (result.IsSuccess)
        {
            var session = result.Value!;
            return IdentityResults.Ok(
                new
                {
                    result = "authenticated",
                    accessToken = session.AccessToken,
                    refreshToken = session.RefreshToken,
                    accessTokenExpiresAt = session.AccessTokenExpiresAt,
                    refreshIdleExpiresAt = session.RefreshIdleExpiresAt,
                    sessionId = session.SessionId,
                },
                "Token refreshed.",
                httpContext);
        }

        return IdentityResults.Error(result.Error!, httpContext);
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext httpContext,
        SessionService sessionService,
        CancellationToken cancellationToken)
    {
        var userIdText = httpContext.User.FindFirst("sub")?.Value;
        var sessionIdText = httpContext.User.FindFirst("sid")?.Value;
        if (!Guid.TryParse(userIdText, out var userId) || !Guid.TryParse(sessionIdText, out var sessionId))
        {
            return IdentityResults.Error(
                new Error(ErrorCodes.AuthUnauthenticated, "Authentication is required.", ErrorType.Unauthenticated),
                httpContext);
        }

        await sessionService.RevokeAsync(
            sessionId,
            userId,
            "logout",
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString(),
            httpContext.TraceIdentifier,
            cancellationToken);
        return IdentityResults.FromResult(Result.Success(), httpContext, "Signed out.");
    }

    private static IResult ToLoginResult(LoginAttempt attempt, HttpContext httpContext)
    {
        if (attempt.RetryAfter is { } retryAfter)
        {
            return IdentityResults.Error(attempt.Failure!, httpContext, retryAfter);
        }

        if (attempt.Outcome is { IsSuccess: true } result && result.Value is { } outcome)
        {
            return outcome switch
            {
                LoginAuthenticated authenticated => IdentityResults.Ok(LoginPayload(authenticated.Session), "Signed in.", httpContext),
                LoginDeviceVerificationRequired challenge => IdentityResults.Ok(
                    new
                    {
                        result = "device_verification_required",
                        challengeId = challenge.ChallengeId,
                        expiresAt = challenge.ExpiresAt,
                    },
                    "Device verification required.",
                    httpContext),
                _ => IdentityResults.Error(attempt.Failure!, httpContext),
            };
        }

        return IdentityResults.Error(attempt.Failure!, httpContext);
    }

    private static object LoginPayload(IssuedSession session) => new
    {
        result = "authenticated",
        accessToken = session.AccessToken,
        refreshToken = session.RefreshToken,
        accessTokenExpiresAt = session.AccessTokenExpiresAt,
        refreshIdleExpiresAt = session.RefreshIdleExpiresAt,
        sessionId = session.SessionId,
    };
}
