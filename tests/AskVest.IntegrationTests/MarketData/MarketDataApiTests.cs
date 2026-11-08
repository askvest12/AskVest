using System.Net;
using System.Text.Json;
using AskVest.IntegrationTests.Catalog;
using AskVest.IntegrationTests.Compliance;
using AskVest.IntegrationTests.Identity;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.MarketData.Contracts;
using AskVest.Modules.MarketData.Providers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.MarketData;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class MarketDataApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private AuthApiFactory _factory = null!;
    private readonly List<string> _instrumentIds = [];

    public async ValueTask InitializeAsync()
    {
        var settings = new Dictionary<string, string?>(ComplianceTestUsers.Settings())
        {
            ["MarketData:PollIntervalSeconds"] = "3600",
            ["MarketData:BatchSize"] = "50",
            ["MarketData:LiveThresholdSeconds"] = "30",
            ["MarketData:StaleThresholdSeconds"] = "300",
        };
        _factory = ComplianceTestUsers.CreateFactory(fixture, settings);
        _factory.CreateClient();

        var suffix = CatalogImportTestData.Suffix();
        var instruments = Enumerable.Range(0, 3)
            .Select(i => CatalogImportTestData.Instrument($"instr-px-{i}-{suffix}", null) with
            {
                Symbol = $"PX{i}{suffix[..4]}",
                ListingExchange = "NMS",
            })
            .ToList();
        instruments.Add(CatalogImportTestData.Instrument($"instr-px-d-{suffix}", null) with
        {
            Symbol = $"PXD{suffix[..4]}",
            ListingExchange = "NMS",
            Status = "delisted",
        });
        var result = await CatalogImportTestData.ImportFolderAsync(
            fixture, [], [], instruments, TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        _instrumentIds.AddRange(instruments.Select(i => i.Id));

        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        await ProbeAsync(token, "normal");
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient Client() => _factory.CreateClient();

    private FakeMarketDataProvider Fake() => _factory.Services.GetRequiredService<FakeMarketDataProvider>();

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
        => await ComplianceTestUsers.DataAsync(response);

    private async Task ProbeAsync(string token, string scenario)
    {
        var client = Client();
        using var response = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/diagnostics/marketdata-scenario", new { scenario })
                .Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<string> TokenAsync()
        => await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);

    [Fact]
    public async Task Prices_Require_Authentication()
    {
        using var response = await Client().GetAsync(
            new Uri($"/api/v1/marketdata/prices?instrumentIds={_instrumentIds[0]}", UriKind.Relative),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Prices_Keep_Request_Order_And_Serialize_Amounts_As_Strings()
    {
        var token = await TokenAsync();
        var ordered = new[] { _instrumentIds[2], _instrumentIds[0], _instrumentIds[1] };
        var quotesBefore = Fake().QuotesCallCount;
        using var response = await Client().SendAsync(
            ComplianceTestUsers.Get($"/api/v1/marketdata/prices?instrumentIds={string.Join(',', ordered)}")
                .Auth(token),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var data = await DataAsync(response);
        var items = data.EnumerateArray().ToList();
        items.Count.ShouldBe(3);
        Fake().QuotesCallCount.ShouldBe(quotesBefore);
        items.Select(i => i.GetProperty("instrumentId").GetString()).ShouldBe(ordered);
        foreach (var item in items)
        {
            item.GetProperty("price").ValueKind.ShouldBe(JsonValueKind.String);
            item.GetProperty("label").GetString().ShouldBe("live");
            item.GetProperty("usableForExecution").GetBoolean().ShouldBeTrue();
            item.GetProperty("session").GetString().ShouldBe("regular");
            item.GetProperty("source").GetString().ShouldBe("AskVest Demo Feed");
            item.GetProperty("currency").GetString().ShouldBe("USD");
        }
    }

    [Fact]
    public async Task Prices_Labels_Follow_Scenario_And_Usability_Rules()
    {
        var token = await TokenAsync();
        var id = _instrumentIds[0];

        async Task<JsonElement> PriceItemAsync()
        {
            using var response = await Client().SendAsync(
                ComplianceTestUsers.Get($"/api/v1/marketdata/prices?instrumentIds={id}").Auth(token),
                TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var data = await DataAsync(response);
            return data.EnumerateArray().Single().Clone();
        }

        (await PriceItemAsync()).GetProperty("label").GetString().ShouldBe("live");

        await ProbeAsync(token, "delayed");
        var delayed = await PriceItemAsync();
        delayed.GetProperty("label").GetString().ShouldBe("delayed");
        delayed.GetProperty("usableForExecution").GetBoolean().ShouldBeTrue();

        await ProbeAsync(token, "halted");
        var halted = await PriceItemAsync();
        halted.GetProperty("label").GetString().ShouldBe("halted");
        halted.GetProperty("usableForExecution").GetBoolean().ShouldBeFalse();

        await ProbeAsync(token, "closed");
        var closed = await PriceItemAsync();
        closed.GetProperty("label").GetString().ShouldBe("closed");
        closed.GetProperty("usableForExecution").GetBoolean().ShouldBeFalse();
        closed.GetProperty("previousClose").ValueKind.ShouldBe(JsonValueKind.String);

        await ProbeAsync(token, "normal");
        await ProbeAsync(token, "outage");
        await BackdateAsync(id, TimeSpan.FromHours(1));
        var stale = await PriceItemAsync();
        stale.GetProperty("label").GetString().ShouldBe("stale");
        stale.GetProperty("usableForExecution").GetBoolean().ShouldBeFalse();
        stale.GetProperty("price").ValueKind.ShouldBe(JsonValueKind.String);

        await ProbeAsync(token, "normal");
        (await PriceItemAsync()).GetProperty("label").GetString().ShouldBe("live");
    }

    [Fact]
    public async Task Prices_Return_Unavailable_For_Unknown_Delisted_And_Unmapped()
    {
        var token = await TokenAsync();
        var delistedId = _instrumentIds[3];
        var unknownId = $"instr-missing-{CatalogImportTestData.Suffix()}";
        var query = string.Join(',', new[] { unknownId, delistedId, _instrumentIds[0] });

        using var response = await Client().SendAsync(
            ComplianceTestUsers.Get($"/api/v1/marketdata/prices?instrumentIds={query}").Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var items = (await DataAsync(response)).EnumerateArray().ToList();
        items.Count.ShouldBe(3);
        items[0].GetProperty("label").GetString().ShouldBe("unavailable");
        items[0].GetProperty("price").ValueKind.ShouldBe(JsonValueKind.Null);
        items[0].GetProperty("usableForExecution").GetBoolean().ShouldBeFalse();
        items[1].GetProperty("label").GetString().ShouldBe("unavailable");
        items[2].GetProperty("label").GetString().ShouldBe("live");
    }

    [Fact]
    public async Task Prices_Reject_Empty_And_Oversized_Batches()
    {
        var token = await TokenAsync();
        var client = Client();

        using var empty = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/marketdata/prices?instrumentIds=").Auth(token),
            TestContext.Current.CancellationToken);
        empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var emptyEnvelope = await ComplianceTestUsers.EnvelopeAsync(empty);
        ComplianceTestUsers.ErrorCode(emptyEnvelope).ShouldBe("validation.failed");

        var oversized = string.Join(',', Enumerable.Range(0, 51).Select(i => $"instr-x{i}"));
        using var big = await client.SendAsync(
            ComplianceTestUsers.Get($"/api/v1/marketdata/prices?instrumentIds={oversized}").Auth(token),
            TestContext.Current.CancellationToken);
        big.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Contract_Returns_Same_Values_As_Endpoint()
    {
        var token = await TokenAsync();
        var id = _instrumentIds[0];
        using var response = await Client().SendAsync(
            ComplianceTestUsers.Get($"/api/v1/marketdata/prices?instrumentIds={id}").Auth(token),
            TestContext.Current.CancellationToken);
        var endpointItem = (await DataAsync(response)).EnumerateArray().Single();

        await using var scope = _factory.Services.CreateAsyncScope();
        var marketData = scope.ServiceProvider.GetRequiredService<IMarketData>();
        var contractItem = await marketData.GetLatestPriceAsync(id, TestContext.Current.CancellationToken);

        contractItem.InstrumentId.ShouldBe(endpointItem.GetProperty("instrumentId").GetString());
        contractItem.Price.ShouldBe(decimal.Parse(
            endpointItem.GetProperty("price").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        contractItem.Label.ShouldBe(endpointItem.GetProperty("label").GetString());
        contractItem.Session.ShouldBe(endpointItem.GetProperty("session").GetString());
        contractItem.UsableForExecution.ShouldBe(endpointItem.GetProperty("usableForExecution").GetBoolean());
        contractItem.Source.ShouldBe(endpointItem.GetProperty("source").GetString());

        var list = await marketData.GetLatestPricesAsync(
            [_instrumentIds[0], "instr-does-not-exist"], TestContext.Current.CancellationToken);
        list.Count.ShouldBe(2);
        list[1].Label.ShouldBe("unavailable");
        list[1].UsableForExecution.ShouldBeFalse();
    }

    private async Task BackdateAsync(string instrumentId, TimeSpan age)
    {
        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE marketdata.latest_prices SET as_of = now() - @age WHERE subject_id = @id",
            connection);
        command.Parameters.AddWithValue("age", age);
        command.Parameters.AddWithValue("id", instrumentId);
        (await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }
}
