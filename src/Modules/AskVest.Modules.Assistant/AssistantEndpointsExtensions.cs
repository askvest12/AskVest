using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Assistant;

public static class AssistantEndpointsExtensions
{
    public static void MapAssistantEndpoints(this RouteGroupBuilder api, bool probesEnabled = false)
    {
        Endpoints.AssistantEndpoints.Map(api, probesEnabled);
    }
}
