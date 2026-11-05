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
}
