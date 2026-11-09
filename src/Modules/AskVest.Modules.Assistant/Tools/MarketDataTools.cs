using System.Text.Json;
using AskVest.Modules.Assistant.Contracts;
using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.MarketData.Contracts;

namespace AskVest.Modules.Assistant.Tools;

/// <summary>Latest prices for up to ten instruments, with freshness labels.</summary>
internal sealed class GetPricesTool(IMarketData marketData, ICatalog catalog) : AssistantTool
{
    private const int MaxInstruments = 10;

    public override string Name => "get_prices";

    public override string Description
        => "Get the latest price for up to 10 instruments. Each price carries its freshness label (live, delayed, closed, halted, stale, unavailable) and as-of time.";

    public override string ParametersSchemaJson => """
        {"type":"object","properties":{"instrumentIds":{"type":"array","items":{"type":"string"},"minItems":1,"maxItems":10,"description":"Catalog instrument ids."}},"required":["instrumentIds"],"additionalProperties":false}
        """;

    public override async Task<ToolResult> ExecuteAsync(
        JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var arrayError = ToolArguments.RequireStringArray(
            arguments, "instrumentIds", 1, MaxInstruments, 128, out var ids);
        if (arrayError is not null)
        {
            return ToolResult.Fail("invalid_arguments", arrayError);
        }

        var distinct = ids.Distinct(StringComparer.Ordinal).ToList();
        var prices = await marketData.GetLatestPricesAsync(distinct, cancellationToken);
        var instruments = (await catalog.ListActiveInstrumentsAsync(cancellationToken))
            .ToDictionary(instrument => instrument.Id, StringComparer.Ordinal);
        var asOf = context.TimeProvider.GetUtcNow();

        var payload = new
        {
            asOf = ToolJson.Ts(asOf),
            prices = prices.Select(price =>
            {
                instruments.TryGetValue(price.InstrumentId, out var instrument);
                return new
                {
                    instrumentId = price.InstrumentId,
                    symbol = instrument?.Symbol,
                    name = instrument?.Name,
                    price = ToolJson.Dec(price.Price),
                    currency = price.Currency,
                    asOf = price.AsOf is { } priceTime ? ToolJson.Ts(priceTime) : null,
                    ageSeconds = price.AgeSeconds,
                    session = price.Session,
                    label = price.Label,
                    usableForExecution = price.UsableForExecution,
                    previousClose = ToolJson.Dec(price.PreviousClose),
                };
            }),
        };

        var sources = prices
            .Select(price =>
            {
                instruments.TryGetValue(price.InstrumentId, out var instrument);
                return new ToolSource(
                    "price",
                    price.InstrumentId,
                    instrument?.Symbol ?? price.InstrumentId,
                    price.AsOf ?? asOf,
                    price.Label);
            })
            .ToList();

        return ToolResult.Ok(ToolJson.Serialize(payload), sources);
    }
}

/// <summary>
/// Server-computed candle summary: the model never does the arithmetic.
/// </summary>
internal sealed class GetPriceHistorySummaryTool(IMarketData marketData, ICatalog catalog) : AssistantTool
{
    private static readonly HashSet<string> Intervals = new(StringComparer.Ordinal) { "15m", "1h", "1d" };
    private static readonly TimeSpan MaxRange = TimeSpan.FromDays(366);

    public override string Name => "get_price_history_summary";

    public override string Description
        => "Summarize price history for one instrument over a range: candle count, start, end, high, low and percentage change, computed by the server.";

    public override string ParametersSchemaJson => """
        {"type":"object","properties":{"instrumentId":{"type":"string"},"interval":{"type":"string","enum":["15m","1h","1d"]},"from":{"type":"string","description":"ISO 8601 UTC start, inclusive."},"to":{"type":"string","description":"ISO 8601 UTC end, exclusive."}},"required":["instrumentId","interval","from","to"],"additionalProperties":false}
        """;

    public override async Task<ToolResult> ExecuteAsync(
        JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var idError = ToolArguments.RequireString(arguments, "instrumentId", 128);
        if (idError is not null)
        {
            return ToolResult.Fail("invalid_arguments", idError);
        }

        var intervalError = ToolArguments.RequireString(arguments, "interval", 8);
        var interval = arguments.TryGetProperty("interval", out var intervalElement)
            && intervalElement.ValueKind == JsonValueKind.String
                ? intervalElement.GetString()!
                : string.Empty;
        if (intervalError is not null || !Intervals.Contains(interval))
        {
            return ToolResult.Fail("invalid_arguments", "'interval' must be one of: 15m, 1h, 1d.");
        }

        if (!ToolArguments.TryGetTimestamp(arguments, "from", out var from)
            || !ToolArguments.TryGetTimestamp(arguments, "to", out var to)
            || to <= from
            || to - from > MaxRange)
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "'from' and 'to' must be ISO 8601 timestamps with from before to and a range of at most 366 days.");
        }

        var instrumentId = arguments.GetProperty("instrumentId").GetString()!;
        var instrument = await catalog.FindInstrumentAsync(instrumentId, cancellationToken);
        if (instrument is null)
        {
            return ToolResult.Fail("not_found", $"No instrument with id '{instrumentId}'.");
        }

        var candles = await marketData.GetCandlesAsync(instrumentId, interval, from, to, cancellationToken);
        if (candles.IsFailure)
        {
            return ToolResult.Fail(candles.Error!.Code, candles.Error.Message);
        }

        var set = candles.Value!;
        var ordered = set.Candles.OrderBy(candle => candle.OpenTime).ToList();
        var asOf = context.TimeProvider.GetUtcNow();
        if (ordered.Count == 0)
        {
            var emptyPayload = new
            {
                instrumentId,
                interval,
                from = ToolJson.Ts(from),
                to = ToolJson.Ts(to),
                stale = set.Stale,
                candleCount = 0,
            };
            return ToolResult.Ok(
                ToolJson.Serialize(emptyPayload),
                [new ToolSource("price_history", instrumentId, instrument.Symbol, asOf, set.Stale ? "stale" : null)]);
        }

        var first = ordered[0];
        var last = ordered[^1];
        var high = ordered.Max(candle => candle.High);
        var low = ordered.Min(candle => candle.Low);
        decimal? changePercent = first.Open == 0
            ? null
            : Math.Round((last.Close - first.Open) / first.Open * 100m, 4);
        var partialCount = ordered.Count(candle => candle.Partial);

        var payload = new
        {
            instrumentId,
            symbol = instrument.Symbol,
            interval,
            from = ToolJson.Ts(from),
            to = ToolJson.Ts(to),
            stale = set.Stale,
            candleCount = ordered.Count,
            partialCandles = partialCount,
            start = ToolJson.Dec(first.Open),
            end = ToolJson.Dec(last.Close),
            high = ToolJson.Dec(high),
            low = ToolJson.Dec(low),
            changePercent = ToolJson.Dec(changePercent),
            firstOpenTime = ToolJson.Ts(first.OpenTime),
            lastOpenTime = ToolJson.Ts(last.OpenTime),
        };

        return ToolResult.Ok(
            ToolJson.Serialize(payload),
            [new ToolSource(
                "price_history",
                instrumentId,
                instrument.Symbol,
                last.OpenTime,
                set.Stale ? "stale" : null)]);
    }
}
