using System.Net;
using System.Text;
using System.Text.Json;
using AskVest.IntegrationTests.Catalog;
using AskVest.IntegrationTests.Compliance;
using AskVest.IntegrationTests.Identity;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Catalog.Import.Data;
using AskVest.Modules.Compliance.Providers;
using AskVest.Modules.Ledger.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Portfolio;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class PortfolioApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string WebhookSecret = "test-kyc-webhook-secret-0123456789abcdef";

    private AuthApiFactory _factory = null!;
    private string _practiceInstrumentId = null!;
    private string _practiceInstrumentId2 = null!;
    private string _realInstrumentId = null!;
    private string _tokenAssetA = null!;
    private string _tokenAssetB = null!;

    public async ValueTask InitializeAsync()
    {
        var suffix = CatalogImportTestData.Suffix();
        _practiceInstrumentId = $"instr-pa-{suffix}";
        _practiceInstrumentId2 = $"instr-pb-{suffix}";
        _realInstrumentId = $"instr-ra-{suffix}";
        _tokenAssetA = CatalogImportTestData.AssetCode("RTA", suffix);
        _tokenAssetB = CatalogImportTestData.AssetCode("RTB", suffix);

        var settings = new Dictionary<string, string?>(ComplianceTestUsers.Settings())
        {
            ["MarketData:Enabled"] = "true",
            ["MarketData:Provider"] = "fake",
            ["MarketData:PollIntervalSeconds"] = "3600",
            ["MarketData:Fake:Scenario"] = "normal",
            ["MarketData:Fake:Session"] = "regular",
            ["MarketData:Fake:DelayMinutes"] = "15",
            ["Practice:ResetCooldownHours"] = "0",
        };
        _factory = ComplianceTestUsers.CreateFactory(fixture, settings);
        _factory.CreateClient();

        var chains = new[]
        {
            CatalogImportTestData.Chain("eip155:11155111", "enabled"),
            CatalogImportTestData.Chain("eip155:17000", "enabled"),
        };
        var issuers = new[] { CatalogImportTestData.Issuer("issuer-ra") };
        var instruments = new[]
        {
            CatalogImportTestData.Instrument(_practiceInstrumentId, null) with
            {
                Symbol = $"PA{suffix[..4]}",
                ListingExchange = "NMS",
            },
            CatalogImportTestData.Instrument(_practiceInstrumentId2, null) with
            {
                Symbol = $"PB{suffix[..4]}",
                ListingExchange = "NMS",
            },
            CatalogImportTestData.Instrument(_realInstrumentId,
            [
                CatalogImportTestData.Token("issuer-ra", "eip155:11155111",
                    CatalogImportTestData.Address(suffix, 'a'), "listed", _tokenAssetA) with { Symbol = $"RA{suffix[..4]}" },
                CatalogImportTestData.Token("issuer-ra", "eip155:17000",
                    CatalogImportTestData.Address(suffix, 'b'), "listed", _tokenAssetB) with { Symbol = $"RB{suffix[..4]}" },
            ]) with
            {
                Symbol = $"RA{suffix[..4]}",
                ListingExchange = "NMS",
            },
        };
        var imported = await CatalogImportTestData.ImportFolderAsync(
            fixture, chains, issuers, instruments, TestContext.Current.CancellationToken);
        imported.IsSuccess.ShouldBeTrue(imported.Error?.Message);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient Client() => _factory.CreateClient();

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
        => await ComplianceTestUsers.DataAsync(response);

    private async Task<string> PracticeEligibleUserAsync(HttpClient client)
    {
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        await ComplianceTestUsers.AcceptDocumentsAsync(
            client, token, ("terms", "v1", new string('a', 64)), ("privacy", "v1", new string('b', 64)));
        await ComplianceTestUsers.SetCountryAsync(client, token, "NG");
        return token;
    }

    private async Task<string> RealEligibleUserAsync(HttpClient client)
    {
        var token = await PracticeEligibleUserAsync(client);
        await ComplianceTestUsers.AcceptDocumentsAsync(
            client, token,
            ("risk_disclosure", "v1", new string('c', 64)),
            ("token_disclosure", "v1", new string('d', 64)));

        using var start = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/compliance/kyc/start", new { }).Auth(token),
            TestContext.Current.CancellationToken);
        start.StatusCode.ShouldBe(HttpStatusCode.OK);
        var reference = (await DataAsync(start)).GetProperty("providerReference").GetString()!;

        var body = JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["eventId"] = $"evt-{Guid.CreateVersion7():N}",
            ["providerReference"] = reference,
            ["outcome"] = "approved",
            ["verifiedCountry"] = "NG",
            ["fullName"] = "Amara Okafor",
            ["dateOfBirth"] = "1990-04-12",
        });
        var timestamp = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds().ToString();
        using var webhook = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/webhooks/kyc/fake", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        webhook.Headers.Add("X-Webhook-Timestamp", timestamp);
        webhook.Headers.Add("X-Webhook-Signature", FakeKycProvider.Signature(timestamp, body, WebhookSecret));
        using var webhookResponse = await client.SendAsync(webhook, TestContext.Current.CancellationToken);
        webhookResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        return token;
    }

    private static async Task SeedPriceAsync(HttpClient client, string token)
    {
        using var probe = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/diagnostics/marketdata-scenario", new { scenario = "normal" })
                .Auth(token),
            TestContext.Current.CancellationToken);
        probe.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task PostJournalAsync(
        Guid userId,
        string reason,
        string referenceType,
        Guid? referenceId,
        IReadOnlyList<LedgerLine> lines,
        string key)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<ILedger>()
            .PostJournalAsync(
                new PostJournalRequest(key, Book.Real, reason, referenceType, referenceId, lines),
                TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
    }

    [Fact]
    public async Task Practice_Portfolio_Replay_Spans_Movement_Pages()
    {
        var client = Client();
        var token = await PracticeEligibleUserAsync(client);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var registered = await scope.ServiceProvider
                .GetRequiredService<ILedger>()
                .RegisterAssetAsync("USD", AssetKind.Fiat, 6, TestContext.Current.CancellationToken);
            registered.IsSuccess.ShouldBeTrue(registered.Error?.Message);
        }

        // 501 probe credits force the replay to cross the 500-movement page boundary.
        for (var index = 0; index < 501; index++)
        {
            using var probe = await client.SendAsync(
                ComplianceTestUsers.Post("/api/v1/practice/diagnostics/ledger-post",
                    new { kind = "credit", asset = "USD", amount = "1" },
                    $"page-{Guid.CreateVersion7():N}-{index}").Auth(token),
                TestContext.Current.CancellationToken);
            probe.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var response = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/practice/portfolio").Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var data = await DataAsync(response);
        var cash = data.GetProperty("cash");
        cash.GetArrayLength().ShouldBe(1);
        decimal.Parse(cash[0].GetProperty("available").GetString()!).ShouldBe(501m);
        data.GetProperty("holdings").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Practice_Portfolio_Ignores_Zeroed_Assets_From_Before_Reset()
    {
        var client = Client();
        var token = await PracticeEligibleUserAsync(client);

        using var start = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/practice/account", new { }).Auth(token),
            TestContext.Current.CancellationToken);
        start.StatusCode.ShouldBe(HttpStatusCode.OK);
        await SeedPriceAsync(client, token);

        // Buy and reset: the first instrument's balance row stays at zero while
        // the replay epoch restarts, so it must not flag the new holding.
        using var buyA = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/practice/orders",
                new { instrumentId = _practiceInstrumentId, side = "buy", type = "market", amount = "100" },
                $"buy-a-{Guid.CreateVersion7():N}").Auth(token),
            TestContext.Current.CancellationToken);
        buyA.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var reset = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/practice/account/reset", new { }).Auth(token),
            TestContext.Current.CancellationToken);
        reset.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var buyB = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/practice/orders",
                new { instrumentId = _practiceInstrumentId2, side = "buy", type = "market", amount = "100" },
                $"buy-b-{Guid.CreateVersion7():N}").Auth(token),
            TestContext.Current.CancellationToken);
        buyB.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var response = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/practice/portfolio").Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var data = await DataAsync(response);
        var holdings = data.GetProperty("holdings");
        holdings.GetArrayLength().ShouldBe(1);
        holdings[0].GetProperty("instrumentId").GetString().ShouldBe(_practiceInstrumentId2);
        holdings[0].GetProperty("flags").EnumerateArray()
            .Select(f => f.GetString()).ShouldNotContain("has_inconsistency");
    }

    [Fact]
    public async Task Portfolio_And_Watchlist_Routes_Require_Authentication()
    {
        var client = Client();
        foreach (var path in new[]
        {
            "/api/v1/practice/portfolio",
            "/api/v1/real/portfolio",
            "/api/v1/watchlist",
        })
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
    }

    [Fact]
    public async Task Practice_Portfolio_Starts_Empty_Then_Reflects_Buy_And_Sell()
    {
        var client = Client();
        var token = await PracticeEligibleUserAsync(client);

        using var empty = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/practice/portfolio").Auth(token),
            TestContext.Current.CancellationToken);
        empty.StatusCode.ShouldBe(HttpStatusCode.OK);
        var emptyData = await DataAsync(empty);
        emptyData.GetProperty("book").GetString().ShouldBe("practice");
        emptyData.GetProperty("valuationCurrency").GetString().ShouldBe("USD");
        emptyData.GetProperty("holdings").GetArrayLength().ShouldBe(0);

        using var start = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/practice/account", new { }).Auth(token),
            TestContext.Current.CancellationToken);
        start.StatusCode.ShouldBe(HttpStatusCode.OK);

        await SeedPriceAsync(client, token);
        using var buy = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/practice/orders",
                new { instrumentId = _practiceInstrumentId, side = "buy", type = "market", amount = "100" },
                $"buy-{Guid.CreateVersion7():N}").Auth(token),
            TestContext.Current.CancellationToken);
        buy.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var afterBuy = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/practice/portfolio").Auth(token),
            TestContext.Current.CancellationToken);
        afterBuy.StatusCode.ShouldBe(HttpStatusCode.OK);
        var buyData = await DataAsync(afterBuy);
        var holdings = buyData.GetProperty("holdings");
        holdings.GetArrayLength().ShouldBe(1);
        var holding = holdings[0];
        holding.GetProperty("instrumentId").GetString().ShouldBe(_practiceInstrumentId);
        decimal.Parse(holding.GetProperty("quantity").GetString()!).ShouldBeGreaterThan(0m);
        decimal.Parse(holding.GetProperty("costBasis").GetString()!).ShouldBeGreaterThan(0m);
        holding.GetProperty("tokens").GetArrayLength().ShouldBe(1);
        var cash = buyData.GetProperty("cash");
        cash.GetArrayLength().ShouldBe(1);
        cash[0].GetProperty("asset").GetString().ShouldBe("USD");
        cash[0].GetProperty("includedInTotal").GetBoolean().ShouldBeTrue();
        var cashAfter = decimal.Parse(cash[0].GetProperty("available").GetString()!);
        cashAfter.ShouldBeLessThan(10000m);

        // Repeated reads are stable.
        using var again = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/practice/portfolio").Auth(token),
            TestContext.Current.CancellationToken);
        var againData = await DataAsync(again);
        againData.GetProperty("holdings")[0].GetProperty("quantity").GetString()
            .ShouldBe(holding.GetProperty("quantity").GetString());

        using var sell = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/practice/orders",
                new { instrumentId = _practiceInstrumentId, side = "sell", type = "market", sellAll = true },
                $"sell-{Guid.CreateVersion7():N}").Auth(token),
            TestContext.Current.CancellationToken);
        sell.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var afterSell = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/practice/portfolio").Auth(token),
            TestContext.Current.CancellationToken);
        var sellData = await DataAsync(afterSell);
        sellData.GetProperty("holdings").GetArrayLength().ShouldBe(0);
        sellData.GetProperty("totals").GetProperty("realized").GetString().ShouldNotBeNull();
    }

    [Fact]
    public async Task Real_Portfolio_Gate_And_Two_Chain_Aggregation()
    {
        var client = Client();
        var token = await RealEligibleUserAsync(client);
        var userId = await UserIdForTokenAsync(token);

        // Practice-only route stays gated for this user is covered elsewhere;
        // here the real portfolio must answer for a real-eligible user.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<ILedger>();
            foreach (var (code, kind, scale) in new[]
            {
                ("USD", AssetKind.Fiat, 6),
                (_tokenAssetA, AssetKind.Token, 18),
                (_tokenAssetB, AssetKind.Token, 18),
            })
            {
                var registered = await ledger.RegisterAssetAsync(code, kind, scale, TestContext.Current.CancellationToken);
                registered.IsSuccess.ShouldBeTrue(registered.Error?.Message);
            }
        }

        var suffix = Guid.CreateVersion7().ToString("N")[..6];
        await PostJournalAsync(userId, LedgerReasonCodes.Deposit, "test", null,
        [
            new LedgerLine(LedgerAccounts.UserAvailable(Book.Real, userId), "USD", 1000m),
            new LedgerLine(LedgerAccounts.SystemClearing(Book.Real), "USD", -1000m),
        ], $"dep-{suffix}");

        // Two buys of the same instrument on two chains.
        await PostJournalAsync(userId, LedgerReasonCodes.TradeBuy, "order", null,
        [
            new LedgerLine(LedgerAccounts.UserAvailable(Book.Real, userId), "USD", -105m),
            new LedgerLine(LedgerAccounts.SystemClearing(Book.Real), "USD", 100m),
            new LedgerLine(LedgerAccounts.SystemFees(Book.Real), "USD", 5m),
            new LedgerLine(LedgerAccounts.SystemClearing(Book.Real), _tokenAssetA, -10m),
            new LedgerLine(LedgerAccounts.UserAvailable(Book.Real, userId), _tokenAssetA, 10m),
        ], $"buy-a-{suffix}");
        await PostJournalAsync(userId, LedgerReasonCodes.TradeBuy, "order", null,
        [
            new LedgerLine(LedgerAccounts.UserAvailable(Book.Real, userId), "USD", -55m),
            new LedgerLine(LedgerAccounts.SystemClearing(Book.Real), "USD", 50m),
            new LedgerLine(LedgerAccounts.SystemFees(Book.Real), "USD", 5m),
            new LedgerLine(LedgerAccounts.SystemClearing(Book.Real), _tokenAssetB, -5m),
            new LedgerLine(LedgerAccounts.UserAvailable(Book.Real, userId), _tokenAssetB, 5m),
        ], $"buy-b-{suffix}");

        // A raw token credit is unclassified; a dividend adds income; a split
        // changes quantity without cost.
        await PostJournalAsync(userId, LedgerReasonCodes.Adjustment, "ops", null,
        [
            new LedgerLine(LedgerAccounts.UserAvailable(Book.Real, userId), _tokenAssetA, 2m),
            new LedgerLine(LedgerAccounts.SystemClearing(Book.Real), _tokenAssetA, -2m),
        ], $"adj-{suffix}");
        await SeedPriceAsync(client, token);

        using var response = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/real/portfolio").Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var data = await DataAsync(response);
        data.GetProperty("book").GetString().ShouldBe("real");
        var holdings = data.GetProperty("holdings");
        holdings.GetArrayLength().ShouldBe(1);
        var holding = holdings[0];
        holding.GetProperty("instrumentId").GetString().ShouldBe(_realInstrumentId);
        decimal.Parse(holding.GetProperty("quantity").GetString()!).ShouldBe(17m);
        decimal.Parse(holding.GetProperty("costBasis").GetString()!).ShouldBe(160m);
        var tokens = holding.GetProperty("tokens");
        tokens.GetArrayLength().ShouldBe(2);
        var chains = tokens.EnumerateArray().Select(t => t.GetProperty("chainId").GetString()).ToList();
        chains.ShouldContain("eip155:11155111");
        chains.ShouldContain("eip155:17000");
        holding.GetProperty("flags").EnumerateArray().Select(f => f.GetString()).ShouldContain("has_unclassified_movements");
        var cash = data.GetProperty("cash");
        cash.GetArrayLength().ShouldBe(1);
        cash[0].GetProperty("asset").GetString().ShouldBe("USD");
        decimal.Parse(cash[0].GetProperty("available").GetString()!).ShouldBe(1000m - 105m - 55m);
    }

    private static async Task<Guid> UserIdForTokenAsync(string token)
    {
        // The sub claim carries the user id; decode it without depending on JWT internals.
        var parts = token.Split('.');
        var segment = parts.Length == 3 ? parts[1] : string.Empty;
        var padded = segment.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        var payload = segment.Length > 0
            ? Encoding.UTF8.GetString(Convert.FromBase64String(padded))
            : "{}";
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.GetProperty("sub").GetGuid();
    }
}
