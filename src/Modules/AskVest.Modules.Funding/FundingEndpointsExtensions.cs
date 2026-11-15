using AskVest.Modules.Compliance.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Funding;

public static class FundingEndpointsExtensions
{
    public static void MapFundingEndpoints(this RouteGroupBuilder api, bool probesEnabled = false)
    {
        api.MapGroup("/real").RequireAuthorization(CompliancePolicyNames.RealEligible);
    }
}
