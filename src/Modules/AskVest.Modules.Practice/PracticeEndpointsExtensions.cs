using AskVest.Modules.Practice.Endpoints;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Practice;

public static class PracticeEndpointsExtensions
{
    public static void MapPracticeEndpoints(this RouteGroupBuilder api)
        => PracticeEndpoints.Map(api);
}
