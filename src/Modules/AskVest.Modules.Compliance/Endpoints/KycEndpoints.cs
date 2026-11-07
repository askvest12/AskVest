using AskVest.Modules.Compliance.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using AskVest.SharedKernel;

namespace AskVest.Modules.Compliance.Endpoints;

public static class KycEndpoints
{
    public static void MapKycEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/kyc/start", StartKycAsync)
            .WithName("StartKycVerification")
            .WithSummary("Start or resume identity verification with the configured provider.")
            .WithTags("Compliance")
            .WithMetadata(new IdempotencyExemptAttribute(), new ReturnsSecretsAttribute());
    }

    private static async Task<IResult> StartKycAsync(
        HttpContext httpContext,
        KycService kycService,
        CancellationToken cancellationToken)
    {
        var result = await kycService.StartAsync(
            DocumentEndpoints.CurrentUserId(httpContext),
            httpContext.TraceIdentifier,
            cancellationToken);
        return result.IsSuccess
            ? ComplianceResults.Ok(new
            {
                providerReference = result.Value.ProviderReference,
                sessionUrl = result.Value.SessionUrl,
                kycStatus = result.Value.KycStatus,
            }, "Verification session ready.", httpContext)
            : ComplianceResults.Error(result.Error!, httpContext);
    }
}
