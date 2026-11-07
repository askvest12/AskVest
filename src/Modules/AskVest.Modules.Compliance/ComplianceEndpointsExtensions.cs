using AskVest.Modules.Compliance.Contracts;
using AskVest.Modules.Compliance.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Compliance;

public static class ComplianceEndpointsExtensions
{
    public static void MapComplianceEndpoints(this RouteGroupBuilder api, bool probesEnabled = false)
    {
        var compliance = api.MapGroup("/compliance");
        compliance.MapDocumentEndpoints();
        compliance.MapCountryEndpoints();
        compliance.MapEligibilityEndpoints();

        var practice = api.MapGroup("/practice")
            .RequireAuthorization(CompliancePolicyNames.PracticeEligible);
        practice.MapProbeEndpoints(probesEnabled, "practice");

        var real = api.MapGroup("/real")
            .RequireAuthorization(CompliancePolicyNames.RealEligible);
        real.MapProbeEndpoints(probesEnabled, "real");
    }
}
