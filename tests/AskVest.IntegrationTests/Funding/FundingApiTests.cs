using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AskVest.IntegrationTests.Compliance;
using AskVest.IntegrationTests.Identity;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Compliance.Providers;
using AskVest.Modules.Funding.Contracts;
using AskVest.Modules.Funding.Providers;
using AskVest.Modules.Funding.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Funding;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class FundingApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string WebhookSecret = "funding-webhook-secret-0123456789abcdef0123456789";
    private const string Password = ComplianceTestUsers.GoodPassword;
    private const string Pin = "246810";

    private AuthApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        var settings = new Dictionary<string, string?>(ComplianceTestUsers.Settings());
        settings["Funding:Providers:0:Key"] = "fake";
        settings["Funding:Providers:0:Adapter"] = "fake";
        settings["Funding:Providers:0:WebhookSecret"] = WebhookSecret;
        settings["Funding:Providers:0:SettlementBalances:ZAR"] = "1000000";
        settings["Funding:Providers:0:SettlementBalances:XOF"] = "0";
        settings["Funding:Currencies:0:Code"] = "ZAR";
        settings["Funding:Currencies:0:Scale"] = "2";
        settings["Funding:Currencies:0:Countries:0"] = "NG";
        settings["Funding:Currencies:0:Provider"] = "fake";
        settings["Funding:Currencies:0:DepositMinimum"] = "1";
        settings["Funding:Currencies:0:DepositMaximum"] = "50000";
        settings["Funding:Currencies:0:DepositDailyMaximum"] = "100000";
        settings["Funding:Currencies:0:WithdrawalMinimum"] = "1";
        settings["Funding:Currencies:0:WithdrawalMaximum"] = "50000";
        settings["Funding:Currencies:0:WithdrawalDaily24Maximum"] = "100000";
        settings["Funding:Currencies:0:WithdrawalDaily30Maximum"] = "200000";
        settings["Funding:Currencies:0:WithdrawalFlatFee"] = "1";
        settings["Funding:Currencies:0:WithdrawalFeeBps"] = "100";
        settings["Funding:Currencies:0:ReviewThresholdValue"] = "100000";
        settings["Funding:Currencies:1:Code"] = "XOF";
        settings["Funding:Currencies:1:Scale"] = "0";
        settings["Funding:Currencies:1:Countries:0"] = "NG";
        settings["Funding:Currencies:1:Provider"] = "fake";
        settings["Funding:Currencies:1:DepositMinimum"] = "1";
        settings["Funding:Currencies:1:DepositMaximum"] = "50000";
        settings["Funding:Currencies:1:DepositDailyMaximum"] = "100000";
        settings["Funding:Currencies:1:WithdrawalMinimum"] = "1";
        settings["Funding:Currencies:1:WithdrawalMaximum"] = "50000";
        settings["Funding:Currencies:1:WithdrawalDaily24Maximum"] = "100000";
        settings["Funding:Currencies:1:WithdrawalDaily30Maximum"] = "200000";
        settings["Funding:Currencies:1:WithdrawalFlatFee"] = "0";
        settings["Funding:Currencies:1:WithdrawalFeeBps"] = "0";
        settings["Funding:Currencies:1:ReviewThresholdValue"] = "10";
        settings["Funding:Pairs:0:From"] = "ZAR";
        settings["Funding:Pairs:0:To"] = "USD";
        settings["Funding:Pairs:0:SpreadBps"] = "50";
        settings["Funding:Pairs:0:FeeBps"] = "100";
        settings["Funding:Pairs:0:MinimumSource"] = "1";
        settings["Funding:Pairs:0:MaximumSource"] = "50000";
        settings["Funding:Pairs:0:DailyCapSource"] = "100000";
        settings["Funding:Pairs:0:MaxJumpPercent"] = "50";
        settings["Funding:Pairs:0:Enabled"] = "true";
        settings["Funding:FxProvider"] = "fake";
        settings["Funding:FxRates:0:From"] = "ZAR";
        settings["Funding:FxRates:0:To"] = "USD";
        settings["Funding:FxRates:0:Rate"] = "0.05";
        settings["Funding:ProviderTimeoutSeconds"] = "5";
        settings["Funding:DepositPollIntervalSeconds"] = "1";
        settings["Funding:PayoutPollIntervalSeconds"] = "1";
        settings["Funding:CleanupIntervalSeconds"] = "10";
        settings["Funding:SubmissionStuckSeconds"] = "10";
        settings["Funding:SubmittedTimeoutSeconds"] = "60";
        settings["Funding:ReservesIntervalMinutes"] = "1";
        settings["Funding:ReservesShortfallThreshold"] = "2";
        settings["Funding:FreshDepositLockHours"] = "0.002";
        settings["Funding:BeneficiaryCoolingOffHours"] = "0.002";
        settings["Funding:ReviewMinAccountAgeHours"] = "0";
        _factory = ComplianceTestUsers.CreateFactory(fixture, settings);
        _factory.CreateClient();
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient Client() => _factory.CreateClient();

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
            ["fullName"] = "Funding Test",
            ["dateOfBirth"] = "1990-04-12",
        });
        var timestamp = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds().ToString();
        var webhook = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/v1/webhooks/kyc/fake", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        webhook.Headers.Add("X-Webhook-Timestamp", timestamp);
        webhook.Headers.Add("X-Webhook-Signature", FakeKycProvider.Signature(timestamp, body, "test-kyc-webhook-secret-0123456789abcdef"));
        using var webhookResponse = await client.SendAsync(webhook, TestContext.Current.CancellationToken);
        webhookResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var pin = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/account/pin", new { password = Password, pin = Pin }).Auth(token),
            TestContext.Current.CancellationToken);
        pin.StatusCode.ShouldBe(HttpStatusCode.OK);
        return token;
    }

    private static async Task<JsonElement> DataAsync(
        HttpClient client, HttpRequestMessage request, HttpStatusCode expected)
    {
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(expected);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> EnvelopeAsync(
        HttpClient client, HttpRequestMessage request, HttpStatusCode expected)
    {
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(expected);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> StepUpAsync(
        HttpClient client, string token, string purpose, string binding)
        => (await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/auth/step-up", new { purpose, pin = Pin, binding }).Auth(token),
            HttpStatusCode.OK)).GetProperty("data");

    private static async Task<JsonElement> BalancesAsync(HttpClient client, string token)
        => (await DataAsync(client,
            ComplianceTestUsers.Get("/api/v1/real/balances").Auth(token), HttpStatusCode.OK)).GetProperty("data");

    private static decimal Amount(JsonElement row)
        => decimal.Parse(
            row.GetProperty("amount").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);

    private static decimal Balance(JsonElement balances, string asset, string property)
        => balances.EnumerateArray()
            .Where(b => b.GetProperty("asset").GetString() == asset)
            .Select(b => decimal.Parse(b.GetProperty(property).GetString()!, System.Globalization.CultureInfo.InvariantCulture))
            .FirstOrDefault();

    private static async Task<string> AccountReferenceAsync(HttpClient client, string token, string currency)
    {
        var account = (await DataAsync(client,
            ComplianceTestUsers.Post($"/api/v1/real/funding/accounts", new { currency },
                $"acct-{Guid.CreateVersion7():N}").Auth(token),
            HttpStatusCode.OK)).GetProperty("data");
        return account.GetProperty("instruction").GetProperty("reference").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendDepositWebhookAsync(
        HttpClient client,
        string providerDepositId,
        string accountReference,
        string currency,
        string amount,
        string state,
        bool payerVerified = true,
        string? eventId = null,
        string? signature = null,
        string? timestamp = null)
    {
        var body = JsonSerializer.Serialize(new
        {
            eventId = eventId ?? $"evt-{Guid.CreateVersion7():N}",
            kind = "deposit",
            deposit = new
            {
                providerDepositId,
                accountReference,
                currency,
                amount,
                providerFee = "0",
                payerVerified,
                state,
                observedAt = TimeProvider.System.GetUtcNow().ToString("O"),
            },
        });
        timestamp ??= TimeProvider.System.GetUtcNow().ToUnixTimeSeconds().ToString();
        var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri($"/api/v1/webhooks/funding/fake", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Webhook-Timestamp", timestamp);
        request.Headers.Add("X-Webhook-Signature",
            signature ?? FundingWebhookSignature.Sign(WebhookSecret, timestamp, body));
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> WaitForDepositStateAsync(
        HttpClient client, string token, string currency, string amount, string wanted, int seconds = 20)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(seconds);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var deposits = (await DataAsync(client,
                ComplianceTestUsers.Get("/api/v1/real/funding/deposits?limit=50").Auth(token),
                HttpStatusCode.OK)).GetProperty("data");
            foreach (var deposit in deposits.GetProperty("items").EnumerateArray())
            {
                if (deposit.GetProperty("state").GetString() == wanted
                    && deposit.GetProperty("currency").GetString() == currency
                    && decimal.Parse(
                        deposit.GetProperty("amount").GetString()!,
                        System.Globalization.CultureInfo.InvariantCulture)
                    == decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture))
                {
                    return deposit.Clone();
                }
            }

            await Task.Delay(400, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Deposit never reached '{wanted}'.");
    }

    private static async Task<JsonElement> WaitForWithdrawalStateAsync(
        HttpClient client, string token, Guid withdrawalId, string wanted, int seconds = 30)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(seconds);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var withdrawal = (await DataAsync(client,
                ComplianceTestUsers.Get($"/api/v1/real/funding/withdrawals/{withdrawalId}").Auth(token),
                HttpStatusCode.OK)).GetProperty("data");
            var state = withdrawal.GetProperty("state").GetString()!;
            if (state == wanted)
            {
                return withdrawal.Clone();
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        var last = (await DataAsync(client,
            ComplianceTestUsers.Get($"/api/v1/real/funding/withdrawals/{withdrawalId}").Auth(token),
            HttpStatusCode.OK)).GetProperty("data");
        throw new TimeoutException(
            $"Withdrawal {withdrawalId} never reached '{wanted}'; last state {last.GetProperty("state").GetString()}, detail {last.GetProperty("detailCode")}");
    }

    private static async Task<Guid> AddActiveBeneficiaryAsync(
        HttpClient client, string token, string currency, string accountName)
    {
        var details = new Dictionary<string, string>
        {
            ["accountName"] = accountName,
            ["accountNumber"] = "0001234567",
            ["bankName"] = "Fake Settlement Bank",
        };
        var userId = UserIdFromToken(token);
        var providerReference = BeneficiaryReference(currency, userId, details);
        var grant = (await StepUpAsync(
            client, token, "change_withdrawal_address", $"{currency}:{providerReference}"))
            .GetProperty("grantToken").GetString()!;
        var added = (await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/funding/beneficiaries",
                new { currency, details, stepUpToken = grant },
                $"ben-{Guid.CreateVersion7():N}").Auth(token),
            HttpStatusCode.OK)).GetProperty("data");
        var id = added.GetProperty("id").GetGuid();

        // Cooling off and the fresh-deposit lock are a few seconds in this
        // suite; listing promotes the beneficiary once active-from passes.
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(20);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var beneficiaries = (await DataAsync(client,
                ComplianceTestUsers.Get("/api/v1/real/funding/beneficiaries").Auth(token),
                HttpStatusCode.OK)).GetProperty("data");
            foreach (var beneficiary in beneficiaries.EnumerateArray())
            {
                if (beneficiary.GetProperty("id").GetGuid() == id
                    && beneficiary.GetProperty("state").GetString() == "active")
                {
                    return id;
                }
            }

            await Task.Delay(400, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("Beneficiary never became usable.");
    }

    private static Guid UserIdFromToken(string token)
    {
        var payload = token.Split('.')[1];
        var json = Encoding.UTF8.GetString(Base64UrlDecode(payload));
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("sub").GetGuid();
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

    private static string BeneficiaryReference(
        string currency, Guid userId, IReadOnlyDictionary<string, string> details)
    {
        var joined = string.Join('|',
            details.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value));
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"fake|{userId:D}|{currency}|{joined}")))
            .ToLowerInvariant();
        return $"fpb-{hash[..24]}";
    }

    private static async Task<JsonElement> PlaceWithdrawalAsync(
        HttpClient client, string token, string currency, string receiveAmount, Guid beneficiaryId)
    {
        var preview = (await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/funding/withdrawals/previews",
                new { currency, receiveAmount, beneficiaryId },
                $"prev-{Guid.CreateVersion7():N}").Auth(token),
            HttpStatusCode.OK)).GetProperty("data");
        var termsHash = preview.GetProperty("termsHash").GetString()!;
        var previewId = preview.GetProperty("id").GetGuid();
        var grant = (await StepUpAsync(client, token, "authorize_withdrawal", termsHash))
            .GetProperty("grantToken").GetString()!;
        return (await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/funding/withdrawals",
                new { previewId, stepUpToken = grant },
                $"wd-{Guid.CreateVersion7():N}").Auth(token),
            HttpStatusCode.OK)).GetProperty("data");
    }

    [Fact]
    public async Task Journey_Deposit_Convert_Beneficiary_Withdraw_Completes()
    {
        var client = Client();
        var token = await RealReadyUserAsync(client);

        var methods = (await DataAsync(client,
            ComplianceTestUsers.Get("/api/v1/real/funding/methods").Auth(token),
            HttpStatusCode.OK)).GetProperty("data");
        methods.EnumerateArray().ShouldContain(m => m.GetProperty("currency").GetString() == "ZAR");

        var accountReference = await AccountReferenceAsync(client, token, "ZAR");
        accountReference.ShouldStartWith("fpi-");

        // Signed webhook deposit: pending then completed credits once.
        var depositId = $"dep-{Guid.CreateVersion7():N}";
        var pending = await SendDepositWebhookAsync(
            client, depositId, accountReference, "ZAR", "200", "pending");
        pending.StatusCode.ShouldBe(HttpStatusCode.OK);
        var completed = await SendDepositWebhookAsync(
            client, depositId, accountReference, "ZAR", "200", "completed");
        completed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var replay = await SendDepositWebhookAsync(
            client, depositId, accountReference, "ZAR", "200", "completed");
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);

        var deposit = await WaitForDepositStateAsync(client, token, "ZAR", "200", "credited");
        Amount(deposit).ShouldBe(200m);

        // Replays changed nothing: still one credited deposit of 200.
        var credited = (await DataAsync(client,
            ComplianceTestUsers.Get("/api/v1/real/funding/deposits?state=credited").Auth(token),
            HttpStatusCode.OK)).GetProperty("data");
        credited.GetProperty("items").EnumerateArray().Count().ShouldBe(1);

        var balances = await BalancesAsync(client, token);
        Balance(balances, "ZAR", "available").ShouldBe(200m);

        // Internal conversion ZAR -> USD at the configured fake rate.
        var quote = (await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/funding/conversions/quotes",
                new { pair = "ZAR:USD", sourceAmount = "100" },
                $"quote-{Guid.CreateVersion7():N}").Auth(token),
            HttpStatusCode.OK)).GetProperty("data");
        quote.GetProperty("state").GetString().ShouldBe("quoted");
        var quoteId = quote.GetProperty("id").GetGuid();
        var conversion = (await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/funding/conversions",
                new { quoteId }, $"conv-{Guid.CreateVersion7():N}").Auth(token),
            HttpStatusCode.OK)).GetProperty("data");
        conversion.GetProperty("state").GetString().ShouldBe("completed");

        balances = await BalancesAsync(client, token);
        Balance(balances, "ZAR", "available").ShouldBe(100m);
        Balance(balances, "USD", "available").ShouldBe(4.925250m);

        // Beneficiary, cooling off, then withdrawal to completion.
        var beneficiaryId = await AddActiveBeneficiaryAsync(client, token, "ZAR", "Funding Test");
        var placed = await PlaceWithdrawalAsync(client, token, "ZAR", "50", beneficiaryId);
        var withdrawalId = placed.GetProperty("id").GetGuid();
        var final = await WaitForWithdrawalStateAsync(client, token, withdrawalId, "completed");
        final.GetProperty("detailCode").ValueKind.ShouldBe(JsonValueKind.Null);

        balances = await BalancesAsync(client, token);
        Balance(balances, "ZAR", "available").ShouldBe(48.5m);
        Balance(balances, "ZAR", "held").ShouldBe(0m);
    }

    [Fact]
    public async Task Webhook_Security_And_Dedup_Are_Enforced()
    {
        var client = Client();
        var token = await RealReadyUserAsync(client);
        var accountReference = await AccountReferenceAsync(client, token, "ZAR");

        // Unknown provider route.
        var unknown = await EnvelopeAsync(client, new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/v1/webhooks/funding/nope", UriKind.Relative))
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        }, HttpStatusCode.NotFound);
        unknown.GetProperty("error").GetProperty("code").GetString().ShouldBe("route.not_found");

        // Bad signature.
        var badSig = await EnvelopeAsync(client, await BuildWebhookRequestAsync(
            client, "dep-x", accountReference, "ZAR", "10", "completed", signature: "00"),
            HttpStatusCode.Unauthorized);
        badSig.GetProperty("error").GetProperty("code").GetString().ShouldBe("webhook.invalid_signature");

        // Stale timestamp.
        var stale = await EnvelopeAsync(client, await BuildWebhookRequestAsync(
            client, "dep-x", accountReference, "ZAR", "10", "completed",
            timestamp: (TimeProvider.System.GetUtcNow().ToUnixTimeSeconds() - 3600).ToString()),
            HttpStatusCode.BadRequest);
        stale.GetProperty("error").GetProperty("code").GetString().ShouldBe("webhook.stale_timestamp");

        // Pending deposit then completed credits exactly once.
        var depositId = $"dep-{Guid.CreateVersion7():N}";
        (await SendDepositWebhookAsync(client, depositId, accountReference, "ZAR", "75", "pending"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendDepositWebhookAsync(client, depositId, accountReference, "ZAR", "75", "completed"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        // Same provider deposit id under a new event id must not double-credit.
        (await SendDepositWebhookAsync(client, depositId, accountReference, "ZAR", "75", "completed"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var deposit = await WaitForDepositStateAsync(client, token, "ZAR", "75", "credited");
        Amount(deposit).ShouldBe(75m);
        var credited = (await DataAsync(client,
            ComplianceTestUsers.Get("/api/v1/real/funding/deposits?state=credited").Auth(token),
            HttpStatusCode.OK)).GetProperty("data");
        credited.GetProperty("items").EnumerateArray().Count().ShouldBe(1);
    }

    private static async Task<HttpRequestMessage> BuildWebhookRequestAsync(
        HttpClient client,
        string providerDepositId,
        string accountReference,
        string currency,
        string amount,
        string state,
        string? signature = null,
        string? timestamp = null)
    {
        var body = JsonSerializer.Serialize(new
        {
            eventId = $"evt-{Guid.CreateVersion7():N}",
            kind = "deposit",
            deposit = new
            {
                providerDepositId,
                accountReference,
                currency,
                amount,
                providerFee = "0",
                payerVerified = true,
                state,
                observedAt = TimeProvider.System.GetUtcNow().ToString("O"),
            },
        });
        timestamp ??= TimeProvider.System.GetUtcNow().ToUnixTimeSeconds().ToString();
        var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/v1/webhooks/funding/fake", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Webhook-Timestamp", timestamp);
        request.Headers.Add("X-Webhook-Signature",
            signature ?? FundingWebhookSignature.Sign(WebhookSecret, timestamp, body));
        return request;
    }

    [Fact]
    public async Task Quarantine_And_Reversal_Are_Resolved_Through_Operator_Contract()
    {
        var client = Client();
        var token = await RealReadyUserAsync(client);
        var accountReference = await AccountReferenceAsync(client, token, "ZAR");

        // Unverified payer quarantines, never credits.
        var quarantinedId = $"dep-{Guid.CreateVersion7():N}";
        (await SendDepositWebhookAsync(
            client, quarantinedId, accountReference, "ZAR", "300", "completed", payerVerified: false))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var quarantined = await WaitForDepositStateAsync(client, token, "ZAR", "300", "quarantined");
        quarantined.GetProperty("detailCode").GetString().ShouldNotBeNullOrWhiteSpace();

        var balances = await BalancesAsync(client, token);
        Balance(balances, "ZAR", "available").ShouldBe(0m);

        // Operator credits the quarantined deposit exactly once.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<IFundingOperator>();
            var resolved = await operations.ResolveDepositAsync(
                quarantined.GetProperty("id").GetGuid(), credit: true,
                TestContext.Current.CancellationToken);
            resolved.IsSuccess.ShouldBeTrue(resolved.Error?.Message);
            var again = await operations.ResolveDepositAsync(
                quarantined.GetProperty("id").GetGuid(), credit: false,
                TestContext.Current.CancellationToken);
            again.IsFailure.ShouldBeTrue("a resolved deposit cannot resolve twice");
        }

        balances = await BalancesAsync(client, token);
        Balance(balances, "ZAR", "available").ShouldBe(300m);

        // Reversal debits what is left and restricts the user.
        (await SendDepositWebhookAsync(
            client, quarantinedId, accountReference, "ZAR", "300", "reversed"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var reversed = await WaitForDepositStateAsync(client, token, "ZAR", "300", "reversed");
        reversed.GetProperty("state").GetString().ShouldBe("reversed");

        balances = await BalancesAsync(client, token);
        Balance(balances, "ZAR", "available").ShouldBe(0m);

        var beneficiaryId = await AddActiveBeneficiaryAsync(client, token, "ZAR", "Funding Test");
        var blocked = await EnvelopeAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/funding/withdrawals/previews",
                new { currency = "ZAR", receiveAmount = "10", beneficiaryId },
                $"prev-{Guid.CreateVersion7():N}").Auth(token),
            HttpStatusCode.UnprocessableEntity);
        blocked.GetProperty("error").GetProperty("code").GetString().ShouldBe("funding.not_available");

        // Operator clears the restriction and withdrawals work again.
        var userId = UserIdFromToken(token);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<IFundingOperator>();
            var cleared = await operations.ClearRestrictionAsync(userId, TestContext.Current.CancellationToken);
            cleared.IsSuccess.ShouldBeTrue(cleared.Error?.Message);
        }

        var balancesAfter = await BalancesAsync(client, token);
        Balance(balancesAfter, "ZAR", "available").ShouldBe(0m);
    }

    [Fact]
    public async Task Review_Approve_And_Reject_Flow_Through_Operator_Contract()
    {
        var client = Client();
        var token = await RealReadyUserAsync(client);
        var accountReference = await AccountReferenceAsync(client, token, "XOF");

        var depositId = $"dep-{Guid.CreateVersion7():N}";
        (await SendDepositWebhookAsync(client, depositId, accountReference, "XOF", "500", "completed"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await WaitForDepositStateAsync(client, token, "XOF", "500", "credited");
        var lockDeadline = TimeProvider.System.GetUtcNow().AddSeconds(15);
        while (TimeProvider.System.GetUtcNow() < lockDeadline)
        {
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        var beneficiaryId = await AddActiveBeneficiaryAsync(client, token, "XOF", "Review Test");

        // XOF review threshold is 10: a 50 receive withdrawal goes to review.
        var preview = (await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/funding/withdrawals/previews",
                new { currency = "XOF", receiveAmount = "50", beneficiaryId },
                $"prev-{Guid.CreateVersion7():N}").Auth(token),
            HttpStatusCode.OK)).GetProperty("data");
        var termsHash = preview.GetProperty("termsHash").GetString()!;
        var previewId = preview.GetProperty("id").GetGuid();
        var grant = (await StepUpAsync(client, token, "authorize_withdrawal", termsHash))
            .GetProperty("grantToken").GetString()!;
        var placed = (await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/funding/withdrawals",
                new { previewId, stepUpToken = grant },
                $"wd-{Guid.CreateVersion7():N}").Auth(token),
            HttpStatusCode.OK)).GetProperty("data");
        placed.GetProperty("state").GetString().ShouldBe("under_review");
        var withdrawalId = placed.GetProperty("id").GetGuid();

        // Funds stay held while under review.
        var held = await BalancesAsync(client, token);
        Balance(held, "XOF", "held").ShouldBe(50m);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<IFundingOperator>();
            var approved = await operations.ApproveWithdrawalAsync(
                withdrawalId, TestContext.Current.CancellationToken);
            approved.IsSuccess.ShouldBeTrue(approved.Error?.Message);
        }

        var final = await WaitForWithdrawalStateAsync(client, token, withdrawalId, "completed", 40);
        final.GetProperty("state").GetString().ShouldBe("completed");

        // A second reviewable withdrawal is rejected and the hold releases.
        var preview2 = (await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/funding/withdrawals/previews",
                new { currency = "XOF", receiveAmount = "20", beneficiaryId },
                $"prev-{Guid.CreateVersion7():N}").Auth(token),
            HttpStatusCode.OK)).GetProperty("data");
        var grant2 = (await StepUpAsync(
            client, token, "authorize_withdrawal", preview2.GetProperty("termsHash").GetString()!))
            .GetProperty("grantToken").GetString()!;
        var placed2 = (await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/funding/withdrawals",
                new { previewId = preview2.GetProperty("id").GetGuid(), stepUpToken = grant2 },
                $"wd-{Guid.CreateVersion7():N}").Auth(token),
            HttpStatusCode.OK)).GetProperty("data");
        placed2.GetProperty("state").GetString().ShouldBe("under_review");
        var withdrawal2 = placed2.GetProperty("id").GetGuid();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<IFundingOperator>();
            var rejected = await operations.RejectWithdrawalAsync(
                withdrawal2, TestContext.Current.CancellationToken);
            rejected.IsSuccess.ShouldBeTrue(rejected.Error?.Message);
        }

        var failed = await WaitForWithdrawalStateAsync(client, token, withdrawal2, "failed", 20);
        failed.GetProperty("state").GetString().ShouldBe("failed");
        var after = await BalancesAsync(client, token);
        Balance(after, "XOF", "held").ShouldBe(0m);
    }

    [Fact]
    public async Task Reserves_Pause_Blocks_Withdrawals_Until_Operator_Lifts_It()
    {
        var client = Client();
        var token = await RealReadyUserAsync(client);
        var accountReference = await AccountReferenceAsync(client, token, "XOF");

        var depositId = $"dep-{Guid.CreateVersion7():N}";
        (await SendDepositWebhookAsync(client, depositId, accountReference, "XOF", "400", "completed"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await WaitForDepositStateAsync(client, token, "XOF", "400", "credited");
        await Task.Delay(8000, TestContext.Current.CancellationToken);

        var beneficiaryId = await AddActiveBeneficiaryAsync(client, token, "XOF", "Reserve Test");

        // XOF settlement balance is configured as zero, so two reserves runs
        // record consecutive shortfalls and store the pause.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var reserves = scope.ServiceProvider.GetRequiredService<FundingReservesService>();
            await reserves.RunAsync(TestContext.Current.CancellationToken);
            await reserves.RunAsync(TestContext.Current.CancellationToken);
        }

        var blocked = await EnvelopeAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/funding/withdrawals/previews",
                new { currency = "XOF", receiveAmount = "10", beneficiaryId },
                $"prev-{Guid.CreateVersion7():N}").Auth(token),
            HttpStatusCode.UnprocessableEntity);
        blocked.GetProperty("error").GetProperty("code").GetString().ShouldBe("funding.not_available");

        // Deposits and conversions continue during the pause: a ZAR deposit
        // still credits.
        var zarReference = await AccountReferenceAsync(client, token, "ZAR");
        var zarDeposit = $"dep-{Guid.CreateVersion7():N}";
        (await SendDepositWebhookAsync(client, zarDeposit, zarReference, "ZAR", "50", "completed"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await WaitForDepositStateAsync(client, token, "ZAR", "50", "credited");

        // Operator lifts the pause and withdrawals work again.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<IFundingOperator>();
            var lifted = await operations.ClearPauseAsync("XOF", TestContext.Current.CancellationToken);
            lifted.IsSuccess.ShouldBeTrue(lifted.Error?.Message);
        }

        var preview = await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/funding/withdrawals/previews",
                new { currency = "XOF", receiveAmount = "10", beneficiaryId },
                $"prev-{Guid.CreateVersion7():N}").Auth(token),
            HttpStatusCode.OK);
        preview.GetProperty("success").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Payout_Rejected_And_SubmitTimeout_Scenarios()
    {
        var client = Client();
        var token = await RealReadyUserAsync(client);
        var accountReference = await AccountReferenceAsync(client, token, "ZAR");
        var depositId = $"dep-{Guid.CreateVersion7():N}";
        (await SendDepositWebhookAsync(client, depositId, accountReference, "ZAR", "500", "completed"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await WaitForDepositStateAsync(client, token, "ZAR", "500", "credited");
        await Task.Delay(8000, TestContext.Current.CancellationToken);
        var beneficiaryId = await AddActiveBeneficiaryAsync(client, token, "ZAR", "Scenario Test");

        // Rejected payout: definitive provider refusal releases the hold.
        await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/diagnostics/funding",
                new { target = "funding", scenario = "rejected" },
                $"probe-{Guid.CreateVersion7():N}").Auth(token),
            HttpStatusCode.OK);
        var rejected = await PlaceWithdrawalAsync(client, token, "ZAR", "40", beneficiaryId);
        var rejectedFinal = await WaitForWithdrawalStateAsync(
            client, token, rejected.GetProperty("id").GetGuid(), "failed", 30);
        rejectedFinal.GetProperty("detailCode").GetString().ShouldBe("payout_rejected");
        var balances = await BalancesAsync(client, token);
        Balance(balances, "ZAR", "held").ShouldBe(0m);
        Balance(balances, "ZAR", "available").ShouldBe(500m);

        // Submit timeout but provider filled: needs_reconciliation then completes.
        await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/diagnostics/funding",
                new { target = "funding", scenario = "submit_timeout_filled" },
                $"probe-{Guid.CreateVersion7():N}").Auth(token),
            HttpStatusCode.OK);
        var uncertain = await PlaceWithdrawalAsync(client, token, "ZAR", "30", beneficiaryId);
        var uncertainId = uncertain.GetProperty("id").GetGuid();
        var reconciled = await WaitForWithdrawalStateAsync(
            client, token, uncertainId, "needs_reconciliation", 30);
        reconciled.GetProperty("state").GetString().ShouldBe("needs_reconciliation");

        await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/diagnostics/funding",
                new { target = "funding", scenario = "normal" },
                $"probe-{Guid.CreateVersion7():N}").Auth(token),
            HttpStatusCode.OK);
        var completed = await WaitForWithdrawalStateAsync(
            client, token, uncertainId, "completed", 40);
        completed.GetProperty("state").GetString().ShouldBe("completed");

        balances = await BalancesAsync(client, token);
        Balance(balances, "ZAR", "held").ShouldBe(0m);
    }

    [Fact]
    public async Task Ownership_Eligibility_And_Idempotency_Are_Enforced()
    {
        var client = Client();
        var token = await RealReadyUserAsync(client);
        var otherToken = await RealReadyUserAsync(client);
        var accountReference = await AccountReferenceAsync(client, token, "ZAR");

        // A second user cannot see the first user's funding data.
        var foreign = await EnvelopeAsync(client,
            ComplianceTestUsers.Get("/api/v1/real/funding/accounts").Auth(otherToken),
            HttpStatusCode.OK);
        foreign.GetProperty("data").EnumerateArray().Count().ShouldBe(0);

        // Anonymous calls are rejected.
        var anonymous = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/real/funding/methods"),
            TestContext.Current.CancellationToken);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Deposit id for the first user is invisible to the second.
        var depositId = $"dep-{Guid.CreateVersion7():N}";
        (await SendDepositWebhookAsync(client, depositId, accountReference, "ZAR", "60", "completed"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var deposit = await WaitForDepositStateAsync(client, token, "ZAR", "60", "credited");
        var foreignDeposit = await EnvelopeAsync(client,
            ComplianceTestUsers.Get($"/api/v1/real/funding/deposits/{deposit.GetProperty("id").GetGuid()}").Auth(otherToken),
            HttpStatusCode.NotFound);
        foreignDeposit.GetProperty("error").GetProperty("code").GetString().ShouldBe("resource.not_found");

        // Conversion replay with the same idempotency key returns the same conversion.
        var quote = (await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/funding/conversions/quotes",
                new { pair = "ZAR:USD", sourceAmount = "10" },
                $"quote-{Guid.CreateVersion7():N}").Auth(token),
            HttpStatusCode.OK)).GetProperty("data");
        var quoteId = quote.GetProperty("id").GetGuid();
        var key = $"conv-{Guid.CreateVersion7():N}";
        var first = (await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/funding/conversions",
                new { quoteId }, key).Auth(token),
            HttpStatusCode.OK)).GetProperty("data");
        var replayed = (await DataAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/funding/conversions",
                new { quoteId }, key).Auth(token),
            HttpStatusCode.OK)).GetProperty("data");
        replayed.GetProperty("id").GetGuid().ShouldBe(first.GetProperty("id").GetGuid());

        // A different key on the same quote is refused.
        var second = await EnvelopeAsync(client,
            ComplianceTestUsers.Post("/api/v1/real/funding/conversions",
                new { quoteId }, $"conv-{Guid.CreateVersion7():N}").Auth(token),
            HttpStatusCode.Conflict);
        second.GetProperty("error").GetProperty("code").GetString().ShouldBe("funding.quote_invalid");

        // An unverified user is not real-eligible.
        var unverified = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        var ineligible = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/real/funding/methods").Auth(unverified),
            TestContext.Current.CancellationToken);
        ineligible.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
