using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Funding;

public static class FundingEndpointsExtensions
{
    public static void MapFundingEndpoints(this RouteGroupBuilder api, bool probesEnabled = false)
        => Endpoints.FundingEndpoints.Map(api, probesEnabled);
}
