using System.Net;
using System.Text.Json;
using AskVest.IntegrationTests.Catalog;
using AskVest.IntegrationTests.Compliance;
using AskVest.IntegrationTests.Identity;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.MarketData.Providers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.MarketData;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class MarketDataCandlesApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private AuthApiFactory _factory = null!;
    private string _instrumentId = "";

    public async ValueTask InitializeAsync()
    {
        var settings = new Dictionary<string, string?>(ComplianceTestUsers.Settings())
        {
            ["MarketData:PollIntervalSeconds"] = "3600",
            ["MarketData:CandleIntervals:0:Interval"] = "15m",
            ["MarketData:CandleIntervals:0:MaxRangeDays"] = "7",
            ["MarketData:CandleIntervals:0:MaxCandles"] = "500",
            ["MarketData:CandleIntervals:0:OpenCacheSeconds"] = "3",
            ["MarketData:CandleIntervals:1:Interval"] = "1h",
            ["MarketData:CandleIntervals:1:MaxRangeDays"] = "60",
            ["MarketData:CandleIntervals:1:MaxCandles"] = "500",
            ["MarketData:CandleIntervals:1:OpenCacheSeconds"] = "3",
            ["MarketData:CandleIntervals:2:Interval"] = "1d",
            ["MarketData:CandleIntervals:2:MaxRangeDays"] = "1826",
            ["MarketData:CandleIntervals:2:MaxCandles"] = "500",
            ["MarketData:CandleIntervals:2:OpenCacheSeconds"] = "3",
        };
        _factory = ComplianceTestUsers.CreateFactory(fixture, settings);
        _factory.CreateClient();

        var suffix = CatalogImportTestData.Suffix();
        var instrument = CatalogImportTestData.Instrument($"instr-cc-{suffix}", null) with
        {
            Symbol = $"CC{suffix[..4]}",
            ListingExchange = "NMS",
        };
        var result = await CatalogImportTestData.ImportFolderAsync(
            fixture, [], [], [instrument], TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        _instrumentId = instrument.Id;
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient Client() => _factory.CreateClient();

    private FakeMarketDataProvider Fake() => _factory.Services.GetRequiredService<FakeMarketDataProvider>();

    private string CandlesUrl(string interval, DateTimeOffset from, DateTimeOffset to, string? id = null)
        => $"/api/v1/marketdata/instruments/{id ?? _instrumentId}/candles?interval={interval}&from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
        => await ComplianceTestUsers.DataAsync(response);

    private static JsonElement[] Items(JsonElement data)
        => data.GetProperty("candles").EnumerateArray().Select(c => c.Clone()).ToArray();

    [Fact]
    public async Task Candles_Require_Authentication()
    {
        var now = TimeProvider.System.GetUtcNow();
        using var response = await Client().GetAsync(
            new Uri(CandlesUrl("1d", now.AddDays(-3), now), UriKind.Relative),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Cache_Miss_Fills_And_Second_Request_Uses_Cache()
    {
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        var now = TimeProvider.System.GetUtcNow();
        var from = now.AddDays(-3);
        var url = CandlesUrl("1d", from, now);
        var client = Client();
        var callsBefore = Fake().CandlesCallCount;

        using var first = await client.SendAsync(
            ComplianceTestUsers.Get(url).Auth(token), TestContext.Current.CancellationToken);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var data = await DataAsync(first);
        data.GetProperty("stale").GetBoolean().ShouldBeFalse();
        data.GetProperty("source").GetString().ShouldBe("AskVest Demo Feed");
        var items = Items(data);
        items.Length.ShouldBeGreaterThanOrEqualTo(3);
        var openTimes = items.Select(i => i.GetProperty("openTime").GetDateTimeOffset()).ToList();
        openTimes.ShouldBe(openTimes.OrderBy(t => t).ToList());
        foreach (var item in items)
        {
            item.GetProperty("open").ValueKind.ShouldBe(JsonValueKind.String);
            item.GetProperty("high").ValueKind.ShouldBe(JsonValueKind.String);
            item.GetProperty("low").ValueKind.ShouldBe(JsonValueKind.String);
            item.GetProperty("close").ValueKind.ShouldBe(JsonValueKind.String);
            item.GetProperty("volume").ValueKind.ShouldBe(JsonValueKind.String);
            item.GetProperty("adjusted").GetBoolean().ShouldBeTrue();
        }

        items[^1].GetProperty("partial").GetBoolean().ShouldBeTrue();
        items.Take(items.Length - 1).ShouldAllBe(i => !i.GetProperty("partial").GetBoolean());

        Fake().CandlesCallCount.ShouldBe(callsBefore + 1);

        using var second = await client.SendAsync(
            ComplianceTestUsers.Get(url).Auth(token), TestContext.Current.CancellationToken);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        Fake().CandlesCallCount.ShouldBe(callsBefore + 1);
    }

    [Fact]
    public async Task Open_Candle_Refreshes_After_Cache_Lifetime()
    {
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        var now = TimeProvider.System.GetUtcNow();
        var url = CandlesUrl("15m", now.AddHours(-2), now);
        var client = Client();

        using var first = await client.SendAsync(
            ComplianceTestUsers.Get(url).Auth(token), TestContext.Current.CancellationToken);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var callsAfterFill = Fake().CandlesCallCount;

        await Task.Delay(3500, TestContext.Current.CancellationToken);
        using var second = await client.SendAsync(
            ComplianceTestUsers.Get(url).Auth(token), TestContext.Current.CancellationToken);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        Fake().CandlesCallCount.ShouldBe(callsAfterFill + 1);
    }

    [Fact]
    public async Task Twenty_Parallel_Identical_Requests_Cause_One_Provider_Call()
    {
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        var suffix = CatalogImportTestData.Suffix();
        var fresh = CatalogImportTestData.Instrument($"instr-ccp-{suffix}", null) with
        {
            Symbol = $"CCP{suffix[..4]}",
            ListingExchange = "NMS",
        };
        var import = await CatalogImportTestData.ImportFolderAsync(
            fixture, [], [], [fresh], TestContext.Current.CancellationToken);
        import.IsSuccess.ShouldBeTrue(import.Error?.Message);

        var now = TimeProvider.System.GetUtcNow();
        var url = CandlesUrl("1h", now.AddDays(-1), now, fresh.Id);
        var callsBefore = Fake().CandlesCallCount;
        var client = Client();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            client.SendAsync(ComplianceTestUsers.Get(url).Auth(token), TestContext.Current.CancellationToken)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        foreach (var response in responses)
        {
            response.Dispose();
        }

        Fake().CandlesCallCount.ShouldBe(callsBefore + 1);
    }

    [Fact]
    public async Task Invalid_Interval_Range_And_Size_Are_Rejected()
    {
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        var client = Client();
        var now = TimeProvider.System.GetUtcNow();

        using var badInterval = await client.SendAsync(
            ComplianceTestUsers.Get(CandlesUrl("5m", now.AddDays(-1), now)).Auth(token),
            TestContext.Current.CancellationToken);
        badInterval.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var badIntervalEnvelope = await ComplianceTestUsers.EnvelopeAsync(badInterval);
        ComplianceTestUsers.ErrorCode(badIntervalEnvelope).ShouldBe("validation.failed");

        using var reversed = await client.SendAsync(
            ComplianceTestUsers.Get(CandlesUrl("1d", now, now.AddDays(-1))).Auth(token),
            TestContext.Current.CancellationToken);
        reversed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var tooWide = await client.SendAsync(
            ComplianceTestUsers.Get(CandlesUrl("15m", now.AddDays(-30), now)).Auth(token),
            TestContext.Current.CancellationToken);
        tooWide.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var tooMany = await client.SendAsync(
            ComplianceTestUsers.Get(CandlesUrl("15m", now.AddDays(-6), now)).Auth(token),
            TestContext.Current.CancellationToken);
        tooMany.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unknown_And_Inactive_Instruments_Return_Not_Found()
    {
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        var client = Client();
        var now = TimeProvider.System.GetUtcNow();
        var missing = $"instr-missing-{CatalogImportTestData.Suffix()}";

        using var unknown = await client.SendAsync(
            ComplianceTestUsers.Get(CandlesUrl("1d", now.AddDays(-1), now, missing)).Auth(token),
            TestContext.Current.CancellationToken);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var unknownEnvelope = await ComplianceTestUsers.EnvelopeAsync(unknown);
        ComplianceTestUsers.ErrorCode(unknownEnvelope).ShouldBe("resource.not_found");

        var suffix = CatalogImportTestData.Suffix();
        var delisted = CatalogImportTestData.Instrument($"instr-ccd-{suffix}", null) with
        {
            Symbol = $"CCD{suffix[..4]}",
            ListingExchange = "NMS",
            Status = "delisted",
        };
        var import = await CatalogImportTestData.ImportFolderAsync(
            fixture, [], [], [delisted], TestContext.Current.CancellationToken);
        import.IsSuccess.ShouldBeTrue(import.Error?.Message);

        using var inactive = await client.SendAsync(
            ComplianceTestUsers.Get(CandlesUrl("1d", now.AddDays(-1), now, delisted.Id)).Auth(token),
            TestContext.Current.CancellationToken);
        inactive.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Provider_Outage_Serves_Cache_With_Stale_Or_Fails_When_Empty()
    {
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        var now = TimeProvider.System.GetUtcNow();
        var url = CandlesUrl("1d", now.AddDays(-3), now);
        var client = Client();

        using var fill = await client.SendAsync(
            ComplianceTestUsers.Get(url).Auth(token), TestContext.Current.CancellationToken);
        fill.StatusCode.ShouldBe(HttpStatusCode.OK);

        await Task.Delay(3500, TestContext.Current.CancellationToken);
        Fake().SetScenario("outage");
        using var stale = await client.SendAsync(
            ComplianceTestUsers.Get(url).Auth(token), TestContext.Current.CancellationToken);
        stale.StatusCode.ShouldBe(HttpStatusCode.OK);
        var staleData = await DataAsync(stale);
        staleData.GetProperty("stale").GetBoolean().ShouldBeTrue();
        Items(staleData).Length.ShouldBeGreaterThan(0);

        Fake().SetScenario("normal");

        var suffix = CatalogImportTestData.Suffix();
        var fresh = CatalogImportTestData.Instrument($"instr-cco-{suffix}", null) with
        {
            Symbol = $"CCO{suffix[..4]}",
            ListingExchange = "NMS",
        };
        var import = await CatalogImportTestData.ImportFolderAsync(
            fixture, [], [], [fresh], TestContext.Current.CancellationToken);
        import.IsSuccess.ShouldBeTrue(import.Error?.Message);

        Fake().SetScenario("outage");
        using var empty = await client.SendAsync(
            ComplianceTestUsers.Get(CandlesUrl("1d", now.AddDays(-1), now, fresh.Id)).Auth(token),
            TestContext.Current.CancellationToken);
        empty.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var envelope = await ComplianceTestUsers.EnvelopeAsync(empty);
        ComplianceTestUsers.ErrorCode(envelope).ShouldBe("marketdata.unavailable");
        Fake().SetScenario("normal");
    }
}
