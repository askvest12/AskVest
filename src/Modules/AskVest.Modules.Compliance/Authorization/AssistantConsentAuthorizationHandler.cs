using System.Security.Claims;
using AskVest.Modules.Compliance.Contracts;
using AskVest.Modules.Compliance.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace AskVest.Modules.Compliance.Authorization;

/// <summary>
/// Fails the request when the caller has not accepted the assistant-category
/// documents. Parks a consent_required blocker so the shared result handler
/// answers 403 compliance.not_eligible naming the missing types.
/// </summary>
internal sealed class AssistantConsentAuthorizationHandler(IComplianceEligibility eligibility)
    : AuthorizationHandler<AssistantConsentRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AssistantConsentRequirement requirement)
    {
        if (context.Resource is not HttpContext httpContext
            || !Guid.TryParse(httpContext.User.FindFirst("sub")?.Value, out var userId))
        {
            context.Fail();
            return;
        }

        var missing = await eligibility.GetMissingDocumentTypesAsync(
            userId, "assistant", httpContext.RequestAborted);
        if (missing.Count == 0)
        {
            context.Succeed(requirement);
            return;
        }

        var blocker = new EligibilityBlocker(
            BlockerCodes.ConsentRequired,
            $"Accept the current documents: {string.Join(", ", missing)}.");
        var existing = httpContext.Items.TryGetValue(
            ComplianceAuthorizationResultHandler.BlockersItemKey, out var value)
            && value is IReadOnlyList<EligibilityBlocker> blockers
            ? blockers
            : [];
        httpContext.Items[ComplianceAuthorizationResultHandler.BlockersItemKey] =
            existing.Concat([blocker]).ToList();
        context.Fail();
    }
}
