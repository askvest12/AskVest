using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Trading;

public static class TradingEndpointsExtensions
{
    public static void MapTradingEndpoints(this RouteGroupBuilder api, bool probesEnabled = false)
        => Endpoints.TradingEndpoints.Map(api, probesEnabled);
}
