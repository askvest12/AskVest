using System.Net;
using System.Text.Json;
using AskVest.IntegrationTests.Catalog;
using AskVest.IntegrationTests.Compliance;
using AskVest.IntegrationTests.Identity;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Catalog.Import.Data;
using AskVest.Modules.Compliance.Providers;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Wallets;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class WalletsApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string WebhookSecret = "test-kyc-webhook-secret-0123456789abcdef";
    private const string Password = ComplianceTestUsers.GoodPassword;
    private const string Pin = "246810";
    private const string ChainId = "eip155:11155111";

    private AuthApiFactory _factory = null!;
    private string _assetCode = null!;
    private string _contractAddress = null!;
    private Guid _tokenId;

    public async ValueTask InitializeAsync()
    {
        var settings = new Dictionary<string, string?>(ComplianceTestUsers.Settings());
        settings["MarketData:Enabled"] = "true";
        settings["MarketData:Provider"] = "fake";
        settings["MarketData:Fake:Session"] = "regular";
        settings["MarketData:PollIntervalSeconds"] = "1";
        settings["Wallets:DepositIntervalSeconds"] = "1";
        settings["Wallets:ProgressIntervalSeconds"] = "1";
        settings["Wallets:AddressCoolingOffHours"] = "0";
        settings["Wallets:PreviewLifetimeSeconds"] = "60";
        settings["Wallets:WithdrawalMinQuantity"] = "0.000001";
        settings["Wallets:MaxWithdrawalValueUsd"] = "100000";
        settings["Wallets:Rolling24HourMaxValueUsd"] = "100000";
        settings["Wallets:Custody:Fake:CompletionDelaySeconds"] = "1";
        settings["Wallets:Custody:Fake:PendingFinalitySeconds"] = "0";
        settings["Wallets:Custody:Routes:0:ChainId"] = ChainId;
        settings["Wallets:Custody:Routes:0:Provider"] = "fake";
        settings["Wallets:Screening:Provider"] = "fake";
        settings["Wallets:Screening:Fake:HitMarkers:0"] = "1111";
        _factory = ComplianceTestUsers.CreateFactory(fixture, settings);
        _factory.CreateClient();

        var suffix = CatalogImportTestData.Suffix();
        _assetCode = $"WLT{suffix}".ToUpperInvariant();
        _contractAddress = $"0x{suffix}{new string('c', 32)}";
        var token = CatalogImportTestData.Token(
            issuerId: "issuer-wallets",
            address: _contractAddress,
            assetCode: _assetCode,
            gatewayKey: "gw-none",
            countries: ["NG"],
            externalTransfers: "allowed")
            with
        { Symbol = $"W{suffix}".ToUpperInvariant()[..8] };
        var instrument = CatalogImportTestData.Instrument("instr-wallets", [token]) with
        {
            Symbol = token.Symbol,
            ListingExchange = "NMS",
        };
        var imported = await CatalogImportTestData.ImportFolderAsync(
            fixture,
            [CatalogImportTestData.Chain()],
            [CatalogImportTestData.Issuer("issuer-wallets")],
            [instrument],
            TestContext.Current.CancellationToken);
        imported.IsSuccess.ShouldBeTrue(imported.Error?.Message);
        _tokenId = await TokenIdAsync(_assetCode);
        _contractAddress = await CanonicalAddressAsync(_assetCode);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient Client() => _factory.CreateClient();

    private async Task<Guid> TokenIdAsync(string assetCode)
    {
        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT id FROM catalog.tokens WHERE ledger_asset_code = @code", connection);
        command.Parameters.AddWithValue("code", assetCode);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        value.ShouldNotBeNull($"no catalog token with asset code '{assetCode}'");
        return (Guid)value!;
    }

    private async Task<string> CanonicalAddressAsync(string assetCode)
    {
        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT canonical_address FROM catalog.tokens WHERE ledger_asset_code = @code", connection);
        command.Parameters.AddWithValue("code", assetCode);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        value.ShouldNotBeNull($"no catalog token with asset code '{assetCode}'");
        return (string)value!;
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
            ["fullName"] = "Wallets Test",
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

    private static async Task<JsonElement> DataAsync(HttpClient client, HttpRequestMessage request, HttpStatusCode expected)
    {
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(expected);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> StepUpAsync(HttpClient client, string token, string purpose, string binding)
        => (await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/auth/step-up", new { purpose, pin = Pin, binding }).Auth(token),
            HttpStatusCode.OK)).GetProperty("data");

    private static async Task<JsonElement> BalancesAsync(HttpClient client, string token)
        => (await DataAsync(client,
            ComplianceTestUsers.Get("/api/v1/real/balances").Auth(token), HttpStatusCode.OK)).GetProperty("data");

    private static decimal Balance(JsonElement balances, string asset, string property)
        => balances.EnumerateArray()
            .Where(b => b.GetProperty("asset").GetString() == asset)
            .Select(b => decimal.Parse(b.GetProperty(property).GetString()!, System.Globalization.CultureInfo.InvariantCulture))
            .FirstOrDefault();

    private static async Task<string> DepositAddressAsync(HttpClient client, string token)
    {
        var wallet = (await DataAsync(client,
            ComplianceTestUsers.Get($"/api/v1/real/wallets/{ChainId}").Auth(token),
            HttpStatusCode.OK)).GetProperty("data");
        return wallet.GetProperty("address").GetString()!;
    }

    [Fact]
    public async Task Deposit_Then_Withdrawal_Completes_And_Releases_Nothing()
    {
        var client = Client();
        var token = await RealReadyUserAsync(client);
        var address = await DepositAddressAsync(client, token);
        address.ShouldNotBeNullOrWhiteSpace();

        // Inject a deposit to the user's own deposit address.
        await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/diagnostics/wallets-deposit", new
            {
                chainId = ChainId,
                destinationAddress = address,
                tokenContractAddress = _contractAddress,
                quantity = "5",
                senderAddress = "sender-clean-1",
                finality = "final",
            }).Auth(token),
            HttpStatusCode.OK);

        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(30);
        decimal tokenBalance = 0m;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var balances = await BalancesAsync(client, token);
            tokenBalance = Balance(balances, _assetCode, "available");
            if (tokenBalance >= 5m)
            {
                break;
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        tokenBalance.ShouldBe(5m);

        // The deposit also shows in the deposits feed as credited.
        var deposits = (await DataAsync(client,
            ComplianceTestUsers.Get("/api/v1/real/deposits").Auth(token), HttpStatusCode.OK))
            .GetProperty("data");
        deposits.EnumerateArray().ShouldContain(d => d.GetProperty("state").GetString() == "credited");

        // Add an external withdrawal address (cooling off is disabled in tests).
        var destination = $"0x{new string('3', 40)}";
        var grant = (await StepUpAsync(client, token, "change_withdrawal_address", destination))
            .GetProperty("grantToken").GetString()!;
        var added = (await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/withdrawal-addresses",
                new { chainId = ChainId, address = destination, label = (string?)null, stepUpToken = grant },
                $"addr-{Guid.CreateVersion7():N}").Auth(token),
            HttpStatusCode.OK)).GetProperty("data");
        added.GetProperty("state").GetString().ShouldBe("active");
        var addressId = added.GetProperty("id").GetGuid();

        // Preview the withdrawal.
        var preview = (await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/withdrawals/previews",
                new { tokenId = _tokenId, addressId, grossQuantity = "2" },
                $"prev-{Guid.CreateVersion7():N}").Auth(token),
            HttpStatusCode.OK)).GetProperty("data");
        preview.GetProperty("state").GetString().ShouldBe("previewed");
        var termsHash = preview.GetProperty("termsHash").GetString()!;
        var previewId = preview.GetProperty("id").GetGuid();

        // Step up bound to the terms hash and place.
        var withdrawGrant = (await StepUpAsync(client, token, "authorize_withdrawal", termsHash))
            .GetProperty("grantToken").GetString()!;
        var placed = (await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/withdrawals",
                new { previewId, stepUpToken = withdrawGrant },
                $"wd-{Guid.CreateVersion7():N}").Auth(token),
            HttpStatusCode.OK)).GetProperty("data");
        var withdrawalId = placed.GetProperty("id").GetGuid();

        deadline = TimeProvider.System.GetUtcNow().AddSeconds(45);
        string state = placed.GetProperty("state").GetString()!;
        string? detail = null;
        while (TimeProvider.System.GetUtcNow() < deadline && state is not ("completed" or "failed" or "needs_reconciliation"))
        {
            await Task.Delay(700, TestContext.Current.CancellationToken);
            var current = (await DataAsync(client,
                ComplianceTestUsers.Get($"/api/v1/real/withdrawals/{withdrawalId}").Auth(token),
                HttpStatusCode.OK)).GetProperty("data");
            state = current.GetProperty("state").GetString()!;
            detail = current.TryGetProperty("detailCode", out var code) ? code.GetString() : null;
        }

        state.ShouldBe("completed", $"withdrawal {withdrawalId} ended {state} detail={detail}");

        var finalBalances = await BalancesAsync(client, token);
        Balance(finalBalances, _assetCode, "available").ShouldBe(3m);
        Balance(finalBalances, _assetCode, "held").ShouldBe(0m);
    }

    [Fact]
    public async Task Screening_Hit_And_StepUp_Binding_Are_Enforced()
    {
        var client = Client();
        var token = await RealReadyUserAsync(client);

        // A screened address is refused even with a valid grant.
        var badDestination = $"0x{new string('1', 40)}";
        var badGrant = (await StepUpAsync(client, token, "change_withdrawal_address", badDestination))
            .GetProperty("grantToken").GetString()!;
        var rejected = await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/withdrawal-addresses",
                new { chainId = ChainId, address = badDestination, label = (string?)null, stepUpToken = badGrant },
                $"addr-{Guid.CreateVersion7():N}").Auth(token),
            HttpStatusCode.BadRequest);
        rejected.GetProperty("success").GetBoolean().ShouldBeFalse();
        rejected.GetProperty("error").GetProperty("code").GetString().ShouldBe("wallet.address_invalid");

        // A grant bound to a different address cannot add this one.
        var destination = $"0x{new string('4', 40)}";
        var wrongGrant = (await StepUpAsync(client, token, "change_withdrawal_address", "other-address"))
            .GetProperty("grantToken").GetString()!;
        var mismatch = await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/withdrawal-addresses",
                new { chainId = ChainId, address = destination, label = (string?)null, stepUpToken = wrongGrant },
                $"addr-{Guid.CreateVersion7():N}").Auth(token),
            HttpStatusCode.Forbidden);
        mismatch.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.step_up_invalid");
    }
}
