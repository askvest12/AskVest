using AskVest.Modules.Compliance.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Trading.Endpoints;

public static class TradingEndpoints
{
    public static void Map(RouteGroupBuilder api, bool probesEnabled)
    {
        api.MapGroup("/real").RequireAuthorization(CompliancePolicyNames.RealEligible);
    }
}
