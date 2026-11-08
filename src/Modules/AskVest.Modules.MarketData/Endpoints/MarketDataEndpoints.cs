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
    private const int MaxPriceBatch = 50;

    public static void Map(RouteGroupBuilder api, bool probesEnabled)
    {
        api.MapGet("/marketdata/prices", PricesAsync)
            .WithName("GetMarketDataPrices")
            .WithSummary("Latest prices for up to 50 catalog instruments, in request order.")
            .WithTags("MarketData");

        api.MapGet("/marketdata/instruments/{instrumentId}/candles", CandlesAsync)
            .WithName("GetMarketDataCandles")
            .WithSummary("Cached or provider candles for one instrument.")
            .WithTags("MarketData");

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

    private static async Task<IResult> PricesAsync(
        HttpContext httpContext,
        string? instrumentIds,
        MarketDataQueryService queryService,
        CancellationToken cancellationToken)
    {
        var ids = (instrumentIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (ids.Count == 0 || ids.Count > MaxPriceBatch)
        {
            return MarketDataResults.Error(
                new Error(
                    ErrorCodes.ValidationFailed,
                    "instrumentIds must contain between 1 and 50 ids.",
                    ErrorType.Validation),
                httpContext);
        }

        var prices = await queryService.GetLatestPricesAsync(ids, cancellationToken);
        return MarketDataResults.Ok(prices, "Prices retrieved.", httpContext);
    }

    private static async Task<IResult> CandlesAsync(
        HttpContext httpContext,
        string instrumentId,
        string? interval,
        string? from,
        string? to,
        CandleService candleService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(interval)
            || !DateTimeOffset.TryParse(from, out var fromValue)
            || !DateTimeOffset.TryParse(to, out var toValue))
        {
            return MarketDataResults.Error(
                new Error(
                    ErrorCodes.ValidationFailed,
                    "interval, from and to are required and must be valid.",
                    ErrorType.Validation),
                httpContext);
        }

        var result = await candleService.GetCandlesAsync(
            instrumentId,
            interval.Trim(),
            fromValue.ToUniversalTime(),
            toValue.ToUniversalTime(),
            cancellationToken);
        return result.IsSuccess
            ? MarketDataResults.Ok(result.Value, "Candles retrieved.", httpContext)
            : MarketDataResults.Error(result.Error!, httpContext);
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
