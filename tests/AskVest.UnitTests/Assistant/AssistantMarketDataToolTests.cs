using System.Globalization;
using System.Text.Json;
using AskVest.Modules.Assistant.Tools;
using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.MarketData.Contracts;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Assistant;

public sealed class AssistantMarketDataToolTests
{
    private static ToolContext Context() => new(Guid.CreateVersion7(), TimeProvider.System);

    private static JsonElement Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static CatalogInstrument Instrument(string id, string symbol)
        => new(id, "stock", $"{symbol} sample", symbol, null, null, "US", "USD", null, "active", null);

    [Fact]
    public async Task GetPrices_Returns_Labels_And_Currency()
    {
        var marketData = new StubMarketData();
        marketData.Prices.Add(new MarketDataPrice(
            "inst-aapl", 123.45m, "USD", new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            30, "regular", "live", true, "fake", 120m));
        var catalog = new StubCatalog();
        catalog.Instruments.Add(Instrument("inst-aapl", "AAPL"));

        var tool = new GetPricesTool(marketData, catalog);
        var result = await tool.ExecuteAsync(
            Args("""{"instrumentIds":["inst-aapl"]}"""), Context(), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        result.Sources.ShouldContain(source => source.Label == "live");
        using var payload = JsonDocument.Parse(result.PayloadJson);
        var price = payload.RootElement.GetProperty("prices")[0];
        price.GetProperty("price").GetString().ShouldBe("123.45");
        price.GetProperty("label").GetString().ShouldBe("live");
        price.GetProperty("usableForExecution").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task GetPrices_Rejects_Empty_And_Oversized_Lists()
    {
        var tool = new GetPricesTool(new StubMarketData(), new StubCatalog());

        var empty = await tool.ExecuteAsync(
            Args("""{"instrumentIds":[]}"""), Context(), TestContext.Current.CancellationToken);
        empty.IsError.ShouldBeTrue();

        var oversized = await tool.ExecuteAsync(
            Args("""{"instrumentIds":["a","b","c","d","e","f","g","h","i","j","k"]}"""),
            Context(), TestContext.Current.CancellationToken);
        oversized.IsError.ShouldBeTrue();
        oversized.ErrorCode.ShouldBe("invalid_arguments");
    }

    [Fact]
    public async Task PriceHistorySummary_Computes_Server_Side_From_Candles()
    {
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var candles = new[]
        {
            new MarketDataCandle(from, 100m, 105m, 95m, 102m, 10m, false, false),
            new MarketDataCandle(from.AddHours(1), 102m, 120m, 100m, 110m, 12m, false, false),
            new MarketDataCandle(from.AddHours(2), 110m, 115m, 108m, 125m, 9m, false, true),
        };
        var marketData = new StubMarketData { CandleSet = new MarketDataCandleSet(false, candles) };
        var catalog = new StubCatalog();
        catalog.Instruments.Add(Instrument("inst-aapl", "AAPL"));

        var tool = new GetPriceHistorySummaryTool(marketData, catalog);
        var result = await tool.ExecuteAsync(
            Args("""{"instrumentId":"inst-aapl","interval":"1h","from":"2026-01-01T00:00:00Z","to":"2026-01-02T00:00:00Z"}"""),
            Context(), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        using var payload = JsonDocument.Parse(result.PayloadJson);
        var root = payload.RootElement;
        root.GetProperty("candleCount").GetInt32().ShouldBe(3);
        root.GetProperty("partialCandles").GetInt32().ShouldBe(1);
        root.GetProperty("start").GetString().ShouldBe("100");
        root.GetProperty("end").GetString().ShouldBe("125");
        root.GetProperty("high").GetString().ShouldBe("120");
        root.GetProperty("low").GetString().ShouldBe("95");
        decimal.Parse(root.GetProperty("changePercent").GetString()!, CultureInfo.InvariantCulture)
            .ShouldBe(25m);
        result.Sources.ShouldContain(source => source.Type == "price_history");
    }

    [Fact]
    public async Task PriceHistorySummary_Rejects_Bad_Range_And_Interval()
    {
        var marketData = new StubMarketData
        {
            CandleSet = new MarketDataCandleSet(false, []),
        };
        var catalog = new StubCatalog();
        catalog.Instruments.Add(Instrument("inst-aapl", "AAPL"));
        var tool = new GetPriceHistorySummaryTool(marketData, catalog);

        var badInterval = await tool.ExecuteAsync(
            Args("""{"instrumentId":"inst-aapl","interval":"5m","from":"2026-01-01T00:00:00Z","to":"2026-01-02T00:00:00Z"}"""),
            Context(), TestContext.Current.CancellationToken);
        badInterval.ErrorCode.ShouldBe("invalid_arguments");

        var reversed = await tool.ExecuteAsync(
            Args("""{"instrumentId":"inst-aapl","interval":"1d","from":"2026-01-02T00:00:00Z","to":"2026-01-01T00:00:00Z"}"""),
            Context(), TestContext.Current.CancellationToken);
        reversed.ErrorCode.ShouldBe("invalid_arguments");

        var tooWide = await tool.ExecuteAsync(
            Args("""{"instrumentId":"inst-aapl","interval":"1d","from":"2024-01-01T00:00:00Z","to":"2026-01-01T00:00:00Z"}"""),
            Context(), TestContext.Current.CancellationToken);
        tooWide.ErrorCode.ShouldBe("invalid_arguments");

        var unknown = await tool.ExecuteAsync(
            Args("""{"instrumentId":"inst-nope","interval":"1d","from":"2026-01-01T00:00:00Z","to":"2026-01-02T00:00:00Z"}"""),
            Context(), TestContext.Current.CancellationToken);
        unknown.ErrorCode.ShouldBe("not_found");
    }

    [Fact]
    public async Task PriceHistorySummary_Returns_Empty_Summary_For_No_Candles()
    {
        var marketData = new StubMarketData
        {
            CandleSet = new MarketDataCandleSet(false, []),
        };
        var catalog = new StubCatalog();
        catalog.Instruments.Add(Instrument("inst-aapl", "AAPL"));
        var tool = new GetPriceHistorySummaryTool(marketData, catalog);

        var result = await tool.ExecuteAsync(
            Args("""{"instrumentId":"inst-aapl","interval":"1d","from":"2026-01-01T00:00:00Z","to":"2026-01-02T00:00:00Z"}"""),
            Context(), TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        using var payload = JsonDocument.Parse(result.PayloadJson);
        payload.RootElement.GetProperty("candleCount").GetInt32().ShouldBe(0);
    }
}
