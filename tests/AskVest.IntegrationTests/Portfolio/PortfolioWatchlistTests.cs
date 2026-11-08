using System.Net;
using System.Text.Json;
using AskVest.IntegrationTests.Catalog;
using AskVest.IntegrationTests.Compliance;
using AskVest.IntegrationTests.Identity;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Catalog.Import.Data;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Portfolio;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class PortfolioWatchlistTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const int InstrumentCount = 65;

    private AuthApiFactory _factory = null!;
    private string _suffix = null!;
    private List<string> _instrumentIds = null!;
    private string _delistInstrumentId = null!;
    private string _probeInstrumentId = null!;

    public async ValueTask InitializeAsync()
    {
        _suffix = CatalogImportTestData.Suffix();
        _instrumentIds = Enumerable.Range(0, InstrumentCount)
            .Select(index => $"wl-{index:D2}-{_suffix}")
            .ToList();
        _delistInstrumentId = $"wl-del-{_suffix}";
        _probeInstrumentId = $"wl-probe-{_suffix}";

        var settings = new Dictionary<string, string?>(ComplianceTestUsers.Settings())
        {
            ["MarketData:Enabled"] = "true",
            ["MarketData:Provider"] = "fake",
            ["MarketData:PollIntervalSeconds"] = "3600",
            ["MarketData:Fake:Scenario"] = "normal",
            ["MarketData:Fake:Session"] = "regular",
            ["MarketData:Fake:DelayMinutes"] = "15",
        };
        _factory = ComplianceTestUsers.CreateFactory(fixture, settings);
        _factory.CreateClient();

        var instruments = _instrumentIds
            .Select((id, index) => CatalogImportTestData.Instrument(id) with
            {
                Symbol = $"WL{index:D2}",
                Name = $"Watchlist Instrument {index}",
            })
            .Append(CatalogImportTestData.Instrument(_delistInstrumentId) with
            {
                Symbol = "WLDL",
                Name = "Watchlist Delisted Instrument",
            })
            .Append(CatalogImportTestData.Instrument(_probeInstrumentId) with
            {
                Symbol = "WLPR",
                Name = "Watchlist Probe Instrument",
            })
            .Concat(Enumerable.Range(0, 3).Select(round => CatalogImportTestData.Instrument($"wl-extra-{round}-{_suffix}") with
            {
                Symbol = $"WX{round}",
                Name = $"Watchlist Extra {round}",
            }))
            .ToList();
        var imported = await CatalogImportTestData.ImportFolderAsync(
            fixture, [], [], instruments, TestContext.Current.CancellationToken);
        imported.IsSuccess.ShouldBeTrue(imported.Error?.Message);

        await using var scope = _factory.Services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<AskVest.Modules.Catalog.Contracts.ICatalog>();
        var found = await catalog.FindInstrumentAsync(_instrumentIds[0], TestContext.Current.CancellationToken);
        found.ShouldNotBeNull($"instrument {_instrumentIds[0]} missing after import");
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient Client() => _factory.CreateClient();

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
        => await ComplianceTestUsers.DataAsync(response);

    private static async Task<string> UserAsync(AuthApiFactory factory)
        => await ComplianceTestUsers.CreateVerifiedUserAsync(factory);

    private static HttpRequestMessage PutWatchlist(string instrumentId, string token)
        => new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/watchlist/{instrumentId}", UriKind.Relative)).Auth(token);

    private static HttpRequestMessage DeleteWatchlist(string instrumentId, string token)
        => new HttpRequestMessage(HttpMethod.Delete, new Uri($"/api/v1/watchlist/{instrumentId}", UriKind.Relative)).Auth(token);

    private static async Task<JsonElement> ListWatchlistAsync(HttpClient client, string token)
    {
        using var response = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/watchlist").Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await DataAsync(response);
    }

    private static async Task<int> WatchlistCountAsync(HttpClient client, string token)
        => (await ListWatchlistAsync(client, token)).GetProperty("items").GetArrayLength();

    private static async Task SeedPricesAsync(HttpClient client, string token)
    {
        using var probe = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/diagnostics/marketdata-scenario", new { scenario = "normal" })
                .Auth(token),
            TestContext.Current.CancellationToken);
        probe.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Watchlist_Mutations_Require_Authentication()
    {
        var client = Client();
        using var put = await client.SendAsync(
            PutWatchlist(_instrumentIds[0], "not-a-token"), TestContext.Current.CancellationToken);
        put.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var delete = await client.SendAsync(
            DeleteWatchlist(_instrumentIds[0], "not-a-token"), TestContext.Current.CancellationToken);
        delete.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Watchlist_Add_List_Remove_Is_Idempotent_And_Owned()
    {
        var client = Client();
        var tokenA = await UserAsync(_factory);
        var tokenB = await UserAsync(_factory);

        foreach (var id in _instrumentIds.Take(3))
        {
            using var add = await client.SendAsync(
                PutWatchlist(id, tokenA), TestContext.Current.CancellationToken);
            add.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Adding again is a harmless success and does not duplicate.
        using var again = await client.SendAsync(
            PutWatchlist(_instrumentIds[0], tokenA), TestContext.Current.CancellationToken);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await WatchlistCountAsync(client, tokenA)).ShouldBe(3);

        await SeedPricesAsync(client, tokenA);
        var list = await ListWatchlistAsync(client, tokenA);
        var items = list.GetProperty("items");
        items.GetArrayLength().ShouldBe(3);
        var ids = items.EnumerateArray().Select(item => item.GetProperty("instrumentId").GetString()).ToList();
        ids.ShouldBe(_instrumentIds.Take(3).Select(id => (string?)id).ToList());
        foreach (var item in items.EnumerateArray())
        {
            item.GetProperty("status").GetString().ShouldBe("active");
            item.GetProperty("label").GetString().ShouldNotBeNull();
            item.GetProperty("usableForExecution").GetBoolean().ShouldBeTrue();
        }

        // Another user sees an empty list.
        (await WatchlistCountAsync(client, tokenB)).ShouldBe(0);

        using var remove = await client.SendAsync(
            DeleteWatchlist(_instrumentIds[1], tokenA), TestContext.Current.CancellationToken);
        remove.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var removeMissing = await client.SendAsync(
            DeleteWatchlist(_instrumentIds[1], tokenA), TestContext.Current.CancellationToken);
        removeMissing.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await WatchlistCountAsync(client, tokenA)).ShouldBe(2);
    }

    [Fact]
    public async Task Watchlist_Unknown_And_Delisted_Instruments_Are_Not_Found()
    {
        var client = Client();
        var token = await UserAsync(_factory);

        using var missing = await client.SendAsync(
            PutWatchlist($"instr-missing-{_suffix}", token), TestContext.Current.CancellationToken);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        ComplianceTestUsers.ErrorCode(await ComplianceTestUsers.EnvelopeAsync(missing))
            .ShouldBe("resource.not_found");

        // Add while active, then delist through a reviewed re-import.
        using var add = await client.SendAsync(
            PutWatchlist(_delistInstrumentId, token), TestContext.Current.CancellationToken);
        add.StatusCode.ShouldBe(HttpStatusCode.OK);

        var delisted = CatalogImportTestData.Instrument(_delistInstrumentId) with
        {
            Symbol = "WLDL",
            Name = "Watchlist Delisted Instrument",
            Status = "delisted",
        };
        var imported = await CatalogImportTestData.ImportFolderAsync(
            fixture, [], [], [delisted], TestContext.Current.CancellationToken);
        imported.IsSuccess.ShouldBeTrue(imported.Error?.Message);

        using var addDelisted = await client.SendAsync(
            PutWatchlist(_delistInstrumentId, token), TestContext.Current.CancellationToken);
        // The item already exists, so the add is a harmless success even though
        // the instrument is now delisted; a fresh user gets not_found instead.
        addDelisted.StatusCode.ShouldBe(HttpStatusCode.OK);

        var other = await UserAsync(_factory);
        using var addDelistedFresh = await client.SendAsync(
            PutWatchlist(_delistInstrumentId, other), TestContext.Current.CancellationToken);
        addDelistedFresh.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The delisted item still lists, with label unavailable.
        var items = (await ListWatchlistAsync(client, token)).GetProperty("items");
        var entry = items.EnumerateArray()
            .Single(item => item.GetProperty("instrumentId").GetString() == _delistInstrumentId);
        entry.GetProperty("status").GetString().ShouldBe("delisted");
        entry.GetProperty("label").GetString().ShouldBe("unavailable");
        entry.GetProperty("usableForExecution").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Watchlist_Cap_Holds_Under_Sixty_Parallel_Adds()
    {
        var client = Client();
        for (var round = 0; round < 3; round++)
        {
            var token = await UserAsync(_factory);
            using var probe = await client.SendAsync(
                PutWatchlist(_probeInstrumentId, token), TestContext.Current.CancellationToken);
            probe.StatusCode.ShouldBe(HttpStatusCode.OK,
                await probe.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            using var probeDelete = await client.SendAsync(
                DeleteWatchlist(_probeInstrumentId, token), TestContext.Current.CancellationToken);
            probeDelete.StatusCode.ShouldBe(HttpStatusCode.OK);
            var tasks = _instrumentIds.Select(id => client.SendAsync(
                PutWatchlist(id, token), TestContext.Current.CancellationToken)).ToList();
            var responses = await Task.WhenAll(tasks);
            var histogram = responses
                .GroupBy(response => (int)response.StatusCode)
                .ToDictionary(group => group.Key, group => group.Count());
            responses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBe(50,
                $"status histogram: {string.Join(", ", histogram.Select(pair => $"{pair.Key}:{pair.Value}"))}");
            foreach (var response in responses)
            {
                response.Dispose();
            }

            (await WatchlistCountAsync(client, token)).ShouldBe(50);

            // The 51st add of a new instrument answers portfolio.watchlist_full.
            var extra = $"wl-extra-{round}-{_suffix}";
            using var full = await client.SendAsync(
                PutWatchlist(extra, token), TestContext.Current.CancellationToken);
            full.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            ComplianceTestUsers.ErrorCode(await ComplianceTestUsers.EnvelopeAsync(full))
                .ShouldBe("portfolio.watchlist_full");
        }
    }
}
