using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Catalog;

public static class CatalogEndpointsExtensions
{
    public static void MapCatalogEndpoints(this RouteGroupBuilder api)
    {
        Endpoints.CatalogEndpoints.Map(api);
    }
}
