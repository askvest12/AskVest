using System.Security.Claims;
using AskVest.Modules.Compliance.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace AskVest.Modules.Compliance.Authorization;

/// <summary>
/// Fails the request when the caller is not eligible for the policy's mode.
/// The blocker list is parked on HttpContext.Items so the authorization result
/// handler can report it; the handler itself cannot write the response.
/// </summary>
internal sealed class EligibilityAuthorizationHandler(IComplianceEligibility eligibility)
    : AuthorizationHandler<EligibilityRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        EligibilityRequirement requirement)
    {
        if (context.Resource is not HttpContext httpContext
            || !Guid.TryParse(httpContext.User.FindFirst("sub")?.Value, out var userId))
        {
            context.Fail();
            return;
        }

        var result = await eligibility.GetEligibilityAsync(userId, httpContext.RequestAborted);
        var allowed = requirement.IsReal ? result.RealAllowed : result.PracticeAllowed;
        if (allowed)
        {
            context.Succeed(requirement);
            return;
        }

        var blockers = requirement.IsReal ? result.RealBlockers : result.PracticeBlockers;
        httpContext.Items[ComplianceAuthorizationResultHandler.BlockersItemKey] = blockers;
        context.Fail();
    }
}
