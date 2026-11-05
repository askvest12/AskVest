using AskVest.Modules.Identity.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Identity;

public static class IdentityEndpointsExtensions
{
    public static RouteGroupBuilder MapIdentityEndpoints(
        this RouteGroupBuilder group,
        bool phoneVerificationEnabled = false)
    {
        group.MapAuthEndpoints();
        group.MapPinStepUpEndpoints();
        group.MapPhoneEndpoints(phoneVerificationEnabled);
        group.MapAccountEndpoints();
        return group;
    }
}
