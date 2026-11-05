using AskVest.Modules.Identity.Contracts;
using AskVest.Modules.Identity.Services;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Identity.Endpoints;

public static class PhoneEndpoints
{
    public static void MapPhoneEndpoints(this RouteGroupBuilder group, bool enabled)
    {
        if (!enabled)
        {
            return;
        }

        group.MapPost("/account/phone", AddPhoneAsync)
            .WithName("AddPhone")
            .WithSummary("Add a phone number and send a verification code.")
            .WithTags("Account")
            .WithMetadata(new RequiresIdempotencyAttribute());

        group.MapPost("/account/phone/verify", VerifyPhoneAsync)
            .WithName("VerifyPhone")
            .WithSummary("Verify the phone number with a one-time code.")
            .WithTags("Account")
            .WithMetadata(new RequiresIdempotencyAttribute());
    }

    private static async Task<IResult> AddPhoneAsync(
        AddPhoneRequest request,
        HttpContext httpContext,
        PhoneService phoneService,
        CancellationToken cancellationToken)
    {
        var userId = UserId(httpContext);
        if (userId is null)
        {
            return Unauthorized(httpContext);
        }

        var result = await phoneService.AddAsync(
            userId.Value, request,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString(),
            httpContext.TraceIdentifier,
            cancellationToken);
        return IdentityResults.FromResult(result, httpContext, "If the number can be verified, a code has been sent.");
    }

    private static async Task<IResult> VerifyPhoneAsync(
        VerifyPhoneRequest request,
        HttpContext httpContext,
        PhoneService phoneService,
        CancellationToken cancellationToken)
    {
        var userId = UserId(httpContext);
        if (userId is null)
        {
            return Unauthorized(httpContext);
        }

        var result = await phoneService.VerifyAsync(
            userId.Value, request,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString(),
            httpContext.TraceIdentifier,
            cancellationToken);
        return IdentityResults.FromResult(result, httpContext, "Phone verified.");
    }

    private static Guid? UserId(HttpContext httpContext)
        => Guid.TryParse(httpContext.User.FindFirst("sub")?.Value, out var id) ? id : null;

    private static IResult Unauthorized(HttpContext httpContext) => IdentityResults.Error(
        new Error(ErrorCodes.AuthUnauthenticated, "Authentication is required.", ErrorType.Unauthenticated),
        httpContext);
}
