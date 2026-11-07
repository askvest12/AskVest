using AskVest.Modules.Compliance.Contracts;
using AskVest.Modules.Compliance.Domain.Enums;
using AskVest.Modules.Compliance.Persistence;
using AskVest.Modules.Compliance.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Compliance.Endpoints;

public static class EligibilityEndpoints
{
    public static void MapEligibilityEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/eligibility", GetEligibilityAsync)
            .WithName("GetComplianceEligibility")
            .WithSummary("Practice and Real eligibility flags, KYC status and blockers.")
            .WithTags("Compliance");
    }

    private static async Task<IResult> GetEligibilityAsync(
        HttpContext httpContext,
        IComplianceEligibility eligibility,
        ComplianceDbContext db,
        CancellationToken cancellationToken)
    {
        var userId = DocumentEndpoints.CurrentUserId(httpContext);
        var result = await eligibility.GetEligibilityAsync(userId, cancellationToken);
        var profile = await db.ComplianceProfiles.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => p.KycStatus)
            .FirstOrDefaultAsync(cancellationToken);
        return ComplianceResults.Ok(new
        {
            practiceAllowed = result.PracticeAllowed,
            realAllowed = result.RealAllowed,
            kycStatus = profile.ToString(),
            blockers = new
            {
                practice = result.PracticeBlockers.Select(BlockerJson),
                real = result.RealBlockers.Select(BlockerJson),
            },
        }, "Eligibility retrieved.", httpContext);
    }

    private static object BlockerJson(EligibilityBlocker blocker)
        => new { code = blocker.Code, message = blocker.Message };
}
