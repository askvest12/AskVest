using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Portfolio;

public static class PortfolioEndpointsExtensions
{
    public static void MapPortfolioEndpoints(this RouteGroupBuilder api)
    {
        Endpoints.PortfolioEndpoints.Map(api);
    }
}
