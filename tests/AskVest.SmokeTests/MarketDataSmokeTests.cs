using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace AskVest.SmokeTests;

[Collection("SmokeStack")]
public sealed class MarketDataSmokeTests
{
    private static readonly string? BaseUrl = Environment.GetEnvironmentVariable("ASKVEST_SMOKE_BASE_URL");
    private static readonly string? MailpitUrl = Environment.GetEnvironmentVariable("ASKVEST_SMOKE_MAILPIT_URL");

    private const string Password = "CorrectHorseSmoke9";
    private static readonly string[] SampleIds = ["sample-nvda", "sample-aapl", "sample-global-etf"];

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

    private static HttpRequestMessage Request(HttpMethod method, string path, object? body, string? token)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (method == HttpMethod.Post)
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
                    System.Text.Encoding.UTF8,
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
        if (!json.TryGetProperty("data", out var data))
        {
            throw new InvalidOperationException($"Expected data, got: {json.GetRawText()}");
        }

        return data;
    }

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

    private static async Task<JsonElement> PricesAsync(HttpClient client, string token, params string[] ids)
    {
        var json = await SendJsonAsync(client, Request(
            HttpMethod.Get, $"/api/v1/marketdata/prices?instrumentIds={string.Join(',', ids)}", null, token));
        return RequireData(json);
    }

    private static JsonElement PriceItem(JsonElement data, string instrumentId)
        => data.EnumerateArray().Single(i => i.GetProperty("instrumentId").GetString() == instrumentId);

    private static async Task ProbeAsync(HttpClient client, string token, string scenario)
    {
        // A scheduled poll may hold the advisory lock; retry briefly until a cycle runs.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var (status, json) = await SendWithStatusAsync(client, Request(
                HttpMethod.Post, "/api/v1/diagnostics/marketdata-scenario", new { scenario }, token));
            status.ShouldBe(HttpStatusCode.OK);
            if (RequireData(json).GetProperty("polled").GetBoolean())
            {
                return;
            }

            await Task.Delay(400, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException($"probe cycle never acquired the poll lock for scenario {scenario}");
    }

    private static async Task<JsonElement> WaitForLiveAsync(
        HttpClient client, string token, string instrumentId)
    {
        JsonElement item = default;
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(15);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            item = PriceItem(await PricesAsync(client, token, instrumentId), instrumentId);
            if (item.GetProperty("label").GetString() == "live")
            {
                break;
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        AssertLive(item);
        return item;
    }

    private static void AssertLive(JsonElement item)
    {
        item.GetProperty("label").GetString().ShouldBe("live");
        item.GetProperty("usableForExecution").GetBoolean().ShouldBeTrue();
        item.GetProperty("session").GetString().ShouldBe("regular");
        item.GetProperty("price").ValueKind.ShouldBe(JsonValueKind.String);
        item.GetProperty("currency").GetString().ShouldBe("USD");
        item.GetProperty("source").GetString().ShouldBe("AskVest Demo Feed");
        var asOf = item.GetProperty("asOf").GetDateTimeOffset();
        (TimeProvider.System.GetUtcNow() - asOf).ShouldBeLessThan(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task MarketData_Journey_Prices_Scenarios_Candles_And_Errors()
    {
        using var client = Client();
        using var mailpit = Mailpit();

        // Unauthenticated requests are refused on both endpoints.
        var anonymousPrices = await SendWithStatusAsync(client, Request(
            HttpMethod.Get, $"/api/v1/marketdata/prices?instrumentIds={SampleIds[0]}", null, null));
        anonymousPrices.Status.ShouldBe(HttpStatusCode.Unauthorized);
        anonymousPrices.Json.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.unauthenticated");

        var now = TimeProvider.System.GetUtcNow();
        var anonymousCandles = await SendWithStatusAsync(client, Request(
            HttpMethod.Get,
            $"/api/v1/marketdata/instruments/{SampleIds[0]}/candles?interval=1d&from={Uri.EscapeDataString(now.AddDays(-3).ToString("O"))}&to={Uri.EscapeDataString(now.ToString("O"))}",
            null, null));
        anonymousCandles.Status.ShouldBe(HttpStatusCode.Unauthorized);

        var email = $"smoke-md-{Guid.CreateVersion7():N}@example.com";
        var token = await SignupVerifiedAsync(client, mailpit, email);

        try
        {
            // The poller runs every 5 seconds; wait for the first stored prices.
            JsonElement prices = default;
            var deadline = TimeProvider.System.GetUtcNow().AddSeconds(30);
            while (TimeProvider.System.GetUtcNow() < deadline)
            {
                prices = await PricesAsync(client, token, SampleIds);
                if (prices.EnumerateArray().All(i => i.GetProperty("label").GetString() == "live"))
                {
                    break;
                }

                await Task.Delay(1000, TestContext.Current.CancellationToken);
            }

            prices.EnumerateArray().Count().ShouldBe(3);
            foreach (var id in SampleIds)
            {
                AssertLive(PriceItem(prices, id));
            }

            // Scenario probes drive the fake provider through one poll cycle each.
            await ProbeAsync(client, token, "delayed");
            var delayed = PriceItem(await PricesAsync(client, token, SampleIds[0]), SampleIds[0]);
            delayed.GetProperty("label").GetString().ShouldBe("delayed");
            delayed.GetProperty("usableForExecution").GetBoolean().ShouldBeTrue();

            await ProbeAsync(client, token, "halted");
            var halted = PriceItem(await PricesAsync(client, token, SampleIds[0]), SampleIds[0]);
            halted.GetProperty("label").GetString().ShouldBe("halted");
            halted.GetProperty("usableForExecution").GetBoolean().ShouldBeFalse();

            await ProbeAsync(client, token, "closed");
            var closed = PriceItem(await PricesAsync(client, token, SampleIds[0]), SampleIds[0]);
            closed.GetProperty("label").GetString().ShouldBe("closed");
            closed.GetProperty("usableForExecution").GetBoolean().ShouldBeFalse();
            closed.GetProperty("previousClose").ValueKind.ShouldBe(JsonValueKind.String);

            await ProbeAsync(client, token, "normal");
            await WaitForLiveAsync(client, token, SampleIds[0]);

            // Outlier spike: the first tick is held as a suspect, the repeated tick confirms.
            var trusted = PriceItem(await PricesAsync(client, token, SampleIds[0]), SampleIds[0])
                .GetProperty("price").GetString();
            await ProbeAsync(client, token, "outlier_spike");
            var afterFirstSpike = PriceItem(await PricesAsync(client, token, SampleIds[0]), SampleIds[0]);
            afterFirstSpike.GetProperty("price").GetString().ShouldBe(trusted);
            afterFirstSpike.GetProperty("usableForExecution").GetBoolean().ShouldBeTrue();

            await ProbeAsync(client, token, "outlier_spike");
            var afterSecondSpike = await WaitForLiveAsync(client, token, SampleIds[0]);
            afterSecondSpike.GetProperty("price").GetString().ShouldNotBe(trusted);

            await ProbeAsync(client, token, "normal");

            // Outage: the last price is retained, then ages past the 15 second stale threshold.
            var outageStart = TimeProvider.System.GetUtcNow();
            await ProbeAsync(client, token, "outage");
            var duringOutage = PriceItem(await PricesAsync(client, token, SampleIds[0]), SampleIds[0]);
            duringOutage.GetProperty("price").ValueKind.ShouldBe(JsonValueKind.String);

            JsonElement stale = default;
            deadline = outageStart.AddSeconds(25);
            while (TimeProvider.System.GetUtcNow() < deadline)
            {
                stale = PriceItem(await PricesAsync(client, token, SampleIds[0]), SampleIds[0]);
                if (stale.GetProperty("label").GetString() == "stale")
                {
                    break;
                }

                await Task.Delay(1000, TestContext.Current.CancellationToken);
            }

            stale.GetProperty("label").GetString().ShouldBe("stale");
            stale.GetProperty("usableForExecution").GetBoolean().ShouldBeFalse();
            stale.GetProperty("price").ValueKind.ShouldBe(JsonValueKind.String);

            await ProbeAsync(client, token, "normal");
            await WaitForLiveAsync(client, token, SampleIds[0]);

            // Unknown symbol: only the overridden sample-nvda symbol is unmapped.
            await ProbeAsync(client, token, "unknown_symbol");
            var mixed = await PricesAsync(client, token, SampleIds[0], SampleIds[1]);
            PriceItem(mixed, SampleIds[0]).GetProperty("label").GetString().ShouldBe("unavailable");
            PriceItem(mixed, SampleIds[0]).GetProperty("usableForExecution").GetBoolean().ShouldBeFalse();
            await WaitForLiveAsync(client, token, SampleIds[1]);

            await ProbeAsync(client, token, "normal");
            await WaitForLiveAsync(client, token, SampleIds[0]);

            // Candles: ascending, decimal strings, adjusted, partial forming candle.
            var from = now.AddDays(-3);
            var candlesJson = await SendJsonAsync(client, Request(HttpMethod.Get,
                $"/api/v1/marketdata/instruments/{SampleIds[0]}/candles?interval=1d&from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(now.ToString("O"))}",
                null, token));
            var candlesData = RequireData(candlesJson);
            candlesData.GetProperty("stale").GetBoolean().ShouldBeFalse();
            candlesData.GetProperty("source").GetString().ShouldBe("AskVest Demo Feed");
            var candles = candlesData.GetProperty("candles").EnumerateArray().ToList();
            candles.Count.ShouldBeGreaterThanOrEqualTo(3);
            var openTimes = candles.Select(c => c.GetProperty("openTime").GetDateTimeOffset()).ToList();
            openTimes.ShouldBe(openTimes.OrderBy(t => t).ToList());
            foreach (var candle in candles)
            {
                candle.GetProperty("open").ValueKind.ShouldBe(JsonValueKind.String);
                candle.GetProperty("high").ValueKind.ShouldBe(JsonValueKind.String);
                candle.GetProperty("low").ValueKind.ShouldBe(JsonValueKind.String);
                candle.GetProperty("close").ValueKind.ShouldBe(JsonValueKind.String);
                candle.GetProperty("volume").ValueKind.ShouldBe(JsonValueKind.String);
                candle.GetProperty("adjusted").GetBoolean().ShouldBeTrue();
            }

            candles[^1].GetProperty("partial").GetBoolean().ShouldBeTrue();
            candles.Take(candles.Count - 1).ShouldAllBe(c => !c.GetProperty("partial").GetBoolean());

            // Candle request validation and error paths.
            var badInterval = await SendWithStatusAsync(client, Request(HttpMethod.Get,
                $"/api/v1/marketdata/instruments/{SampleIds[0]}/candles?interval=5m&from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(now.ToString("O"))}",
                null, token));
            badInterval.Status.ShouldBe(HttpStatusCode.BadRequest);
            badInterval.Json.GetProperty("error").GetProperty("code").GetString().ShouldBe("validation.failed");

            var tooWide = await SendWithStatusAsync(client, Request(HttpMethod.Get,
                $"/api/v1/marketdata/instruments/{SampleIds[0]}/candles?interval=15m&from={Uri.EscapeDataString(now.AddDays(-30).ToString("O"))}&to={Uri.EscapeDataString(now.ToString("O"))}",
                null, token));
            tooWide.Status.ShouldBe(HttpStatusCode.BadRequest);

            var tooMany = await SendWithStatusAsync(client, Request(HttpMethod.Get,
                $"/api/v1/marketdata/instruments/{SampleIds[0]}/candles?interval=15m&from={Uri.EscapeDataString(now.AddDays(-6).ToString("O"))}&to={Uri.EscapeDataString(now.ToString("O"))}",
                null, token));
            tooMany.Status.ShouldBe(HttpStatusCode.BadRequest);

            var unknownInstrument = await SendWithStatusAsync(client, Request(HttpMethod.Get,
                $"/api/v1/marketdata/instruments/instr-nope/candles?interval=1d&from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(now.ToString("O"))}",
                null, token));
            unknownInstrument.Status.ShouldBe(HttpStatusCode.NotFound);
            unknownInstrument.Json.GetProperty("error").GetProperty("code").GetString().ShouldBe("resource.not_found");

            var oversizedBatch = string.Join(',', Enumerable.Range(0, 51).Select(i => $"instr-x{i}"));
            var bigBatch = await SendWithStatusAsync(client, Request(HttpMethod.Get,
                $"/api/v1/marketdata/prices?instrumentIds={oversizedBatch}", null, token));
            bigBatch.Status.ShouldBe(HttpStatusCode.BadRequest);
            bigBatch.Json.GetProperty("error").GetProperty("code").GetString().ShouldBe("validation.failed");
        }
        finally
        {
            try
            {
                await ProbeAsync(client, token, "normal");
            }
            catch
            {
                // Best effort restore; the scenario only affects this stack's fake feed.
            }
        }
    }
}
