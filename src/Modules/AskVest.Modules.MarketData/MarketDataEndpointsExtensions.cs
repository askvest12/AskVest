using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.MarketData;

public static class MarketDataEndpointsExtensions
{
    public static void MapMarketDataEndpoints(this RouteGroupBuilder api, bool probesEnabled)
    {
        Endpoints.MarketDataEndpoints.Map(api, probesEnabled);
    }
}
