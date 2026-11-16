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
public sealed class FundingSmokeTests
{
    private static readonly string? BaseUrl = Environment.GetEnvironmentVariable("ASKVEST_SMOKE_BASE_URL");
    private static readonly string? MailpitUrl = Environment.GetEnvironmentVariable("ASKVEST_SMOKE_MAILPIT_URL");
    private static readonly string? WebhookSecret = Environment.GetEnvironmentVariable("ASKVEST_COMPLIANCE_KYC_WEBHOOK_SECRET");
    private static readonly string? FundingWebhookSecret = Environment.GetEnvironmentVariable("ASKVEST_FUNDING_WEBHOOK_SECRET");

    private const string Password = "CorrectHorseSmoke9";
    private const string Pin = "246810";

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

    private static void RequireSecrets()
    {
        if (WebhookSecret is null)
        {
            Assert.Skip("ASKVEST_COMPLIANCE_KYC_WEBHOOK_SECRET is not set; the smoke stack secret must be exported.");
        }

        if (FundingWebhookSecret is null)
        {
            Assert.Skip("ASKVEST_FUNDING_WEBHOOK_SECRET is not set; the smoke stack secret must be exported.");
        }
    }

    private static string NewEmail() => $"smoke-f-{Guid.CreateVersion7():N}@example.com";

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
            + "\"fullName\":\"Funding Smoke\","
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

    private static async Task<Guid> UserIdAsync(HttpClient client, string token)
    {
        var me = await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/account/me", null, null, token));
        return RequireData(me).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> CreateFundingAccountAsync(HttpClient client, string token, string currency)
    {
        var result = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/real/funding/accounts",
            new { currency }, $"fa-{Guid.CreateVersion7():N}", token));
        result.GetProperty("success").GetBoolean().ShouldBeTrue();
        return RequireData(result);
    }

    private static async Task<HttpStatusCode> SendFundingWebhookAsync(
        HttpClient client, string body, string? signature = null, string? timestamp = null)
    {
        var ts = timestamp ?? TimeProvider.System.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sig = signature ?? Convert.ToHexString(
            HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(FundingWebhookSecret!),
                Encoding.UTF8.GetBytes($"{ts}.{body}"))).ToLowerInvariant();
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/webhooks/funding/fake", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Webhook-Timestamp", ts);
        request.Headers.Add("X-Webhook-Signature", sig);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static string DepositWebhookBody(string eventId, string accountReference, string amount, bool payerVerified = true)
        => "{"
            + $"\"eventId\":\"{eventId}\","
            + "\"kind\":\"deposit\","
            + "\"deposit\":{"
            + $"\"providerDepositId\":\"dep-{eventId}\","
            + $"\"accountReference\":\"{accountReference}\","
            + "\"currency\":\"ZAR\","
            + $"\"amount\":\"{amount}\","
            + "\"providerFee\":\"0\","
            + $"\"payerVerified\":{(payerVerified ? "true" : "false")},"
            + "\"state\":\"completed\","
            + $"\"observedAt\":\"{TimeProvider.System.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture)}\""
            + "}}";

    private static async Task<JsonElement> WaitForDepositStateAsync(
        HttpClient client, string token, string state)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(60);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var deposits = await SendJsonAsync(client,
                Request(HttpMethod.Get, "/api/v1/real/funding/deposits", null, null, token));
            foreach (var deposit in deposits.GetProperty("data").GetProperty("items").EnumerateArray())
            {
                if (deposit.GetProperty("state").GetString() == state)
                {
                    return deposit;
                }
            }

            await Task.Delay(700, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"No funding deposit reached state '{state}'.");
    }

    private static async Task<JsonElement> WaitForWithdrawalStateAsync(
        HttpClient client, string token, Guid withdrawalId, params string[] states)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(90);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var result = await SendJsonAsync(client,
                Request(HttpMethod.Get, $"/api/v1/real/funding/withdrawals/{withdrawalId}", null, null, token));
            var withdrawal = RequireData(result);
            var state = withdrawal.GetProperty("state").GetString();
            if (states.Contains(state, StringComparer.Ordinal))
            {
                return withdrawal;
            }

            await Task.Delay(700, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Funding withdrawal {withdrawalId} never reached [{string.Join(',', states)}].");
    }

    private static async Task<JsonElement> WaitForBeneficiaryStateAsync(
        HttpClient client, string token, Guid beneficiaryId, string state)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(60);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var beneficiaries = await SendJsonAsync(client,
                Request(HttpMethod.Get, "/api/v1/real/funding/beneficiaries", null, null, token));
            var match = beneficiaries.GetProperty("data").EnumerateArray()
                .FirstOrDefault(b => b.GetProperty("id").GetGuid() == beneficiaryId);
            if (match.ValueKind == JsonValueKind.Object
                && match.GetProperty("state").GetString() == state)
            {
                return match;
            }

            await Task.Delay(700, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Beneficiary {beneficiaryId} never reached '{state}'.");
    }

    private static async Task SetFundingScenarioAsync(HttpClient client, string token, string target, string scenario)
    {
        var result = await SendJsonAsync(client, Request(HttpMethod.Post,
            "/api/v1/diagnostics/funding", new { target, scenario }, null, token));
        result.GetProperty("success").GetBoolean().ShouldBeTrue();
    }

    private static string BeneficiaryProviderReference(Guid userId, string currency, IReadOnlyDictionary<string, string> details)
    {
        var joined = string.Join('|', details.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value));
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"fake|{userId:D}|{currency}|{joined}")))
            .ToLowerInvariant();
        return $"fpb-{hash[..24]}";
    }

    private static async Task<(Guid BeneficiaryId, JsonElement Preview)> AddActiveBeneficiaryAndPreviewAsync(
        HttpClient client, string token, Guid userId, string receiveAmount, string accountNumber)
    {
        var details = new Dictionary<string, string>
        {
            ["accountName"] = "Funding Smoke",
            ["accountNumber"] = accountNumber,
            ["bankName"] = "Smoke Bank",
        };
        var providerReference = BeneficiaryProviderReference(userId, "ZAR", details);
        var grant = await StepUpAsync(client, token, "change_withdrawal_address", $"ZAR:{providerReference}");
        var added = RequireData(await SendJsonAsync(client,
            Request(HttpMethod.Post, "/api/v1/real/funding/beneficiaries",
                new { currency = "ZAR", details, stepUpToken = grant },
                $"ben-{Guid.CreateVersion7():N}", token)));
        added.GetProperty("state").GetString().ShouldBe("cooling_off");
        var beneficiaryId = added.GetProperty("id").GetGuid();
        await WaitForBeneficiaryStateAsync(client, token, beneficiaryId, "active");

        // The fresh-deposit lock may still be closing; retry the preview until it opens.
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(45);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var previewResult = await SendJsonAsync(client,
                Request(HttpMethod.Post, "/api/v1/real/funding/withdrawals/previews",
                    new { currency = "ZAR", receiveAmount, beneficiaryId },
                    $"wprev-{Guid.CreateVersion7():N}", token));
            if (previewResult.GetProperty("success").GetBoolean())
            {
                var preview = RequireData(previewResult);
                preview.GetProperty("state").GetString().ShouldBe("previewed");
                return (beneficiaryId, preview);
            }

            ErrorCode(previewResult).ShouldBe("funding.limit_exceeded");
            await Task.Delay(1500, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("Withdrawal preview never succeeded; fresh-deposit lock did not open.");
    }

    private static async Task<JsonElement> PlaceWithdrawalAsync(
        HttpClient client, string token, JsonElement preview)
    {
        var grant = await StepUpAsync(
            client, token, "authorize_withdrawal", preview.GetProperty("termsHash").GetString()!);
        var placed = RequireData(await SendJsonAsync(client,
            Request(HttpMethod.Post, "/api/v1/real/funding/withdrawals",
                new { previewId = preview.GetProperty("id").GetGuid(), stepUpToken = grant },
                $"wplc-{Guid.CreateVersion7():N}", token)));
        placed.GetProperty("state").GetString().ShouldBe("requested");
        return placed;
    }

    [Fact]
    public async Task Funding_Journey_Deposit_Conversion_Beneficiary_Withdrawal_Completes()
    {
        RequireSecrets();
        using var client = Client();
        using var mailpit = Mailpit();

        var token = await SignupVerifiedAsync(client, mailpit, NewEmail());
        await MakeRealEligibleAsync(client, token);
        await SetPinAsync(client, token);
        var userId = await UserIdAsync(client, token);

        // Scenario state is global to the provider fakes; a previous failed
        // run can leave it armed, so normalize before starting.
        await SetFundingScenarioAsync(client, token, "funding", "normal");
        await SetFundingScenarioAsync(client, token, "fx", "normal");

        var account = await CreateFundingAccountAsync(client, token, "ZAR");
        var accountReference = account.GetProperty("instruction").GetProperty("reference").GetString()!;
        accountReference.ShouldStartWith("fpi-");

        // Signed deposit webhook credits the account.
        var depositEvent = $"evt-{Guid.CreateVersion7():N}";
        var depositBody = DepositWebhookBody(depositEvent, accountReference, "500");
        (await SendFundingWebhookAsync(client, depositBody)).ShouldBe(HttpStatusCode.OK);

        // Replaying the same event id is deduplicated: still credited once.
        (await SendFundingWebhookAsync(client, depositBody)).ShouldBe(HttpStatusCode.OK);

        await WaitForDepositStateAsync(client, token, "credited");

        var funded = await BalancesAsync(client, token);
        Amount(funded, "ZAR", "available").ShouldBe(500m);

        // Internal conversion ZAR -> USD at the configured pair.
        var quote = RequireData(await SendJsonAsync(client,
            Request(HttpMethod.Post, "/api/v1/real/funding/conversions/quotes",
                new { pair = "ZAR:USD", sourceAmount = "200" },
                $"convq-{Guid.CreateVersion7():N}", token)));
        quote.GetProperty("state").GetString().ShouldBe("quoted");
        var quoteId = quote.GetProperty("id").GetGuid();

        var conversion = RequireData(await SendJsonAsync(client,
            Request(HttpMethod.Post, "/api/v1/real/funding/conversions",
                new { quoteId }, $"conve-{Guid.CreateVersion7():N}", token)));
        conversion.GetProperty("state").GetString().ShouldBe("completed");

        var converted = await BalancesAsync(client, token);
        Amount(converted, "ZAR", "available").ShouldBe(300m);
        Amount(converted, "USD", "available").ShouldBeGreaterThan(0m);

        // Beneficiary add is step-up bound to currency + provider reference.
        var (beneficiaryId, preview) = await AddActiveBeneficiaryAndPreviewAsync(
            client, token, userId, "100", "9988776655");
        beneficiaryId.ShouldNotBe(Guid.Empty);

        var placed = await PlaceWithdrawalAsync(client, token, preview);
        var withdrawalId = placed.GetProperty("id").GetGuid();

        // needs_reconciliation is transient: the progress job resolves it.
        var completed = await WaitForWithdrawalStateAsync(
            client, token, withdrawalId, "completed", "failed");
        completed.GetProperty("state").GetString().ShouldBe("completed");

        var final = await BalancesAsync(client, token);
        var debited = decimal.Parse(preview.GetProperty("totalDebited").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Amount(final, "ZAR", "available").ShouldBe(300m - debited);
        Amount(final, "ZAR", "held").ShouldBe(0m);
    }

    [Fact]
    public async Task Funding_Webhook_Negatives_And_Payout_Drills()
    {
        RequireSecrets();
        using var client = Client();
        using var mailpit = Mailpit();

        var token = await SignupVerifiedAsync(client, mailpit, NewEmail());
        await MakeRealEligibleAsync(client, token);
        await SetPinAsync(client, token);
        var userId = await UserIdAsync(client, token);

        await SetFundingScenarioAsync(client, token, "funding", "normal");
        await SetFundingScenarioAsync(client, token, "fx", "normal");

        var account = await CreateFundingAccountAsync(client, token, "ZAR");
        var accountReference = account.GetProperty("instruction").GetProperty("reference").GetString()!;

        // Webhook negatives: bad signature, stale timestamp, unknown provider.
        var badBody = DepositWebhookBody($"evt-{Guid.CreateVersion7():N}", accountReference, "10");
        (await SendFundingWebhookAsync(client, badBody, signature: "deadbeef")).ShouldNotBe(HttpStatusCode.OK);
        var staleTs = (TimeProvider.System.GetUtcNow().AddHours(-2)).ToUnixTimeSeconds()
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        (await SendFundingWebhookAsync(client, badBody, timestamp: staleTs)).ShouldNotBe(HttpStatusCode.OK);
        var unknownProvider = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/webhooks/funding/nope", UriKind.Relative))
        {
            Content = new StringContent(badBody, Encoding.UTF8, "application/json"),
        };
        unknownProvider.Headers.Add("X-Webhook-Timestamp",
            TimeProvider.System.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        unknownProvider.Headers.Add("X-Webhook-Signature", "deadbeef");
        using (var unknownResponse = await client.SendAsync(unknownProvider, TestContext.Current.CancellationToken))
        {
            unknownResponse.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        }

        // Unknown account reference: accepted but ignored, never credited.
        var ignoredBody = DepositWebhookBody($"evt-{Guid.CreateVersion7():N}", "fpi-doesnotexist", "75");
        (await SendFundingWebhookAsync(client, ignoredBody)).ShouldBe(HttpStatusCode.OK);

        // A real deposit credits once; replaying the event does not double it.
        var depositEvent = $"evt-{Guid.CreateVersion7():N}";
        var depositBody = DepositWebhookBody(depositEvent, accountReference, "400");
        (await SendFundingWebhookAsync(client, depositBody)).ShouldBe(HttpStatusCode.OK);
        (await SendFundingWebhookAsync(client, depositBody)).ShouldBe(HttpStatusCode.OK);
        await WaitForDepositStateAsync(client, token, "credited");

        var funded = await BalancesAsync(client, token);
        Amount(funded, "ZAR", "available").ShouldBe(400m);

        // Payout drill 1: provider rejects the payout; the hold is released.
        await SetFundingScenarioAsync(client, token, "funding", "rejected");
        var (_, rejectedPreview) = await AddActiveBeneficiaryAndPreviewAsync(
            client, token, userId, "50", "9988776655");
        var rejected = await PlaceWithdrawalAsync(client, token, rejectedPreview);
        var rejectedFinal = await WaitForWithdrawalStateAsync(
            client, token, rejected.GetProperty("id").GetGuid(), "failed", "completed");
        rejectedFinal.GetProperty("state").GetString().ShouldBe("failed");
        await SetFundingScenarioAsync(client, token, "funding", "normal");

        var afterReject = await BalancesAsync(client, token);
        Amount(afterReject, "ZAR", "held").ShouldBe(0m);

        // Payout drill 2: submit timeout where the provider actually filled.
        // The withdrawal parks in needs_reconciliation and the progress job
        // resolves it to completed once the provider reports the fill.
        await SetFundingScenarioAsync(client, token, "funding", "submit_timeout_filled");
        var (_, timeoutPreview) = await AddActiveBeneficiaryAndPreviewAsync(
            client, token, userId, "40", "1122334455");
        var timedOut = await PlaceWithdrawalAsync(client, token, timeoutPreview);
        var timedOutFinal = await WaitForWithdrawalStateAsync(
            client, token, timedOut.GetProperty("id").GetGuid(), "completed", "failed");
        timedOutFinal.GetProperty("state").GetString().ShouldBe("completed");
        await SetFundingScenarioAsync(client, token, "funding", "normal");

        // FX drill: a stale rate refuses the quote.
        await SetFundingScenarioAsync(client, token, "fx", "stale");
        var fxQuote = await SendJsonAsync(client,
            Request(HttpMethod.Post, "/api/v1/real/funding/conversions/quotes",
                new { pair = "ZAR:USD", sourceAmount = "10" },
                $"convq-{Guid.CreateVersion7():N}", token));
        ErrorCode(fxQuote).ShouldBe("funding.quote_unavailable");
        await SetFundingScenarioAsync(client, token, "fx", "normal");

        // Beneficiary drill: name mismatch refuses the add.
        var mismatchDetails = new Dictionary<string, string>
        {
            ["accountName"] = "Different Name",
            ["accountNumber"] = "111222333",
            ["bankName"] = "Smoke Bank",
        };
        var mismatchRef = BeneficiaryProviderReference(userId, "ZAR", mismatchDetails);
        var mismatchGrant = await StepUpAsync(client, token, "change_withdrawal_address", $"ZAR:{mismatchRef}");
        await SetFundingScenarioAsync(client, token, "funding", "name_mismatch");
        var mismatch = await SendJsonAsync(client,
            Request(HttpMethod.Post, "/api/v1/real/funding/beneficiaries",
                new { currency = "ZAR", details = mismatchDetails, stepUpToken = mismatchGrant },
                $"ben-{Guid.CreateVersion7():N}", token));
        ErrorCode(mismatch).ShouldBe("funding.beneficiary_invalid");
        await SetFundingScenarioAsync(client, token, "funding", "normal");
    }
}
