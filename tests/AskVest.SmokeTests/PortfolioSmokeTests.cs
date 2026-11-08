using System.Globalization;
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
public sealed class PortfolioSmokeTests
{
    private static readonly string? BaseUrl = Environment.GetEnvironmentVariable("ASKVEST_SMOKE_BASE_URL");
    private static readonly string? MailpitUrl = Environment.GetEnvironmentVariable("ASKVEST_SMOKE_MAILPIT_URL");
    private static readonly string? WebhookSecret = Environment.GetEnvironmentVariable("ASKVEST_COMPLIANCE_KYC_WEBHOOK_SECRET");

    private const string Password = "CorrectHorseSmoke9";
    private const string PracticeInstrumentId = "sample-nvda";
    private const string RealInstrumentId = "sample-aapl";
    private const string TokenAssetA = "SMPSAPL2";
    private const string TokenAssetB = "SMPSAPL3";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

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

    private static HttpRequestMessage Request(HttpMethod method, string path, object? body, string? token, bool idempotencyKey = true)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (idempotencyKey && (method == HttpMethod.Post || method == HttpMethod.Put))
        {
            request.Headers.Add("Idempotency-Key", $"k-{Guid.CreateVersion7():N}");
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
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            await Task.Delay(TimeSpan.FromSeconds(11), TestContext.Current.CancellationToken);
            using var retry = new HttpRequestMessage(request.Method, request.RequestUri);
            if (request.Content is not null)
            {
                retry.Content = new StringContent(
                    await request.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                    Encoding.UTF8,
                    "application/json");
            }

            foreach (var header in request.Headers)
            {
                retry.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var retried = await client.SendAsync(retry, TestContext.Current.CancellationToken);
            return await retried.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Json)> SendWithStatusAsync(
        HttpClient client, HttpRequestMessage request)
    {
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return (response.StatusCode, json);
    }

    private static JsonElement RequireData(JsonElement json)
    {
        if (!json.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Expected data object, got: {json.GetRawText()}");
        }

        return data;
    }

    private static string ErrorCode(JsonElement json) => json.GetProperty("error").GetProperty("code").GetString()!;

    private static decimal Dec(JsonElement element, string property)
        => decimal.Parse(element.GetProperty(property).GetString()!, Invariant);

    private static decimal Round2(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal Round6(decimal value) => decimal.Round(value, 6, MidpointRounding.AwayFromZero);

    private static string Quantity(decimal value) => value.ToString("0.############################", Invariant);

    private static async Task<string> WaitForCodeAsync(HttpClient mailpit, string to)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(60);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var matches = await mailpit.GetFromJsonAsync<JsonElement>(
                new Uri("/api/v1/messages?limit=50", UriKind.Relative), TestContext.Current.CancellationToken);
            var message = matches.GetProperty("messages").EnumerateArray()
                .Where(m => m.GetProperty("To").EnumerateArray().Any(t => t.GetProperty("Address").GetString() == to))
                .Where(m => m.GetProperty("Subject").GetString()!.Contains("Verify", StringComparison.OrdinalIgnoreCase))
                .LastOrDefault();
            if (message.ValueKind == JsonValueKind.Object)
            {
                var detail = await mailpit.GetFromJsonAsync<JsonElement>(
                    new Uri($"/api/v1/message/{message.GetProperty("ID").GetString()}", UriKind.Relative),
                    TestContext.Current.CancellationToken);
                var match = System.Text.RegularExpressions.Regex.Match(
                    detail.GetProperty("Text").GetString() ?? string.Empty, @"code is (\d{6})");
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"No verify email captured for {to}.");
    }

    private static async Task<string> SignupVerifiedAsync(HttpClient client, HttpClient mailpit, string email)
    {
        var signUp = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/auth/sign-up",
            new { email, password = Password }, null));
        signUp.GetProperty("success").GetBoolean().ShouldBeTrue();
        var verifyCode = await WaitForCodeAsync(mailpit, email);
        var verify = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/auth/verify-email",
            new { email, code = verifyCode }, null));
        verify.GetProperty("success").GetBoolean().ShouldBeTrue();
        var login = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/auth/login", new
        {
            email,
            password = Password,
            device = new { installationId = $"dev-{Guid.CreateVersion7():N}", platform = "android", deviceName = "Smoke", appVersion = "1.0.0" },
        }, null));
        return RequireData(login).GetProperty("accessToken").GetString()!;
    }

    private static async Task MakePracticeEligibleAsync(HttpClient client, string token)
    {
        var docs = await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/compliance/documents", null, token));
        var items = docs.GetProperty("data").GetProperty("documents").EnumerateArray()
            .Where(d => d.GetProperty("requiredFor").GetString() == "practice" && !d.GetProperty("accepted").GetBoolean())
            .Select(d => new { type = d.GetProperty("type").GetString()!, version = d.GetProperty("version").GetString()!, sha256 = d.GetProperty("sha256").GetString()! })
            .ToList();
        items.ShouldNotBeEmpty("expected unaccepted practice documents");
        var accepted = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/compliance/consents", new { documents = items }, token));
        accepted.GetProperty("success").GetBoolean().ShouldBeTrue();
        var country = await SendJsonAsync(client, Request(HttpMethod.Put, "/api/v1/compliance/country", new { country = "NG" }, token));
        country.GetProperty("success").GetBoolean().ShouldBeTrue();
    }

    private static async Task MakeRealEligibleAsync(HttpClient client, string token)
    {
        var docs = await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/compliance/documents", null, token));
        var items = docs.GetProperty("data").GetProperty("documents").EnumerateArray()
            .Where(d => d.GetProperty("requiredFor").GetString() == "real" && !d.GetProperty("accepted").GetBoolean())
            .Select(d => new { type = d.GetProperty("type").GetString()!, version = d.GetProperty("version").GetString()!, sha256 = d.GetProperty("sha256").GetString()! })
            .ToList();
        items.ShouldNotBeEmpty("expected unaccepted real documents");
        var accepted = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/compliance/consents", new { documents = items }, token));
        accepted.GetProperty("success").GetBoolean().ShouldBeTrue();

        var started = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/compliance/kyc/start", null, token));
        started.GetProperty("success").GetBoolean().ShouldBeTrue();
        var reference = RequireData(started).GetProperty("providerReference").GetString()!;

        var body = "{"
            + $"\"eventId\":\"evt-{Guid.CreateVersion7():N}\","
            + $"\"providerReference\":\"{reference}\","
            + "\"outcome\":\"approved\","
            + "\"verifiedCountry\":\"NG\","
            + "\"fullName\":\"Portfolio Smoke\","
            + "\"dateOfBirth\":\"1990-04-12\""
            + "}";
        var timestamp = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds().ToString(Invariant);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(WebhookSecret!));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();
        using var webhook = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/webhooks/kyc/fake", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        webhook.Headers.Add("X-Webhook-Timestamp", timestamp);
        webhook.Headers.Add("X-Webhook-Signature", signature);
        using var webhookResponse = await client.SendAsync(webhook, TestContext.Current.CancellationToken);
        webhookResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        await SmokeLearning.CompleteRequiredLessonsAsync(client, token, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> PortfolioAsync(HttpClient client, string book, string token)
    {
        var json = await SendJsonAsync(client, Request(HttpMethod.Get, $"/api/v1/{book}/portfolio", null, token));
        json.GetProperty("success").GetBoolean().ShouldBeTrue(json.GetRawText());
        return RequireData(json);
    }

    private static async Task<JsonElement> WatchlistAsync(HttpClient client, string token)
    {
        var json = await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/watchlist", null, token));
        json.GetProperty("success").GetBoolean().ShouldBeTrue(json.GetRawText());
        return RequireData(json).GetProperty("items");
    }

    private static async Task ProbeScenarioAsync(HttpClient client, string token, string scenario)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var (status, json) = await SendWithStatusAsync(client, Request(
                HttpMethod.Post, "/api/v1/diagnostics/marketdata-scenario", new { scenario }, token));
            status.ShouldBe(HttpStatusCode.OK);
            if (RequireData(json).GetProperty("polled").GetBoolean())
            {
                return;
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException($"probe cycle never acquired the poll lock for scenario {scenario}");
    }

    private static async Task<JsonElement> OrderAsync(HttpClient client, string token, object body)
    {
        var json = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/practice/orders", body, token));
        json.GetProperty("success").GetBoolean().ShouldBeTrue(json.GetRawText());
        return RequireData(json);
    }

    private static object OrderBody(string side, string type, string? amount = null, string? quantity = null, bool? sellAll = null, string? stopPrice = null)
        => new
        {
            instrumentId = PracticeInstrumentId,
            side,
            type,
            amount,
            quantity,
            sellAll,
            stopPrice,
        };

    private static async Task<JsonElement> AccountAsync(HttpClient client, string token)
    {
        var json = await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/practice/account", null, token));
        return RequireData(json);
    }

    private static async Task ResetWhenAllowedAsync(HttpClient client, string token)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(90);
        while (true)
        {
            var (status, json) = await SendWithStatusAsync(client, Request(
                HttpMethod.Post, "/api/v1/practice/account/reset", new { }, token));
            if (status == HttpStatusCode.OK)
            {
                json.GetProperty("success").GetBoolean().ShouldBeTrue(json.GetRawText());
                return;
            }

            ErrorCode(json).ShouldBe("practice.reset_not_allowed");
            if (TimeProvider.System.GetUtcNow() >= deadline)
            {
                throw new TimeoutException($"Practice reset never became allowed: {json.GetRawText()}");
            }

            var nextResetAt = (await AccountAsync(client, token)).GetProperty("nextResetAt").GetDateTimeOffset();
            var wait = nextResetAt - TimeProvider.System.GetUtcNow() + TimeSpan.FromMilliseconds(500);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, TestContext.Current.CancellationToken);
            }
        }
    }

    private static async Task<decimal> LatestPriceAsync(HttpClient client, string token, string instrumentId)
    {
        var json = await SendJsonAsync(client, Request(
            HttpMethod.Get, $"/api/v1/marketdata/prices?instrumentIds={instrumentId}", null, token));
        var item = json.GetProperty("data").EnumerateArray().Single();
        item.GetProperty("usableForExecution").GetBoolean().ShouldBeTrue(item.GetRawText());
        return Dec(item, "price");
    }

    private static async Task<JsonElement> LedgerProbeAsync(
        HttpClient client, string book, string asset, string amount, string token)
    {
        var json = await SendJsonAsync(client, Request(
            HttpMethod.Post, $"/api/v1/{book}/diagnostics/ledger-post",
            new { kind = "credit", asset, amount }, token));
        json.GetProperty("success").GetBoolean().ShouldBeTrue(json.GetRawText());
        return RequireData(json);
    }

    private static void AssertMoneyIsString(JsonElement element, string property)
        => element.GetProperty(property).ValueKind.ShouldBe(JsonValueKind.String, $"{property} must serialize as a string");

    [Fact]
    public async Task Portfolio_Journey_Practice_Watchlist_Real_And_Gates()
    {
        RequireWebhookSecret();
        using var client = Client();
        using var mailpit = Mailpit();

        var email = $"smoke-v-{Guid.CreateVersion7():N}@example.com";
        var token = await SignupVerifiedAsync(client, mailpit, email);
        await MakePracticeEligibleAsync(client, token);

        try
        {
            // 1. Practice buys at two prices, sells part, portfolio replays it.
            var account = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/practice/account", new { }, token));
            account.GetProperty("success").GetBoolean().ShouldBeTrue(account.GetRawText());

            await ProbeScenarioAsync(client, token, "normal");
            var buy1 = await OrderAsync(client, token, OrderBody("buy", "market", amount: "1000"));
            buy1.GetProperty("state").GetString().ShouldBe("filled");
            var quantity1 = Dec(buy1, "filledQuantity");
            var cash1 = Dec(buy1, "cashAmount");

            await ProbeScenarioAsync(client, token, "drop");
            var buy2 = await OrderAsync(client, token, OrderBody("buy", "market", amount: "500"));
            buy2.GetProperty("state").GetString().ShouldBe("filled");
            var quantity2 = Dec(buy2, "filledQuantity");
            var cash2 = Dec(buy2, "cashAmount");
            var droppedPrice = await LatestPriceAsync(client, token, PracticeInstrumentId);

            var quantityBefore = quantity1 + quantity2;
            var totalCost = cash1 + cash2;
            var sell = await OrderAsync(client, token,
                OrderBody("sell", "market", quantity: Quantity(quantityBefore / 2m)));
            sell.GetProperty("state").GetString().ShouldBe("filled");
            var sold = Dec(sell, "filledQuantity");
            var sellNet = Dec(sell, "cashAmount");

            var removedCost = Round6(totalCost * sold / quantityBefore);
            var remainingCost = totalCost - removedCost;
            var expectedQuantity = quantityBefore - sold;
            var expectedCostBasis = Round2(remainingCost);
            var expectedRealized = Round2(sellNet - removedCost);
            var expectedMarketValue = Round2(expectedQuantity * droppedPrice);
            var expectedUnrealized = Round2(expectedQuantity * droppedPrice - remainingCost);

            var portfolio = await PortfolioAsync(client, "practice", token);
            portfolio.GetProperty("book").GetString().ShouldBe("practice");
            portfolio.GetProperty("valuationCurrency").GetString().ShouldBe("USD");
            var holdings = portfolio.GetProperty("holdings");
            holdings.GetArrayLength().ShouldBe(1);
            var holding = holdings[0];
            holding.GetProperty("instrumentId").GetString().ShouldBe(PracticeInstrumentId);
            Dec(holding, "quantity").ShouldBe(expectedQuantity);
            Dec(holding, "costBasis").ShouldBe(expectedCostBasis);
            Dec(holding, "realizedProfit").ShouldBe(expectedRealized);
            Dec(holding, "marketValue").ShouldBe(expectedMarketValue);
            Dec(holding, "unrealizedProfit").ShouldBe(expectedUnrealized);
            holding.GetProperty("priceLabel").GetString().ShouldBe("live");
            holding.GetProperty("usableForExecution").GetBoolean().ShouldBeTrue();
            AssertMoneyIsString(holding, "quantity");
            AssertMoneyIsString(holding, "costBasis");
            AssertMoneyIsString(holding, "marketValue");
            AssertMoneyIsString(holding, "unrealizedProfit");
            AssertMoneyIsString(holding, "realizedProfit");
            var averageCost = Dec(holding, "averageCostPerUnit");
            Math.Abs(averageCost - remainingCost / expectedQuantity).ShouldBeLessThan(0.000001m);
            holding.GetProperty("tokens").GetArrayLength().ShouldBe(1);
            holding.GetProperty("flags").EnumerateArray()
                .ShouldNotContain(f => f.GetString() == "has_unclassified_movements");

            var cash = 10000m - cash1 - cash2 + sellNet;
            var totals = portfolio.GetProperty("totals");
            Dec(totals, "cash").ShouldBe(Round2(cash));
            Dec(totals, "holdingsValue").ShouldBe(expectedMarketValue);
            Dec(totals, "costBasis").ShouldBe(expectedCostBasis);
            Dec(totals, "unrealized").ShouldBe(expectedUnrealized);
            Dec(totals, "realized").ShouldBe(expectedRealized);
            Dec(totals, "totalValue").ShouldBe(Round2(cash + expectedMarketValue));
            totals.GetProperty("income").GetString()!.ShouldBe("0");

            // 2. A stop holds shares without changing the replayed quantity.
            var stopPrice = (droppedPrice * 0.5m).ToString("0.############################", Invariant);
            var stop = await OrderAsync(client, token,
                OrderBody("sell", "stop_loss", sellAll: true, stopPrice: stopPrice));
            stop.GetProperty("state").GetString().ShouldBe("open");

            var duringHold = await PortfolioAsync(client, "practice", token);
            Dec(duringHold.GetProperty("holdings")[0], "quantity").ShouldBe(expectedQuantity);

            await ResetWhenAllowedAsync(client, token);
            var afterReset = await PortfolioAsync(client, "practice", token);
            afterReset.GetProperty("holdings").GetArrayLength().ShouldBe(0);
            var resetCash = afterReset.GetProperty("cash");
            resetCash.GetArrayLength().ShouldBe(1);
            resetCash[0].GetProperty("asset").GetString().ShouldBe("USD");
            Dec(resetCash[0], "available").ShouldBe(10000m);
            Dec(afterReset.GetProperty("totals"), "cash").ShouldBe(10000m);
            Dec(afterReset.GetProperty("totals"), "totalValue").ShouldBe(10000m);

            // 3. Watchlist: add three, re-add one, remove one, then halt labels.
            foreach (var instrumentId in new[] { PracticeInstrumentId, RealInstrumentId, "sample-global-etf" })
            {
                var added = await SendJsonAsync(client, Request(
                    HttpMethod.Put, $"/api/v1/watchlist/{instrumentId}", null, token, idempotencyKey: false));
                added.GetProperty("success").GetBoolean().ShouldBeTrue(added.GetRawText());
            }

            var readded = await SendJsonAsync(client, Request(
                HttpMethod.Put, $"/api/v1/watchlist/{PracticeInstrumentId}", null, token, idempotencyKey: false));
            readded.GetProperty("success").GetBoolean().ShouldBeTrue(readded.GetRawText());

            var watchlist = await WatchlistAsync(client, token);
            watchlist.GetArrayLength().ShouldBe(3);
            watchlist.EnumerateArray().Select(i => i.GetProperty("instrumentId").GetString()).ToList()
                .ShouldBe([PracticeInstrumentId, RealInstrumentId, "sample-global-etf"]);
            watchlist.EnumerateArray().ShouldAllBe(i => i.GetProperty("label").GetString() != "unavailable");

            var removed = await SendJsonAsync(client, Request(
                HttpMethod.Delete, $"/api/v1/watchlist/{RealInstrumentId}", null, token, idempotencyKey: false));
            removed.GetProperty("success").GetBoolean().ShouldBeTrue(removed.GetRawText());
            var afterRemove = await WatchlistAsync(client, token);
            afterRemove.GetArrayLength().ShouldBe(2);

            await ProbeScenarioAsync(client, token, "halted");
            var haltedList = await WatchlistAsync(client, token);
            haltedList.GetArrayLength().ShouldBe(2);
            haltedList.EnumerateArray().ShouldAllBe(i =>
                i.GetProperty("label").GetString() == "halted"
                && !i.GetProperty("usableForExecution").GetBoolean());
            await ProbeScenarioAsync(client, token, "normal");

            // 4. Real book: two tokens on two chains aggregate into one holding.
            await MakeRealEligibleAsync(client, token);
            await LedgerProbeAsync(client, "real", "USD", "100", token);
            await LedgerProbeAsync(client, "real", TokenAssetA, "10", token);
            await LedgerProbeAsync(client, "real", TokenAssetB, "5", token);

            var realPortfolio = await PortfolioAsync(client, "real", token);
            realPortfolio.GetProperty("book").GetString().ShouldBe("real");
            var realHoldings = realPortfolio.GetProperty("holdings");
            realHoldings.GetArrayLength().ShouldBe(1);
            var realHolding = realHoldings[0];
            realHolding.GetProperty("instrumentId").GetString().ShouldBe(RealInstrumentId);
            Dec(realHolding, "quantity").ShouldBe(15m);
            var tokens = realHolding.GetProperty("tokens");
            tokens.GetArrayLength().ShouldBe(2);
            tokens.EnumerateArray().Select(t => t.GetProperty("chainId").GetString()).ToList()
                .ShouldBe(["eip155:11155111", "eip155:17000"], ignoreOrder: true);
            tokens.EnumerateArray().Select(t => t.GetProperty("assetCode").GetString()).ToList()
                .ShouldBe([TokenAssetA, TokenAssetB], ignoreOrder: true);
            realHolding.GetProperty("flags").EnumerateArray()
                .Select(f => f.GetString()).ShouldContain("has_unclassified_movements");
            var realCash = realPortfolio.GetProperty("cash");
            realCash.GetArrayLength().ShouldBe(1);
            realCash[0].GetProperty("asset").GetString().ShouldBe("USD");
            Dec(realCash[0], "available").ShouldBe(100m);

            // The same user's practice book is untouched by real movements.
            var practiceAfter = await PortfolioAsync(client, "practice", token);
            practiceAfter.GetProperty("holdings").GetArrayLength().ShouldBe(0);
            Dec(practiceAfter.GetProperty("cash")[0], "available").ShouldBe(10000m);

            // 5. Gates and ownership.
            var emailB = $"smoke-v2-{Guid.CreateVersion7():N}@example.com";
            var tokenB = await SignupVerifiedAsync(client, mailpit, emailB);
            await MakePracticeEligibleAsync(client, tokenB);

            var realDenied = await SendWithStatusAsync(client, Request(
                HttpMethod.Get, "/api/v1/real/portfolio", null, tokenB));
            realDenied.Status.ShouldBe(HttpStatusCode.Forbidden);
            ErrorCode(realDenied.Json).ShouldBe("compliance.not_eligible");

            foreach (var path in new[] { "/api/v1/practice/portfolio", "/api/v1/real/portfolio", "/api/v1/watchlist" })
            {
                var anonymous = await SendWithStatusAsync(client, Request(HttpMethod.Get, path, null, null));
                anonymous.Status.ShouldBe(HttpStatusCode.Unauthorized);
            }

            var foreignWatchlist = await WatchlistAsync(client, tokenB);
            foreignWatchlist.GetArrayLength().ShouldBe(0);
            var foreignPortfolio = await PortfolioAsync(client, "practice", tokenB);
            foreignPortfolio.GetProperty("holdings").GetArrayLength().ShouldBe(0);
            foreignPortfolio.GetProperty("cash").GetArrayLength().ShouldBe(0);
        }
        finally
        {
            try
            {
                await ProbeScenarioAsync(client, token, "normal");
            }
            catch
            {
                // Best effort restore; the scenario only affects this stack's fake feed.
            }
        }
    }
}
