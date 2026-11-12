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
public sealed class TradingSmokeTests
{
    private static readonly string? BaseUrl = Environment.GetEnvironmentVariable("ASKVEST_SMOKE_BASE_URL");
    private static readonly string? MailpitUrl = Environment.GetEnvironmentVariable("ASKVEST_SMOKE_MAILPIT_URL");
    private static readonly string? WebhookSecret = Environment.GetEnvironmentVariable("ASKVEST_COMPLIANCE_KYC_WEBHOOK_SECRET");

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

    private static void RequireWebhookSecret()
    {
        if (WebhookSecret is null)
        {
            Assert.Skip("ASKVEST_COMPLIANCE_KYC_WEBHOOK_SECRET is not set; the smoke stack secret must be exported.");
        }
    }

    private static string NewEmail() => $"smoke-t-{Guid.CreateVersion7():N}@example.com";

    private static HttpRequestMessage Request(HttpMethod method, string path, object? body, string? key, string? token)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (method == HttpMethod.Post || method == HttpMethod.Put)
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
            + "\"fullName\":\"Trading Smoke\","
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

    private static async Task<string> IssueStepUpAsync(HttpClient client, string token, string binding)
    {
        var result = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/auth/step-up",
            new { purpose = "authorize_order", pin = Pin, binding }, null, token));
        result.GetProperty("success").GetBoolean().ShouldBeTrue();
        return RequireData(result).GetProperty("grantToken").GetString()!;
    }

    private static async Task<JsonElement> CreateQuoteAsync(
        HttpClient client, string token, Guid tokenId, string side, string? amount = null,
        string? quantity = null, bool? sellAll = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["tokenId"] = tokenId,
            ["side"] = side,
        };
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

        var result = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/real/quotes", body, null, token));
        result.GetProperty("success").GetBoolean().ShouldBeTrue();
        return RequireData(result);
    }

    private static async Task<JsonElement> PlaceOrderAsync(
        HttpClient client, string token, Guid quoteId, string stepUpToken, string? key = null)
    {
        var result = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/real/orders",
            new { quoteId, stepUpToken }, key, token));
        result.GetProperty("success").GetBoolean().ShouldBeTrue();
        return RequireData(result);
    }

    private static async Task<JsonElement> GetOrderAsync(HttpClient client, string token, Guid orderId)
    {
        var result = await SendJsonAsync(client, Request(HttpMethod.Get, $"/api/v1/real/orders/{orderId}", null, null, token));
        result.GetProperty("success").GetBoolean().ShouldBeTrue();
        return RequireData(result);
    }

    private static async Task<JsonElement> WaitForOrderStateAsync(
        HttpClient client, string token, Guid orderId, params string[] states)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(60);
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

    private static async Task SetScenarioAsync(HttpClient client, string token, string scenario)
    {
        var result = await SendJsonAsync(client, Request(HttpMethod.Post,
            "/api/v1/diagnostics/trading-scenario", new { scenario }, null, token));
        result.GetProperty("success").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task RealOrder_BuyThenSell_SettlesExactly()
    {
        RequireWebhookSecret();
        using var client = Client();
        using var mailpit = Mailpit();
        var token = await SignupVerifiedAsync(client, mailpit, NewEmail());
        await MakeRealEligibleAsync(client, token);
        await SetPinAsync(client, token);

        await SendJsonWithStatusAsync(client, Request(HttpMethod.Post,
            "/api/v1/real/diagnostics/ledger-post",
            new { kind = "credit", asset = "USD", amount = "100" }, null, token), HttpStatusCode.OK);

        var tokenId = await SampleTokenIdAsync(client, token, "SNVDA");

        var quote = await CreateQuoteAsync(client, token, tokenId, "buy", amount: "10");
        quote.GetProperty("state").GetString().ShouldBe("open");
        quote.GetProperty("settlementAsset").GetString().ShouldBe("USD");
        var termsHash = quote.GetProperty("termsHash").GetString()!;
        var quoteId = quote.GetProperty("id").GetGuid();
        var quantity = decimal.Parse(quote.GetProperty("quantity").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var total = decimal.Parse(quote.GetProperty("total").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        var balancesBefore = await BalancesAsync(client, token);
        var usdBefore = Amount(balancesBefore, "USD", "available");

        var grant = await IssueStepUpAsync(client, token, termsHash);
        var orderKey = $"ord-{Guid.CreateVersion7():N}";
        var order = await PlaceOrderAsync(client, token, quoteId, grant, orderKey);
        var orderId = order.GetProperty("id").GetGuid();
        order.GetProperty("state").GetString().ShouldBe("pending_submission");

        var replay = await PlaceOrderAsync(client, token, quoteId, grant, orderKey);
        replay.GetProperty("id").GetGuid().ShouldBe(orderId);

        var grant2 = await IssueStepUpAsync(client, token, termsHash);
        var reuse = await PlaceOrderAsync(client, token, quoteId, grant2);
        reuse.GetProperty("id").GetGuid().ShouldBe(orderId);

        var executed = await WaitForOrderStateAsync(client, token, orderId, "executed", "failed", "needs_reconciliation");
        executed.GetProperty("state").GetString().ShouldBe("executed");

        var balancesAfter = await BalancesAsync(client, token);
        Amount(balancesAfter, "USD", "available").ShouldBe(usdBefore - total);
        Amount(balancesAfter, "USD", "held").ShouldBe(0m);
        Amount(balancesAfter, "SMPNVDA1", "available").ShouldBe(quantity);

        var sellQuote = await CreateQuoteAsync(client, token, tokenId, "sell", sellAll: true);
        var sellGrant = await IssueStepUpAsync(client, token, sellQuote.GetProperty("termsHash").GetString()!);
        var sellOrder = await PlaceOrderAsync(client, token, sellQuote.GetProperty("id").GetGuid(), sellGrant);
        var sellExecuted = await WaitForOrderStateAsync(client, token, sellOrder.GetProperty("id").GetGuid(), "executed", "failed", "needs_reconciliation");
        sellExecuted.GetProperty("state").GetString().ShouldBe("executed");

        var finalBalances = await BalancesAsync(client, token);
        Amount(finalBalances, "SMPNVDA1", "available").ShouldBe(0m);
        Amount(finalBalances, "USD", "held").ShouldBe(0m);
        Amount(finalBalances, "SMPNVDA1", "held").ShouldBe(0m);
    }

    [Fact]
    public async Task RealOrder_Rejected_Releases_And_MismatchedFill_KeepsHold()
    {
        RequireWebhookSecret();
        using var client = Client();
        using var mailpit = Mailpit();

        var rejectToken = await SignupVerifiedAsync(client, mailpit, NewEmail());
        await MakeRealEligibleAsync(client, rejectToken);
        await SetPinAsync(client, rejectToken);
        await SendJsonWithStatusAsync(client, Request(HttpMethod.Post,
            "/api/v1/real/diagnostics/ledger-post",
            new { kind = "credit", asset = "USD", amount = "50" }, null, rejectToken), HttpStatusCode.OK);
        var rejectTokenId = await SampleTokenIdAsync(client, rejectToken, "SNVDA");

        await SetScenarioAsync(client, rejectToken, "rejected");
        try
        {
            var rejectQuote = await CreateQuoteAsync(client, rejectToken, rejectTokenId, "buy", amount: "6");
            var rejectGrant = await IssueStepUpAsync(client, rejectToken, rejectQuote.GetProperty("termsHash").GetString()!);
            var rejectOrder = await PlaceOrderAsync(client, rejectToken, rejectQuote.GetProperty("id").GetGuid(), rejectGrant);
            var rejectDone = await WaitForOrderStateAsync(client, rejectToken, rejectOrder.GetProperty("id").GetGuid(), "failed", "executed", "needs_reconciliation");
            rejectDone.GetProperty("state").GetString().ShouldBe("failed");

            var rejectBalances = await BalancesAsync(client, rejectToken);
            Amount(rejectBalances, "USD", "held").ShouldBe(0m);
        }
        finally
        {
            await SetScenarioAsync(client, rejectToken, "normal");
        }

        var reconToken = await SignupVerifiedAsync(client, mailpit, NewEmail());
        await MakeRealEligibleAsync(client, reconToken);
        await SetPinAsync(client, reconToken);
        await SendJsonWithStatusAsync(client, Request(HttpMethod.Post,
            "/api/v1/real/diagnostics/ledger-post",
            new { kind = "credit", asset = "USD", amount = "50" }, null, reconToken), HttpStatusCode.OK);
        var reconTokenId = await SampleTokenIdAsync(client, reconToken, "SNVDA");

        await SetScenarioAsync(client, reconToken, "fill_mismatch");
        try
        {
            var reconQuote = await CreateQuoteAsync(client, reconToken, reconTokenId, "buy", amount: "7");
            var reconGrant = await IssueStepUpAsync(client, reconToken, reconQuote.GetProperty("termsHash").GetString()!);
            var reconOrder = await PlaceOrderAsync(client, reconToken, reconQuote.GetProperty("id").GetGuid(), reconGrant);
            var reconDone = await WaitForOrderStateAsync(client, reconToken, reconOrder.GetProperty("id").GetGuid(), "needs_reconciliation", "executed", "failed");
            reconDone.GetProperty("state").GetString().ShouldBe("needs_reconciliation");

            var reconBalances = await BalancesAsync(client, reconToken);
            Amount(reconBalances, "USD", "held").ShouldBeGreaterThan(0m);
            Amount(reconBalances, "SMPNVDA1", "available").ShouldBe(0m);
        }
        finally
        {
            await SetScenarioAsync(client, reconToken, "normal");
        }
    }

    [Fact]
    public async Task RealOrder_RequiresBoundStepUp_And_HidesOtherUsersQuotes()
    {
        RequireWebhookSecret();
        using var client = Client();
        using var mailpit = Mailpit();
        var token = await SignupVerifiedAsync(client, mailpit, NewEmail());
        await MakeRealEligibleAsync(client, token);
        await SetPinAsync(client, token);
        await SendJsonWithStatusAsync(client, Request(HttpMethod.Post,
            "/api/v1/real/diagnostics/ledger-post",
            new { kind = "credit", asset = "USD", amount = "50" }, null, token), HttpStatusCode.OK);
        var tokenId = await SampleTokenIdAsync(client, token, "SNVDA");

        var quote = await CreateQuoteAsync(client, token, tokenId, "buy", amount: "6");
        var quoteId = quote.GetProperty("id").GetGuid();

        var wrongGrant = await IssueStepUpAsync(client, token, "different-terms");
        var refused = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/real/orders",
            new { quoteId, stepUpToken = wrongGrant }, null, token));
        refused.GetProperty("success").GetBoolean().ShouldBeFalse();
        ErrorCode(refused).ShouldBe("auth.step_up_invalid");

        await SendJsonWithStatusAsync(client,
            Request(HttpMethod.Get, $"/api/v1/real/quotes/{quoteId}", null, null, null),
            HttpStatusCode.Unauthorized);

        var otherToken = await SignupVerifiedAsync(client, mailpit, NewEmail());
        await MakeRealEligibleAsync(client, otherToken);
        var otherGet = await SendJsonWithStatusAsync(client,
            Request(HttpMethod.Get, $"/api/v1/real/quotes/{quoteId}", null, null, otherToken),
            HttpStatusCode.NotFound);
        ErrorCode(otherGet).ShouldBe("resource.not_found");

        var freshToken = await SignupVerifiedAsync(client, mailpit, NewEmail());
        await SendJsonWithStatusAsync(client,
            Request(HttpMethod.Post, "/api/v1/real/quotes",
                new { tokenId, side = "buy", amount = "6" }, null, freshToken),
            HttpStatusCode.Forbidden);
    }
}
