using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Wallets;

public static class WalletsEndpointsExtensions
{
    public static void MapWalletsEndpoints(this RouteGroupBuilder api, bool probesEnabled = false)
        => Endpoints.WalletsEndpoints.Map(api, probesEnabled);
}
