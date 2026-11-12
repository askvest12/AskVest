using System.Net;
using System.Text.Json;
using AskVest.IntegrationTests.Catalog;
using AskVest.IntegrationTests.Compliance;
using AskVest.IntegrationTests.Identity;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Catalog.Import.Data;
using AskVest.Modules.Compliance.Providers;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Trading;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class TradingApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string WebhookSecret = "test-kyc-webhook-secret-0123456789abcdef";
    private const string Password = ComplianceTestUsers.GoodPassword;
    private const string Pin = "246810";
    private const string GatewayKey = "gw-trading-test";

    private AuthApiFactory _factory = null!;
    private string _assetCode = null!;
    private Guid _tokenId;

    public async ValueTask InitializeAsync()
    {
        var settings = new Dictionary<string, string?>(ComplianceTestUsers.Settings());
        settings["MarketData:Enabled"] = "true";
        settings["MarketData:Provider"] = "fake";
        settings["MarketData:Fake:Session"] = "regular";
        settings["MarketData:PollIntervalSeconds"] = "1";
        settings["MarketData:LiveThresholdSeconds"] = "30";
        settings["MarketData:StaleThresholdSeconds"] = "300";
        settings["Trading:ProgressIntervalSeconds"] = "1";
        settings["Trading:BuyEnabled"] = "true";
        settings["Trading:SellEnabled"] = "true";
        settings["Trading:MinOrderAmount"] = "1";
        settings["Trading:MaxOrderAmount"] = "100000";
        settings["Trading:Rolling24HourMax"] = "100000";
        settings["Trading:MaxNonFinalOrders"] = "10";
        settings["Trading:QuoteLifetimeSeconds"] = "30";
        settings["Trading:SubmissionStuckSeconds"] = "10";
        settings["Trading:SubmittedTimeoutSeconds"] = "60";
        settings[$"Trading:Gateway:Routes:{GatewayKey}:Provider"] = "fake";
        settings[$"Trading:Gateway:Routes:{GatewayKey}:SettlementAsset"] = "USD";
        _factory = ComplianceTestUsers.CreateFactory(fixture, settings);
        _factory.CreateClient();

        var suffix = CatalogImportTestData.Suffix();
        _assetCode = $"TST{suffix}".ToUpperInvariant();
        var symbol = $"T{suffix}".ToUpperInvariant()[..8];
        var token = CatalogImportTestData.Token(
            issuerId: "issuer-trading",
            address: $"0x{suffix}{new string('b', 32)}",
            assetCode: _assetCode,
            gatewayKey: GatewayKey,
            countries: ["NG"])
            with { Symbol = symbol };
        var instrument = CatalogImportTestData.Instrument("instr-trading", [token]) with
        {
            Symbol = symbol,
            ListingExchange = "NMS",
        };
        var imported = await CatalogImportTestData.ImportFolderAsync(
            fixture,
            [CatalogImportTestData.Chain()],
            [CatalogImportTestData.Issuer("issuer-trading")],
            [instrument],
            TestContext.Current.CancellationToken);
        imported.IsSuccess.ShouldBeTrue(imported.Error?.Message);
        _tokenId = await TokenIdAsync(_assetCode);
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient Client() => _factory.CreateClient();

    private async Task<Guid> TokenIdAsync(string assetCode)
    {
        await using var connection = new Npgsql.NpgsqlConnection(fixture.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT id FROM catalog.tokens WHERE ledger_asset_code = @code", connection);
        command.Parameters.AddWithValue("code", assetCode);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        value.ShouldNotBeNull($"no catalog token with asset code '{assetCode}'");
        return (Guid)value!;
    }

    private async Task<string> RealReadyUserAsync(HttpClient client)
    {
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        await ComplianceTestUsers.AcceptDocumentsAsync(client, token, ComplianceTestUsers.AllDocuments);
        await ComplianceTestUsers.SetCountryAsync(client, token, "NG");

        using var start = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/compliance/kyc/start", new { }).Auth(token),
            TestContext.Current.CancellationToken);
        start.StatusCode.ShouldBe(HttpStatusCode.OK);
        var reference = (await ComplianceTestUsers.DataAsync(start))
            .GetProperty("providerReference").GetString()!;

        var body = JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["eventId"] = $"evt-{Guid.CreateVersion7():N}",
            ["providerReference"] = reference,
            ["outcome"] = "approved",
            ["verifiedCountry"] = "NG",
            ["fullName"] = "Trading Test",
            ["dateOfBirth"] = "1990-04-12",
        });
        var timestamp = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds().ToString();
        var webhook = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/v1/webhooks/kyc/fake", UriKind.Relative))
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        webhook.Headers.Add("X-Webhook-Timestamp", timestamp);
        webhook.Headers.Add("X-Webhook-Signature", FakeKycProvider.Signature(timestamp, body, WebhookSecret));
        using var webhookResponse = await client.SendAsync(webhook, TestContext.Current.CancellationToken);
        webhookResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var pin = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/account/pin", new { password = Password, pin = Pin }).Auth(token),
            TestContext.Current.CancellationToken);
        pin.StatusCode.ShouldBe(HttpStatusCode.OK);
        return token;
    }

    private static async Task<JsonElement> SendAsync(HttpClient client, HttpRequestMessage request)
    {
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return await ComplianceTestUsers.EnvelopeAsync(response);
    }

    private static async Task<JsonElement> SendOkAsync(HttpClient client, HttpRequestMessage request, HttpStatusCode expected)
    {
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(expected);
        return await ComplianceTestUsers.EnvelopeAsync(response);
    }

    private static async Task<JsonElement> DataAsync(HttpClient client, HttpRequestMessage request, HttpStatusCode expected)
        => (await SendOkAsync(client, request, expected)).GetProperty("data");

    private async Task<JsonElement> QuoteAsync(HttpClient client, string token, string side, string? amount = null, string? quantity = null, bool? sellAll = null)
    {
        var body = new Dictionary<string, object?> { ["tokenId"] = _tokenId, ["side"] = side };
        if (amount is not null)
        {
            body["amount"] = amount;
        }

        if (quantity is not null)
        {
            body["quantity"] = quantity;
        }

        if (sellAll is not null)
        {
            body["sellAll"] = sellAll;
        }

        var data = await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/quotes", body).Auth(token), HttpStatusCode.OK);
        return data;
    }

    private static async Task<JsonElement> StepUpAsync(HttpClient client, string token, string binding)
    {
        var data = await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/auth/step-up", new { purpose = "authorize_order", pin = Pin, binding }).Auth(token),
            HttpStatusCode.OK);
        return data;
    }

    private static async Task<JsonElement> PlaceOrderAsync(HttpClient client, string token, Guid quoteId, string grantToken, string? key = null)
    {
        var data = await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/orders", new { quoteId, stepUpToken = grantToken }, key).Auth(token),
            HttpStatusCode.OK);
        return data;
    }

    private static async Task<JsonElement> GetOrderAsync(HttpClient client, string token, Guid orderId)
        => await DataAsync(client,
            ComplianceTestUsers.Get($"/api/v1/real/orders/{orderId}").Auth(token), HttpStatusCode.OK);

    private static async Task<JsonElement> WaitForOrderAsync(HttpClient client, string token, Guid orderId, params string[] states)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(45);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var order = await GetOrderAsync(client, token, orderId);
            var state = order.GetProperty("state").GetString();
            if (states.Contains(state, StringComparer.Ordinal))
            {
                return order;
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Order {orderId} never reached [{string.Join(',', states)}].");
    }

    private static async Task<JsonElement> BalancesAsync(HttpClient client, string token)
        => await DataAsync(client,
            ComplianceTestUsers.Get("/api/v1/real/balances").Auth(token), HttpStatusCode.OK);

    private static decimal Balance(JsonElement balances, string asset, string property)
        => balances.EnumerateArray()
            .Where(b => b.GetProperty("asset").GetString() == asset)
            .Select(b => decimal.Parse(b.GetProperty(property).GetString()!, System.Globalization.CultureInfo.InvariantCulture))
            .FirstOrDefault();

    private static async Task SetScenarioAsync(HttpClient client, string token, string scenario)
    {
        var data = await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/diagnostics/trading-scenario", new { scenario }).Auth(token),
            HttpStatusCode.OK);
        data.GetProperty("applied").GetBoolean().ShouldBeTrue();
    }

    private async Task<string> FundedUserAsync(HttpClient client, string amount = "100")
    {
        var token = await RealReadyUserAsync(client);
        await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/diagnostics/ledger-post",
                new { kind = "credit", asset = "USD", amount }).Auth(token),
            HttpStatusCode.OK);
        return token;
    }

    [Fact]
    public async Task Buy_Then_Sell_Settles_Exactly_And_Replay_Is_Safe()
    {
        var client = Client();
        var token = await FundedUserAsync(client);

        var quote = await QuoteAsync(client, token, "buy", amount: "10");
        quote.GetProperty("state").GetString().ShouldBe("open");
        quote.GetProperty("settlementAsset").GetString().ShouldBe("USD");
        var quoteId = quote.GetProperty("id").GetGuid();
        var termsHash = quote.GetProperty("termsHash").GetString()!;
        var total = decimal.Parse(quote.GetProperty("total").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var quantity = decimal.Parse(quote.GetProperty("quantity").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        var before = await BalancesAsync(client, token);
        var usdBefore = Balance(before, "USD", "available");

        var grant = (await StepUpAsync(client, token, termsHash)).GetProperty("grantToken").GetString()!;
        var orderKey = $"ord-{Guid.CreateVersion7():N}";
        var order = await PlaceOrderAsync(client, token, quoteId, grant, orderKey);
        var orderId = order.GetProperty("id").GetGuid();
        order.GetProperty("state").GetString().ShouldBe("pending_submission");

        var replay = await PlaceOrderAsync(client, token, quoteId, grant, orderKey);
        replay.GetProperty("id").GetGuid().ShouldBe(orderId);

        var executed = await WaitForOrderAsync(client, token, orderId, "executed", "failed", "needs_reconciliation");
        executed.GetProperty("state").GetString().ShouldBe("executed");

        var after = await BalancesAsync(client, token);
        Balance(after, "USD", "available").ShouldBe(usdBefore - total);
        Balance(after, "USD", "held").ShouldBe(0m);
        Balance(after, _assetCode, "available").ShouldBe(quantity);

        var sellQuote = await QuoteAsync(client, token, "sell", sellAll: true);
        var sellGrant = (await StepUpAsync(client, token, sellQuote.GetProperty("termsHash").GetString()!))
            .GetProperty("grantToken").GetString()!;
        var sellOrder = await PlaceOrderAsync(client, token, sellQuote.GetProperty("id").GetGuid(), sellGrant);
        var sellDone = await WaitForOrderAsync(client, token, sellOrder.GetProperty("id").GetGuid(), "executed", "failed", "needs_reconciliation");
        sellDone.GetProperty("state").GetString().ShouldBe("executed");

        var final = await BalancesAsync(client, token);
        Balance(final, _assetCode, "available").ShouldBe(0m);
        Balance(final, _assetCode, "held").ShouldBe(0m);
        Balance(final, "USD", "held").ShouldBe(0m);
    }

    [Fact]
    public async Task Rejected_Submission_Fails_And_Releases_The_Hold()
    {
        var client = Client();
        var token = await FundedUserAsync(client, "50");
        await SetScenarioAsync(client, token, "rejected");
        try
        {
            var quote = await QuoteAsync(client, token, "buy", amount: "6");
            var grant = (await StepUpAsync(client, token, quote.GetProperty("termsHash").GetString()!))
                .GetProperty("grantToken").GetString()!;
            var order = await PlaceOrderAsync(client, token, quote.GetProperty("id").GetGuid(), grant);
            var done = await WaitForOrderAsync(client, token, order.GetProperty("id").GetGuid(), "failed", "executed", "needs_reconciliation");
            done.GetProperty("state").GetString().ShouldBe("failed");

            var balances = await BalancesAsync(client, token);
            Balance(balances, "USD", "held").ShouldBe(0m);
        }
        finally
        {
            await SetScenarioAsync(client, token, "normal");
        }
    }

    [Fact]
    public async Task Mismatched_Fill_Goes_To_Reconciliation_And_Keeps_The_Hold()
    {
        var client = Client();
        var token = await FundedUserAsync(client, "50");
        await SetScenarioAsync(client, token, "fill_mismatch");
        try
        {
            var quote = await QuoteAsync(client, token, "buy", amount: "7");
            var grant = (await StepUpAsync(client, token, quote.GetProperty("termsHash").GetString()!))
                .GetProperty("grantToken").GetString()!;
            var order = await PlaceOrderAsync(client, token, quote.GetProperty("id").GetGuid(), grant);
            var done = await WaitForOrderAsync(client, token, order.GetProperty("id").GetGuid(), "needs_reconciliation", "executed", "failed");
            done.GetProperty("state").GetString().ShouldBe("needs_reconciliation");

            var balances = await BalancesAsync(client, token);
            Balance(balances, "USD", "held").ShouldBeGreaterThan(0m);
            Balance(balances, _assetCode, "available").ShouldBe(0m);
        }
        finally
        {
            await SetScenarioAsync(client, token, "normal");
        }
    }

    [Fact]
    public async Task StepUp_Must_Match_Quote_And_Other_Users_See_NotFound()
    {
        var client = Client();
        var token = await FundedUserAsync(client, "50");
        var quote = await QuoteAsync(client, token, "buy", amount: "6");
        var quoteId = quote.GetProperty("id").GetGuid();

        var wrongGrant = (await StepUpAsync(client, token, "different-terms"))
            .GetProperty("grantToken").GetString()!;
        var refused = await SendAsync(client, ComplianceTestUsers.Post(
            "/api/v1/real/orders", new { quoteId, stepUpToken = wrongGrant }).Auth(token));
        refused.GetProperty("success").GetBoolean().ShouldBeFalse();
        ComplianceTestUsers.ErrorCode(refused).ShouldBe("auth.step_up_invalid");

        var otherToken = await FundedUserAsync(client, "50");
        var otherGet = await SendOkAsync(client,
            ComplianceTestUsers.Get($"/api/v1/real/quotes/{quoteId}").Auth(otherToken),
            HttpStatusCode.NotFound);
        ComplianceTestUsers.ErrorCode(otherGet).ShouldBe("resource.not_found");

        var stranger = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        var denied = await SendOkAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/quotes",
                new { tokenId = _tokenId, side = "buy", amount = "6" }).Auth(stranger),
            HttpStatusCode.Forbidden);
        denied.GetProperty("success").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Limits_And_Invalid_Input_Are_Rejected()
    {
        var client = Client();
        var token = await FundedUserAsync(client, "5");

        var smallQuote = await QuoteAsync(client, token, "buy", amount: "0.5");
        var smallGrant = (await StepUpAsync(client, token, smallQuote.GetProperty("termsHash").GetString()!))
            .GetProperty("grantToken").GetString()!;
        var belowMin = await SendAsync(client, ComplianceTestUsers.Post(
            "/api/v1/real/orders",
            new { quoteId = smallQuote.GetProperty("id").GetGuid(), stepUpToken = smallGrant }).Auth(token));
        belowMin.GetProperty("success").GetBoolean().ShouldBeFalse();
        ComplianceTestUsers.ErrorCode(belowMin).ShouldBe("trading.order_invalid");

        var badSide = await SendAsync(client, ComplianceTestUsers.Post(
            "/api/v1/real/quotes", new { tokenId = _tokenId, side = "hold" }).Auth(token));
        badSide.GetProperty("success").GetBoolean().ShouldBeFalse();

        var unauthenticated = await SendOkAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/quotes", new { tokenId = _tokenId, side = "buy", amount = "6" }),
            HttpStatusCode.Unauthorized);
        unauthenticated.GetProperty("success").GetBoolean().ShouldBeFalse();
    }
}
