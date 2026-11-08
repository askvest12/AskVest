using AskVest.Modules.MarketData.Domain;
using AskVest.Modules.MarketData.Providers;
using AskVest.Modules.MarketData.Services;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.MarketData.Endpoints;

public static class MarketDataEndpoints
{
    public static void Map(RouteGroupBuilder api, bool probesEnabled)
    {
        if (!probesEnabled)
        {
            return;
        }

        api.MapPost("/diagnostics/marketdata-scenario", ScenarioProbeAsync)
            .WithName("SetMarketDataScenario")
            .WithSummary("Diagnostic: set the fake market data provider scenario and run one poll cycle.")
            .WithTags("Diagnostics")
            .WithMetadata(new RequiresIdempotencyAttribute())
            .ExcludeFromDescription();
    }

    private static async Task<IResult> ScenarioProbeAsync(
        HttpContext httpContext,
        ScenarioProbeRequest request,
        IMarketDataProvider provider,
        PricePoller poller,
        CancellationToken cancellationToken)
    {
        if (provider is not FakeMarketDataProvider fake)
        {
            return MarketDataResults.Ok(
                new { applied = false },
                "Probe ignored: the configured provider is not the fake.",
                httpContext);
        }

        var scenario = request.Scenario?.Trim() ?? string.Empty;
        if (!MarketDataVocabulary.Scenarios.Contains(scenario))
        {
            return MarketDataResults.Error(
                new Error(ErrorCodes.ValidationFailed, "The scenario is not recognized.", ErrorType.Validation),
                httpContext);
        }

        fake.SetScenario(scenario);
        var polled = await poller.RunCycleAsync(cancellationToken);
        return MarketDataResults.Ok(
            new { applied = true, polled },
            "Scenario set and one poll cycle ran.",
            httpContext);
    }

    private sealed record ScenarioProbeRequest(string? Scenario);
}
