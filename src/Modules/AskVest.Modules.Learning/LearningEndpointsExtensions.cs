using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Learning;

public static class LearningEndpointsExtensions
{
    public static void MapLearningEndpoints(this RouteGroupBuilder api)
    {
        Endpoints.LearningEndpoints.Map(api);
    }
}
