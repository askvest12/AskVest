using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace AskVest.SmokeTests;

[Collection("SmokeStack")]
public sealed class WalletsSmokeTests
{
    private static readonly string? BaseUrl = Environment.GetEnvironmentVariable("ASKVEST_SMOKE_BASE_URL");
    private static readonly string? MailpitUrl = Environment.GetEnvironmentVariable("ASKVEST_SMOKE_MAILPIT_URL");
    private static readonly string? WebhookSecret = Environment.GetEnvironmentVariable("ASKVEST_COMPLIANCE_KYC_WEBHOOK_SECRET");

    private const string Password = "CorrectHorseSmoke9";
    private const string Pin = "246810";
    private const string ChainId = "eip155:11155111";
    private const string AssetCode = "SMPNVDA1";

    private static HttpClient Client()
    {
        if (BaseUrl is null)
        {
            Assert.Skip("ASKVEST_SMOKE_BASE_URL is not set; smoke tests require a running stack.");
        }

        return new HttpClient { BaseAddress = new Uri(BaseUrl) };
    }

    private static HttpClient Mailpit()
    {
        if (MailpitUrl is null)
        {
            Assert.Skip("ASKVEST_SMOKE_MAILPIT_URL is not set; smoke tests require Mailpit.");
        }

        return new HttpClient { BaseAddress = new Uri(MailpitUrl) };
    }

    private static void RequireWebhookSecret()
    {
        if (WebhookSecret is null)
        {
            Assert.Skip("ASKVEST_COMPLIANCE_KYC_WEBHOOK_SECRET is not set; the smoke stack secret must be exported.");
        }
    }

    private static string NewEmail() => $"smoke-w-{Guid.CreateVersion7():N}@example.com";

    private static HttpRequestMessage Request(HttpMethod method, string path, object? body, string? key, string? token)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Delete)
        {
            request.Headers.Add("Idempotency-Key", key ?? $"k-{Guid.CreateVersion7():N}");
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static async Task<JsonElement> SendJsonAsync(HttpClient client, HttpRequestMessage request)
    {
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> SendJsonWithStatusAsync(HttpClient client, HttpRequestMessage request, HttpStatusCode expected)
    {
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(expected);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private static JsonElement RequireData(JsonElement json)
    {
        if (!json.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Expected data object, got: {json.GetRawText()}");
        }

        return data;
    }

    private static string? ErrorCode(JsonElement json)
        => json.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
            ? error.GetProperty("code").GetString()
            : null;

    private static async Task<string> WaitForCodeAsync(HttpClient mailpit, string to, string subjectContains)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(60);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var matches = await mailpit.GetFromJsonAsync<JsonElement>(
                new Uri("/api/v1/messages?limit=50", UriKind.Relative), TestContext.Current.CancellationToken);
            var messages = matches.GetProperty("messages").EnumerateArray()
                .Where(m => m.GetProperty("To").EnumerateArray().Any(t => t.GetProperty("Address").GetString() == to))
                .Where(m => m.GetProperty("Subject").GetString()!.Contains(subjectContains, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (messages.Count > 0)
            {
                var latest = messages[^1];
                var detail = await mailpit.GetFromJsonAsync<JsonElement>(
                    new Uri($"/api/v1/message/{latest.GetProperty("ID").GetString()}", UriKind.Relative),
                    TestContext.Current.CancellationToken);
                var text = detail.GetProperty("Text").GetString() ?? string.Empty;
                var match = System.Text.RegularExpressions.Regex.Match(text, @"code is (\d{6})");
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"No '{subjectContains}' email captured for {to}.");
    }

    private static async Task<string> SignupVerifiedAsync(HttpClient client, HttpClient mailpit, string email)
    {
        var signUp = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/auth/sign-up", new { email, password = Password }, null, null));
        signUp.GetProperty("success").GetBoolean().ShouldBeTrue();
        var verifyCode = await WaitForCodeAsync(mailpit, email, "Verify");
        var verify = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/auth/verify-email", new { email, code = verifyCode }, null, null));
        verify.GetProperty("success").GetBoolean().ShouldBeTrue();
        var login = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/auth/login", new
        {
            email,
            password = Password,
            device = new { installationId = $"dev-{Guid.CreateVersion7():N}", platform = "android", deviceName = "Smoke", appVersion = "1.0.0" },
        }, null, null));
        var data = RequireData(login);
        data.GetProperty("result").GetString().ShouldBe("authenticated");
        return data.GetProperty("accessToken").GetString()!;
    }

    private static async Task MakeRealEligibleAsync(HttpClient client, string token)
    {
        var docs = await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/compliance/documents", null, null, token));
        var all = docs.GetProperty("data").GetProperty("documents").EnumerateArray()
            .Where(d => !d.GetProperty("accepted").GetBoolean())
            .Select(d => new
            {
                type = d.GetProperty("type").GetString()!,
                version = d.GetProperty("version").GetString()!,
                sha256 = d.GetProperty("sha256").GetString()!,
            })
            .ToList();
        all.ShouldNotBeEmpty("expected unaccepted documents");
        var accepted = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/compliance/consents",
            new { documents = all }, null, token));
        accepted.GetProperty("success").GetBoolean().ShouldBeTrue();

        var country = await SendJsonAsync(client, Request(HttpMethod.Put, "/api/v1/compliance/country",
            new { country = "NG" }, null, token));
        country.GetProperty("success").GetBoolean().ShouldBeTrue();

        var started = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/compliance/kyc/start", null, null, token));
        started.GetProperty("success").GetBoolean().ShouldBeTrue();
        var reference = RequireData(started).GetProperty("providerReference").GetString()!;

        var body = "{"
            + $"\"eventId\":\"evt-{Guid.CreateVersion7():N}\","
            + $"\"providerReference\":\"{reference}\","
            + "\"outcome\":\"approved\","
            + "\"verifiedCountry\":\"NG\","
            + "\"fullName\":\"Wallets Smoke\","
            + "\"dateOfBirth\":\"1990-04-12\""
            + "}";
        var timestamp = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(WebhookSecret!));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();
        var webhook = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/webhooks/kyc/fake", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        webhook.Headers.Add("X-Webhook-Timestamp", timestamp);
        webhook.Headers.Add("X-Webhook-Signature", signature);
        using var webhookResponse = await client.SendAsync(webhook, TestContext.Current.CancellationToken);
        webhookResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        await SmokeLearning.CompleteRequiredLessonsAsync(client, token, TestContext.Current.CancellationToken);
    }

    private static async Task SetPinAsync(HttpClient client, string token)
    {
        var result = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/account/pin",
            new { password = Password, pin = Pin }, null, token));
        result.GetProperty("success").GetBoolean().ShouldBeTrue();
    }

    private static async Task<string> StepUpAsync(HttpClient client, string token, string purpose, string binding)
    {
        var result = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/auth/step-up",
            new { purpose, pin = Pin, binding }, null, token));
        result.GetProperty("success").GetBoolean().ShouldBeTrue();
        return RequireData(result).GetProperty("grantToken").GetString()!;
    }

    private static async Task<JsonElement> BalancesAsync(HttpClient client, string token)
    {
        var result = await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/real/balances", null, null, token));
        result.GetProperty("success").GetBoolean().ShouldBeTrue();
        return result.GetProperty("data");
    }

    private static decimal Amount(JsonElement balances, string asset, string property)
        => balances.EnumerateArray()
            .Where(b => b.GetProperty("asset").GetString() == asset)
            .Select(b => decimal.Parse(b.GetProperty(property).GetString()!, System.Globalization.CultureInfo.InvariantCulture))
            .FirstOrDefault();

    private static async Task<Guid> SampleTokenIdAsync(HttpClient client, string token, string symbol)
    {
        var detail = await SendJsonAsync(client,
            Request(HttpMethod.Get, "/api/v1/catalog/instruments/sample-nvda", null, null, token));
        var found = RequireData(detail).GetProperty("tokens").EnumerateArray()
            .First(t => t.GetProperty("symbol").GetString() == symbol);
        return found.GetProperty("id").GetGuid();
    }

    private static async Task<string> SampleContractAddressAsync(HttpClient client, string token, Guid tokenId)
    {
        var detail = await SendJsonAsync(client,
            Request(HttpMethod.Get, $"/api/v1/catalog/tokens/{tokenId}", null, null, token));
        return RequireData(detail).GetProperty("contractAddress").GetString()!;
    }

    private static async Task<JsonElement> GetWithdrawalAsync(HttpClient client, string token, Guid withdrawalId)
    {
        var result = await SendJsonAsync(client,
            Request(HttpMethod.Get, $"/api/v1/real/withdrawals/{withdrawalId}", null, null, token));
        result.GetProperty("success").GetBoolean().ShouldBeTrue();
        return RequireData(result);
    }

    private static async Task<JsonElement> WaitForWithdrawalAsync(
        HttpClient client, string token, Guid withdrawalId, params string[] states)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(90);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var withdrawal = await GetWithdrawalAsync(client, token, withdrawalId);
            var state = withdrawal.GetProperty("state").GetString();
            if (states.Contains(state, StringComparer.Ordinal))
            {
                return withdrawal;
            }

            await Task.Delay(700, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Withdrawal {withdrawalId} never reached [{string.Join(',', states)}].");
    }

    private static async Task SetCustodyScenarioAsync(HttpClient client, string token, string scenario)
    {
        var result = await SendJsonAsync(client, Request(HttpMethod.Post,
            "/api/v1/diagnostics/wallets-custody-scenario", new { scenario }, null, token));
        result.GetProperty("success").GetBoolean().ShouldBeTrue();
    }

    private static async Task InjectDepositAsync(
        HttpClient client, string token, string destination, string contract, string quantity)
    {
        var result = await SendJsonWithStatusAsync(client, Request(HttpMethod.Post,
            "/api/v1/diagnostics/wallets-deposit", new
            {
                chainId = ChainId,
                destinationAddress = destination,
                tokenContractAddress = contract,
                quantity,
                senderAddress = "sender-clean-1",
                finality = "final",
            }, null, token), HttpStatusCode.OK);
        RequireData(result).GetProperty("applied").GetBoolean().ShouldBeTrue();
    }

    private static async Task<(Guid AddressId, string TermsHash, Guid PreviewId)> PreviewWithdrawalAsync(
        HttpClient client, string token, Guid tokenId, string destination, string gross)
    {
        var grant = await StepUpAsync(client, token, "change_withdrawal_address", destination);
        var added = RequireData(await SendJsonAsync(client,
            Request(HttpMethod.Post, "/api/v1/real/withdrawal-addresses",
                new { chainId = ChainId, address = destination, label = (string?)null, stepUpToken = grant },
                $"addr-{Guid.CreateVersion7():N}", token)));
        added.GetProperty("state").GetString().ShouldBe("active");
        var addressId = added.GetProperty("id").GetGuid();

        var preview = RequireData(await SendJsonAsync(client,
            Request(HttpMethod.Post, "/api/v1/real/withdrawals/previews",
                new { tokenId, addressId, grossQuantity = gross },
                $"prev-{Guid.CreateVersion7():N}", token)));
        preview.GetProperty("state").GetString().ShouldBe("previewed");
        return (addressId, preview.GetProperty("termsHash").GetString()!, preview.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Deposit_Then_Withdrawal_Completes_And_Replay_Is_Safe()
    {
        RequireWebhookSecret();
        using var client = Client();
        using var mailpit = Mailpit();

        var probe = await SignupVerifiedAsync(client, mailpit, NewEmail());
        await MakeRealEligibleAsync(client, probe);
        await SetPinAsync(client, probe);
        var tokenId = await SampleTokenIdAsync(client, probe, "SNVDA");
        var contract = await SampleContractAddressAsync(client, probe, tokenId);

        var accessToken = await SignupVerifiedAsync(client, mailpit, NewEmail());
        await MakeRealEligibleAsync(client, accessToken);
        await SetPinAsync(client, accessToken);

        var wallet = RequireData(await SendJsonAsync(client,
            Request(HttpMethod.Get, $"/api/v1/real/wallets/{ChainId}", null, null, accessToken)));
        var depositAddress = wallet.GetProperty("address").GetString()!;
        depositAddress.ShouldNotBeNullOrWhiteSpace();

        await InjectDepositAsync(client, accessToken, depositAddress, contract, "5");

        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(60);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var balances = await BalancesAsync(client, accessToken);
            if (Amount(balances, AssetCode, "available") >= 5m)
            {
                break;
            }

            await Task.Delay(700, TestContext.Current.CancellationToken);
        }

        var funded = await BalancesAsync(client, accessToken);
        Amount(funded, AssetCode, "available").ShouldBe(5m);

        var deposits = await SendJsonAsync(client,
            Request(HttpMethod.Get, "/api/v1/real/deposits", null, null, accessToken));
        deposits.GetProperty("data").EnumerateArray()
            .ShouldContain(d => d.GetProperty("state").GetString() == "credited");

        // All-digit hex address: canonical form equals the input, so the
        // step-up binding is the same string.
        var destination = $"0x{new string('3', 40)}";
        var (_, termsHash, previewId) = await PreviewWithdrawalAsync(
            client, accessToken, tokenId, destination, "2");

        var withdrawGrant = await StepUpAsync(client, accessToken, "authorize_withdrawal", termsHash);
        var placeKey = $"wd-{Guid.CreateVersion7():N}";
        var placed = RequireData(await SendJsonAsync(client,
            Request(HttpMethod.Post, "/api/v1/real/withdrawals",
                new { previewId, stepUpToken = withdrawGrant }, placeKey, accessToken)));
        var withdrawalId = placed.GetProperty("id").GetGuid();

        // Replaying the same idempotency key returns the same withdrawal.
        var replay = RequireData(await SendJsonAsync(client,
            Request(HttpMethod.Post, "/api/v1/real/withdrawals",
                new { previewId, stepUpToken = withdrawGrant }, placeKey, accessToken)));
        replay.GetProperty("id").GetGuid().ShouldBe(withdrawalId);

        // Another user sees resource.not_found, never the withdrawal.
        var other = await SignupVerifiedAsync(client, mailpit, NewEmail());
        await MakeRealEligibleAsync(client, other);
        var foreign = await SendJsonWithStatusAsync(client,
            Request(HttpMethod.Get, $"/api/v1/real/withdrawals/{withdrawalId}", null, null, other),
            HttpStatusCode.NotFound);
        ErrorCode(foreign).ShouldBe("resource.not_found");

        var completed = await WaitForWithdrawalAsync(client, accessToken, withdrawalId, "completed", "failed", "needs_reconciliation");
        completed.GetProperty("state").GetString().ShouldBe("completed");

        var final = await BalancesAsync(client, accessToken);
        Amount(final, AssetCode, "available").ShouldBe(3m);
        Amount(final, AssetCode, "held").ShouldBe(0m);
    }

    [Fact]
    public async Task Rejected_Custody_Submission_Releases_The_Hold()
    {
        RequireWebhookSecret();
        using var client = Client();
        using var mailpit = Mailpit();

        var probe = await SignupVerifiedAsync(client, mailpit, NewEmail());
        await MakeRealEligibleAsync(client, probe);
        await SetPinAsync(client, probe);
        var tokenId = await SampleTokenIdAsync(client, probe, "SNVDA");
        var contract = await SampleContractAddressAsync(client, probe, tokenId);

        var accessToken = await SignupVerifiedAsync(client, mailpit, NewEmail());
        await MakeRealEligibleAsync(client, accessToken);
        await SetPinAsync(client, accessToken);

        var wallet = RequireData(await SendJsonAsync(client,
            Request(HttpMethod.Get, $"/api/v1/real/wallets/{ChainId}", null, null, accessToken)));
        await InjectDepositAsync(client, accessToken, wallet.GetProperty("address").GetString()!, contract, "5");

        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(60);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var balances = await BalancesAsync(client, accessToken);
            if (Amount(balances, AssetCode, "available") >= 5m)
            {
                break;
            }

            await Task.Delay(700, TestContext.Current.CancellationToken);
        }

        try
        {
            await SetCustodyScenarioAsync(client, accessToken, "rejected");
            var destination = $"0x{new string('4', 40)}";
            var (_, termsHash, previewId) = await PreviewWithdrawalAsync(
                client, accessToken, tokenId, destination, "2");
            var grant = await StepUpAsync(client, accessToken, "authorize_withdrawal", termsHash);
            var placed = RequireData(await SendJsonAsync(client,
                Request(HttpMethod.Post, "/api/v1/real/withdrawals",
                    new { previewId, stepUpToken = grant }, null, accessToken)));
            var withdrawalId = placed.GetProperty("id").GetGuid();

            var failed = await WaitForWithdrawalAsync(client, accessToken, withdrawalId, "failed", "completed", "needs_reconciliation");
            failed.GetProperty("state").GetString().ShouldBe("failed");

            var final = await BalancesAsync(client, accessToken);
            Amount(final, AssetCode, "available").ShouldBe(5m);
            Amount(final, AssetCode, "held").ShouldBe(0m);
        }
        finally
        {
            await SetCustodyScenarioAsync(client, accessToken, "normal");
        }
    }
}
