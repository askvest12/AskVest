using AskVest.Modules.Compliance.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Compliance;

public static class ComplianceEndpointsExtensions
{
    public static void MapComplianceEndpoints(this RouteGroupBuilder api)
    {
        api.MapGroup("/compliance");
        api.MapGroup("/practice").RequireAuthorization(CompliancePolicyNames.PracticeEligible);
        api.MapGroup("/real").RequireAuthorization(CompliancePolicyNames.RealEligible);
    }
}
