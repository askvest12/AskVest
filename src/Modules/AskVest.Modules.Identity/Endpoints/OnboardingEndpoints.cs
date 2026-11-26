using AskVest.Modules.Identity.Contracts;
using AskVest.Modules.Identity.Services;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Identity.Endpoints;

public static class OnboardingEndpoints
{
    public static void MapOnboardingEndpoints(this RouteGroupBuilder group)
    {
        group.MapPut("/account/onboarding", SaveOnboardingAsync)
            .WithName("SaveOnboarding")
            .WithSummary("Save onboarding answers and display name. All fields optional; safe to replay.")
            .WithTags("Account")
            .WithMetadata(new RequiresIdempotencyAttribute());
    }

    private static async Task<IResult> SaveOnboardingAsync(
        OnboardingRequest request,
        HttpContext httpContext,
        OnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        var userIdText = httpContext.User.FindFirst("sub")?.Value;
        if (!Guid.TryParse(userIdText, out var userId))
        {
            return IdentityResults.Error(
                new Error(ErrorCodes.AuthUnauthenticated, "Authentication is required.", ErrorType.Unauthenticated),
                httpContext);
        }

        var result = await onboardingService.SaveAsync(userId, request, cancellationToken);
        return IdentityResults.FromResult(result, httpContext, "Onboarding saved.");
    }
}
