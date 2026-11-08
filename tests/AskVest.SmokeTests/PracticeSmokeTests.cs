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
public sealed class PracticeSmokeTests
{
    private static readonly string? BaseUrl = Environment.GetEnvironmentVariable("ASKVEST_SMOKE_BASE_URL");
    private static readonly string? MailpitUrl = Environment.GetEnvironmentVariable("ASKVEST_SMOKE_MAILPIT_URL");
    private static readonly string? WebhookSecret = Environment.GetEnvironmentVariable("ASKVEST_COMPLIANCE_KYC_WEBHOOK_SECRET");

    private const string Password = "CorrectHorseSmoke9";
    private const string InstrumentId = "sample-nvda";
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

    private static HttpRequestMessage Request(HttpMethod method, string path, object? body, string? token, string? key = null)
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
            + "\"fullName\":\"Practice Smoke\","
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
    }

    private static async Task<JsonElement> AccountAsync(HttpClient client, string token)
    {
        var json = await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/practice/account", null, token));
        return RequireData(json);
    }

    private static async Task<decimal> CashAsync(HttpClient client, string token)
        => decimal.Parse((await AccountAsync(client, token)).GetProperty("cash").GetString()!, Invariant);

    private static async Task<JsonElement> PositionsAsync(HttpClient client, string token)
    {
        var json = await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/practice/positions", null, token));
        return RequireData(json);
    }

    private static HttpRequestMessage OrderRequest(string body, string? token, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/practice/orders", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", key ?? $"k-{Guid.CreateVersion7():N}");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static async Task<JsonElement> PlaceOrderAsync(
        HttpClient client, string token, string body, string? key = null)
    {
        var json = await SendJsonAsync(client, OrderRequest(body, token, key));
        json.GetProperty("success").GetBoolean().ShouldBeTrue(json.GetRawText());
        return RequireData(json);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Json)> PlaceOrderWithStatusAsync(
        HttpClient client, string token, string body, string? key = null)
        => await SendWithStatusAsync(client, OrderRequest(body, token, key));

    private static async Task<JsonElement> GetOrderAsync(HttpClient client, string token, string orderId)
    {
        var json = await SendJsonAsync(client, Request(HttpMethod.Get, $"/api/v1/practice/orders/{orderId}", null, token));
        return RequireData(json);
    }

    private static async Task<JsonElement> ResetWhenAllowedAsync(HttpClient client, string token)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(90);
        while (true)
        {
            var (status, json) = await SendWithStatusAsync(client, Request(
                HttpMethod.Post, "/api/v1/practice/account/reset", new { }, token));
            if (status == HttpStatusCode.OK)
            {
                json.GetProperty("success").GetBoolean().ShouldBeTrue(json.GetRawText());
                return json;
            }

            ErrorCode(json).ShouldBe("practice.reset_not_allowed");
            if (TimeProvider.System.GetUtcNow() >= deadline)
            {
                throw new TimeoutException($"Practice reset never became allowed: {json.GetRawText()}");
            }

            var account = await AccountAsync(client, token);
            var nextResetAt = account.GetProperty("nextResetAt").GetDateTimeOffset();
            var wait = nextResetAt - TimeProvider.System.GetUtcNow() + TimeSpan.FromMilliseconds(500);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, TestContext.Current.CancellationToken);
            }
        }
    }

    private static async Task<JsonElement> WaitForOrderStateAsync(
        HttpClient client, string token, string orderId, string state)
    {
        JsonElement order = default;
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(30);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            order = await GetOrderAsync(client, token, orderId);
            if (order.GetProperty("state").GetString() == state)
            {
                return order;
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Order {orderId} never reached state {state}; last: {order.GetRawText()}");
    }

    private static async Task<JsonElement> ProbeScenarioAsync(HttpClient client, string token, string scenario)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var (status, json) = await SendWithStatusAsync(client, Request(
                HttpMethod.Post, "/api/v1/diagnostics/marketdata-scenario", new { scenario }, token));
            status.ShouldBe(HttpStatusCode.OK);
            if (RequireData(json).GetProperty("polled").GetBoolean())
            {
                return json;
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException($"probe cycle never acquired the poll lock for scenario {scenario}");
    }

    private static async Task<decimal> LatestPriceAsync(HttpClient client, string token)
    {
        var json = await SendJsonAsync(client, Request(
            HttpMethod.Get, $"/api/v1/marketdata/prices?instrumentIds={InstrumentId}", null, token));
        var item = json.GetProperty("data").EnumerateArray().Single();
        item.GetProperty("usableForExecution").GetBoolean().ShouldBeTrue(item.GetRawText());
        return decimal.Parse(item.GetProperty("price").GetString()!, Invariant);
    }

    private static decimal Round6(decimal value) => decimal.Round(value, 6, MidpointRounding.AwayFromZero);

    private static string OrderBody(string side, string type, string? amount = null, string? quantity = null, bool? sellAll = null, string? stopPrice = null)
        => JsonSerializer.Serialize(new
        {
            instrumentId = InstrumentId,
            side,
            type,
            amount,
            quantity,
            sellAll,
            stopPrice,
        });

    private static async Task<JsonElement> BuyAsync(HttpClient client, string token, string amount, string? key = null)
        => await PlaceOrderAsync(client, token, OrderBody("buy", "market", amount: amount), key);

    [Fact]
    public async Task Practice_Journey_Account_Orders_Stops_Reset_And_Isolation()
    {
        RequireWebhookSecret();
        using var client = Client();
        using var mailpit = Mailpit();

        var email = $"smoke-p-{Guid.CreateVersion7():N}@example.com";
        var token = await SignupVerifiedAsync(client, mailpit, email);
        await MakePracticeEligibleAsync(client, token);

        try
        {
            // 1. Starting the account twice grants the credits exactly once.
            var first = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/practice/account", new { }, token));
            first.GetProperty("success").GetBoolean().ShouldBeTrue(first.GetRawText());
            RequireData(first).GetProperty("cash").GetString().ShouldBe("10000");
            var second = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/practice/account", new { }, token));
            RequireData(second).GetProperty("cash").GetString().ShouldBe("10000");

            var bookBalances = await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/practice/balances", null, token));
            var usd = bookBalances.GetProperty("data").EnumerateArray()
                .Single(b => b.GetProperty("asset").GetString() == "USD");
            usd.GetProperty("available").GetString().ShouldBe("10000");

            // Wait for a usable price before trading.
            var deadline = TimeProvider.System.GetUtcNow().AddSeconds(30);
            while (TimeProvider.System.GetUtcNow() < deadline)
            {
                var probe = await SendJsonAsync(client, Request(
                    HttpMethod.Get, $"/api/v1/marketdata/prices?instrumentIds={InstrumentId}", null, token));
                var item = probe.GetProperty("data").EnumerateArray().Single();
                if (item.GetProperty("usableForExecution").GetBoolean())
                {
                    break;
                }

                await Task.Delay(1000, TestContext.Current.CancellationToken);
            }

            // 2. Buy 1000 USD, inspect positions, sell half, sell all, check history.
            var cashBefore = await CashAsync(client, token);
            var buyKey = $"smoke-buy-{Guid.CreateVersion7():N}";
            var buy = await BuyAsync(client, token, "1000", buyKey);
            buy.GetProperty("state").GetString().ShouldBe("filled");
            buy.GetProperty("side").GetString().ShouldBe("buy");
            var buyId = buy.GetProperty("id").GetGuid();
            var fillPrice = decimal.Parse(buy.GetProperty("fillPrice").GetString()!, Invariant);
            var filledQty = decimal.Parse(buy.GetProperty("filledQuantity").GetString()!, Invariant);
            var cost = Round6(filledQty * fillPrice);
            var expectedFee = Round6(cost * 50m / 10000m);
            decimal.Parse(buy.GetProperty("fee").GetString()!, Invariant).ShouldBe(expectedFee);
            decimal.Parse(buy.GetProperty("cashAmount").GetString()!, Invariant).ShouldBe(cost + expectedFee);

            var positions = await PositionsAsync(client, token);
            positions.GetProperty("cash").GetString().ShouldBe(
                (cashBefore - cost - expectedFee).ToString("0.############################", Invariant));
            var position = positions.GetProperty("items").EnumerateArray().Single();
            position.GetProperty("instrumentId").GetString().ShouldBe(InstrumentId);
            position.GetProperty("availableQuantity").GetString()!.ShouldNotBeNullOrWhiteSpace();
            position.GetProperty("priceLabel").GetString().ShouldBe("live");
            position.GetProperty("marketValue").GetString()!.ShouldNotBeNullOrWhiteSpace();
            var holding = decimal.Parse(position.GetProperty("availableQuantity").GetString()!, Invariant);

            var halfQty = (holding / 2m).ToString("0.############################", Invariant);
            var halfSell = await PlaceOrderAsync(client, token,
                OrderBody("sell", "market", quantity: halfQty));
            halfSell.GetProperty("state").GetString().ShouldBe("filled");

            var sellAll = await PlaceOrderAsync(client, token,
                OrderBody("sell", "market", sellAll: true));
            sellAll.GetProperty("state").GetString().ShouldBe("filled");

            var history = await SendJsonAsync(client, Request(
                HttpMethod.Get, "/api/v1/practice/orders?limit=10", null, token));
            var historyItems = RequireData(history).GetProperty("items").EnumerateArray().ToList();
            historyItems.Count.ShouldBe(3);
            historyItems.Select(i => i.GetProperty("id").GetGuid())
                .ShouldBe([sellAll.GetProperty("id").GetGuid(), halfSell.GetProperty("id").GetGuid(), buyId]);

            // 3. Replaying the buy key returns the same order and adds nothing.
            var replay = await PlaceOrderAsync(client, token,
                OrderBody("buy", "market", amount: "1000"), buyKey);
            replay.GetProperty("id").GetGuid().ShouldBe(buyId);
            var historyAfterReplay = await SendJsonAsync(client, Request(
                HttpMethod.Get, "/api/v1/practice/orders?limit=10", null, token));
            RequireData(historyAfterReplay).GetProperty("items").EnumerateArray().Count().ShouldBe(3);

            // 4. Stop-loss holds shares; cancel releases them.
            var price = await LatestPriceAsync(client, token);
            var stopPrice = (price * 0.95m).ToString("0.############################", Invariant);
            await BuyAsync(client, token, "1000");
            var heldPositions = await PositionsAsync(client, token);
            var heldItem = heldPositions.GetProperty("items").EnumerateArray().Single();
            var heldQty = decimal.Parse(heldItem.GetProperty("availableQuantity").GetString()!, Invariant);

            var stop = await PlaceOrderAsync(client, token,
                OrderBody("sell", "stop_loss", sellAll: true, stopPrice: stopPrice));
            stop.GetProperty("state").GetString().ShouldBe("open");
            var stopId = stop.GetProperty("id").GetGuid();

            var afterHold = await PositionsAsync(client, token);
            var heldRow = afterHold.GetProperty("items").EnumerateArray().Single();
            decimal.Parse(heldRow.GetProperty("availableQuantity").GetString()!, Invariant).ShouldBe(0m);
            decimal.Parse(heldRow.GetProperty("heldQuantity").GetString()!, Invariant).ShouldBe(heldQty);

            var cancel = await SendJsonAsync(client, Request(
                HttpMethod.Post, $"/api/v1/practice/orders/{stopId}/cancel", new { }, token));
            cancel.GetProperty("success").GetBoolean().ShouldBeTrue(cancel.GetRawText());
            RequireData(cancel).GetProperty("state").GetString().ShouldBe("cancelled");

            var afterCancel = await PositionsAsync(client, token);
            var released = afterCancel.GetProperty("items").EnumerateArray().Single();
            decimal.Parse(released.GetProperty("availableQuantity").GetString()!, Invariant).ShouldBe(heldQty);
            decimal.Parse(released.GetProperty("heldQuantity").GetString()!, Invariant).ShouldBe(0m);

            // Cancelling twice conflicts.
            var cancelAgain = await SendWithStatusAsync(client, Request(
                HttpMethod.Post, $"/api/v1/practice/orders/{stopId}/cancel", new { }, token));
            cancelAgain.Status.ShouldBe(HttpStatusCode.Conflict);

            // 5. A drop triggers the stop at the current price, below the stop.
            var cashBeforeTrigger = await CashAsync(client, token);
            var stop2 = await PlaceOrderAsync(client, token,
                OrderBody("sell", "stop_loss", sellAll: true, stopPrice: stopPrice));
            var stop2Id = stop2.GetProperty("id").GetGuid().ToString();
            await ProbeScenarioAsync(client, token, "drop");
            var triggered = await WaitForOrderStateAsync(client, token, stop2Id, "filled");
            decimal.Parse(triggered.GetProperty("fillPrice").GetString()!, Invariant)
                .ShouldBeLessThan(decimal.Parse(stopPrice, Invariant));
            decimal.Parse(triggered.GetProperty("cashAmount").GetString()!, Invariant).ShouldBeGreaterThan(0m);
            (await CashAsync(client, token)).ShouldBeGreaterThan(cashBeforeTrigger);
            (await PositionsAsync(client, token)).GetProperty("items").EnumerateArray().ShouldBeEmpty();
            await ProbeScenarioAsync(client, token, "normal");

            // 6. A halted market refuses buys and names the label.
            await ProbeScenarioAsync(client, token, "halted");
            var haltedBuy = await PlaceOrderWithStatusAsync(client, token, OrderBody("buy", "market", amount: "100"));
            haltedBuy.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
            haltedBuy.Json.GetProperty("success").GetBoolean().ShouldBeFalse();
            ErrorCode(haltedBuy.Json).ShouldBe("practice.market_unavailable");
            haltedBuy.Json.GetProperty("error").GetProperty("details").EnumerateArray()
                .ShouldContain(d => d.GetProperty("field").GetString() == "label"
                    && d.GetProperty("message").GetString() == "halted");
            await ProbeScenarioAsync(client, token, "normal");

            // 7. Limit and validity errors.
            var tooMuch = await PlaceOrderWithStatusAsync(client, token, OrderBody("buy", "market", amount: "100000"));
            tooMuch.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
            ErrorCode(tooMuch.Json).ShouldBe("ledger.insufficient_funds");

            var oversell = await PlaceOrderWithStatusAsync(client, token, OrderBody("sell", "market", quantity: "999999"));
            oversell.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
            ErrorCode(oversell.Json).ShouldBe("practice.insufficient_position");

            await BuyAsync(client, token, "500");
            var currentPrice = await LatestPriceAsync(client, token);
            var highStop = (currentPrice * 1.10m).ToString("0.############################", Invariant);
            var badStop = await PlaceOrderWithStatusAsync(client, token,
                OrderBody("sell", "stop_loss", sellAll: true, stopPrice: highStop));
            badStop.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
            ErrorCode(badStop.Json).ShouldBe("practice.order_invalid");

            // 8. Twenty parallel buys never overdraw the cash balance.
            var cashBeforeBurst = await CashAsync(client, token);
            var burstTasks = Enumerable.Range(0, 20).Select(i =>
                PlaceOrderWithStatusAsync(client, token, OrderBody("buy", "market", amount: "1000"))).ToList();
            var burst = await Task.WhenAll(burstTasks);
            var spent = 0m;
            var filledCount = 0;
            foreach (var (status, json) in burst)
            {
                if (status == HttpStatusCode.OK)
                {
                    filledCount++;
                    spent += decimal.Parse(RequireData(json).GetProperty("cashAmount").GetString()!, Invariant);
                }
                else
                {
                    status.ShouldBe(HttpStatusCode.UnprocessableEntity);
                    ErrorCode(json).ShouldBe("ledger.insufficient_funds");
                }
            }

            filledCount.ShouldBeGreaterThan(0);
            filledCount.ShouldBeLessThan(20);
            var cashAfterBurst = await CashAsync(client, token);
            cashAfterBurst.ShouldBe(cashBeforeBurst - spent);
            cashAfterBurst.ShouldBeGreaterThanOrEqualTo(0m);

            // 9. Reset returns credits and blocks an immediate second reset. The
            // smoke cooldown is small but nonzero, so wait out nextResetAt if needed.
            var reset = await ResetWhenAllowedAsync(client, token);
            reset.GetProperty("success").GetBoolean().ShouldBeTrue(reset.GetRawText());
            var afterReset = RequireData(reset);
            afterReset.GetProperty("cash").GetString().ShouldBe("10000");
            afterReset.GetProperty("resetCount").GetInt32().ShouldBe(1);
            (await PositionsAsync(client, token)).GetProperty("items").EnumerateArray().ShouldBeEmpty();

            var bookAfterReset = await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/practice/balances", null, token));
            var usdAfter = bookAfterReset.GetProperty("data").EnumerateArray()
                .Single(b => b.GetProperty("asset").GetString() == "USD");
            usdAfter.GetProperty("available").GetString().ShouldBe("10000");

            var secondReset = await SendWithStatusAsync(client, Request(
                HttpMethod.Post, "/api/v1/practice/account/reset", new { }, token));
            secondReset.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
            ErrorCode(secondReset.Json).ShouldBe("practice.reset_not_allowed");

            // 10. Isolation: another user sees nothing; practice never reaches real.
            var emailB = $"smoke-p2-{Guid.CreateVersion7():N}@example.com";
            var tokenB = await SignupVerifiedAsync(client, mailpit, emailB);
            await MakePracticeEligibleAsync(client, tokenB);

            var foreignOrder = await SendWithStatusAsync(client, Request(
                HttpMethod.Get, $"/api/v1/practice/orders/{buyId}", null, tokenB));
            foreignOrder.Status.ShouldBe(HttpStatusCode.NotFound);
            ErrorCode(foreignOrder.Json).ShouldBe("resource.not_found");
            var foreignList = await SendJsonAsync(client, Request(
                HttpMethod.Get, "/api/v1/practice/orders", null, tokenB));
            RequireData(foreignList).GetProperty("items").EnumerateArray().ShouldBeEmpty();

            var realBeforeKyc = await SendWithStatusAsync(client, Request(
                HttpMethod.Get, "/api/v1/real/balances", null, token));
            realBeforeKyc.Status.ShouldBe(HttpStatusCode.Forbidden);
            ErrorCode(realBeforeKyc.Json).ShouldBe("compliance.not_eligible");

            await MakeRealEligibleAsync(client, token);
            var realBalances = await SendJsonAsync(client, Request(
                HttpMethod.Get, "/api/v1/real/balances", null, token));
            var realItems = realBalances.GetProperty("data").EnumerateArray().ToList();
            realItems.ShouldAllBe(item => !item.GetProperty("asset").GetString()!.StartsWith("P.", StringComparison.Ordinal));
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
