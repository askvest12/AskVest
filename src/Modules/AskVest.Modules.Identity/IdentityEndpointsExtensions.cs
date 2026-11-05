using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Identity;

public static class IdentityEndpointsExtensions
{
    public static RouteGroupBuilder MapIdentityEndpoints(this RouteGroupBuilder group)
    {
        return group;
    }
}
