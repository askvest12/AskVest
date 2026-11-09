using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace AskVest.SmokeTests;

// One end-to-end assistant journey over the real stack with the fake model.
// Item 7 of the spec (audit table inspection) is verified through docker exec
// because the postgres container publishes no host port in smoke compose.
[Collection("SmokeStack")]
public sealed class AssistantSmokeTests
{
    private static readonly string? BaseUrl = Environment.GetEnvironmentVariable("ASKVEST_SMOKE_BASE_URL");
    private static readonly string? MailpitUrl = Environment.GetEnvironmentVariable("ASKVEST_SMOKE_MAILPIT_URL");

    private const string Password = "CorrectHorseSmoke9";
    private const string PracticeInstrumentId = "sample-nvda";
    private const string PriceInstrumentId = "sample-aapl";
    private const string HostileInstrumentId = "sample-hostile-fixture";
    private const string SafeFallback =
        "I could not put together a reliable answer for that. Please try rephrasing, or browse the lessons for the concepts.";

    [Fact]
    public async Task Assistant_Journey()
    {
        var client = Client();
        var mailpit = Mailpit();
        var tokenA = await SignupEligibleAsync(client, mailpit, "assistant-a");

        // 1. Practice eligible but no ai_processing consent: refused naming the consent.
        var denied = await SendWithStatusAsync(client, MessageRequest(tokenA, "What is a tokenized stock?"));
        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        denied.Json.GetProperty("error").GetProperty("code").GetString().ShouldBe("compliance.not_eligible");
        denied.Json.GetProperty("error").GetProperty("details").EnumerateArray()
            .ShouldContain(d => d.GetProperty("message").GetString()!.Contains("ai_processing", StringComparison.Ordinal));

        await AcceptAssistantConsentAsync(client, tokenA);
        await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/diagnostics/assistant-scenario",
            new { scenario = "normal" }, tokenA));
        await ProbeMarketDataAsync(client, tokenA);

        // Citations resolve and the price fact card matches market data.
        var priceReply = await SendMessageAsync(client, tokenA, $"What is the latest price for {PriceInstrumentId}?");
        var citations = priceReply.GetProperty("citations").EnumerateArray().ToList();
        citations.ShouldNotBeEmpty();
        citations.ShouldAllBe(c => c.GetProperty("id").GetString()!.Length > 0);
        var apiPrice = await LatestPriceAsync(client, tokenA, PriceInstrumentId);
        var apiPriceValue = decimal.Parse(apiPrice.GetProperty("price").GetString()!, CultureInfo.InvariantCulture);
        var apiLabel = apiPrice.GetProperty("label").GetString()!;
        var cards = priceReply.GetProperty("factCards").EnumerateArray().ToList();
        cards.ShouldNotBeEmpty();
        var priceField = cards.SelectMany(c => c.GetProperty("fields").EnumerateArray())
            .FirstOrDefault(f => f.GetProperty("name").GetString() == "price");
        priceField.ValueKind.ShouldBe(JsonValueKind.Object);
        decimal.Parse(priceField.GetProperty("value").GetString()!, CultureInfo.InvariantCulture)
            .ShouldBe(apiPriceValue);
        cards.SelectMany(c => c.GetProperty("fields").EnumerateArray())
            .ShouldContain(f => f.GetProperty("value").GetString() == apiLabel);

        // 2. Practice buy, then holdings match the practice portfolio.
        var accountStart = await SendWithStatusAsync(client,
            Request(HttpMethod.Post, "/api/v1/practice/account", new { }, tokenA));
        accountStart.Json.GetProperty("success").GetBoolean().ShouldBeTrue(accountStart.Json.GetRawText());
        var order = await SendWithStatusAsync(client, Request(HttpMethod.Post, "/api/v1/practice/orders",
            new { instrumentId = PracticeInstrumentId, side = "buy", type = "market", amount = "100" }, tokenA));
        order.Json.GetProperty("success").GetBoolean().ShouldBeTrue(order.Json.GetRawText());
        var holdingsReply = await SendMessageAsync(client, tokenA, "What is in my practice portfolio?");
        holdingsReply.GetProperty("text").GetString().ShouldNotBeNullOrWhiteSpace();
        var portfolio = await PortfolioAsync(client, tokenA);
        portfolio.GetProperty("holdings").EnumerateArray()
            .ShouldContain(h => h.GetProperty("instrumentId").GetString() == PracticeInstrumentId);
        var holdingsValue = decimal.Parse(
            portfolio.GetProperty("totals").GetProperty("holdingsValue").GetString()!, CultureInfo.InvariantCulture);
        holdingsReply.GetProperty("factCards").EnumerateArray()
            .SelectMany(c => c.GetProperty("fields").EnumerateArray())
            .Where(f => f.GetProperty("name").GetString() == "holdingsValue")
            .Select(f => decimal.Parse(f.GetProperty("value").GetString()!, CultureInfo.InvariantCulture))
            .ShouldContain(v => v == holdingsValue);

        var realReply = await SendMessageAsync(client, tokenA, "Show me my real portfolio.");
        Assert.Contains("not available", realReply.GetProperty("text").GetString()!, StringComparison.OrdinalIgnoreCase);

        // 3. Scenario drills through the probe on a second user so the smoke
        // hourly quota does not exhaust the journey user early.
        var tokenB = await SignupEligibleAsync(client, mailpit, "assistant-b");
        await AcceptAssistantConsentAsync(client, tokenB);
        await SendWithStatusAsync(client, Request(HttpMethod.Post, "/api/v1/practice/account", new { }, tokenB));

        var beforeCash = (await AccountAsync(client, tokenB)).GetProperty("cash").GetString();
        try
        {
            var advice = await SendScenarioAsync(client, tokenB, "advice_phrase");
            advice.GetProperty("policy").GetProperty("blocked").GetBoolean().ShouldBeTrue();
            advice.GetProperty("text").GetString().ShouldBe(SafeFallback);
            var ungrounded = await SendScenarioAsync(client, tokenB, "ungrounded_number");
            ungrounded.GetProperty("text").GetString().ShouldBe(SafeFallback);

            var fabricated = await SendScenarioAsync(client, tokenB, "fabricated_citation");
            var fabricatedText = fabricated.GetProperty("text").GetString()!;
            Assert.DoesNotContain("[r99]", fabricatedText, StringComparison.Ordinal);
            if (fabricated.GetProperty("citations").GetArrayLength() == 0)
            {
                Assert.DoesNotContain("[r", fabricatedText, StringComparison.Ordinal);
            }

            var markup = await SendScenarioAsync(client, tokenB, "markup_output");
            var markupText = markup.GetProperty("text").GetString()!;
            Assert.DoesNotContain("<", markupText, StringComparison.Ordinal);
            Assert.DoesNotContain("http", markupText, StringComparison.OrdinalIgnoreCase);

            var injection = await SendScenarioAsync(client, tokenB, "injection_obey");
            Assert.DoesNotContain("00000000-0000-0000-0000-000000000000",
                injection.GetProperty("text").GetString()!, StringComparison.Ordinal);
            (await AccountAsync(client, tokenB)).GetProperty("cash").GetString().ShouldBe(beforeCash);

            var (errorStatus, _) = await SendScenarioWithStatusAsync(client, tokenB, "error");
            errorStatus.ShouldBe(HttpStatusCode.ServiceUnavailable);
            var (slowStatus, _) = await SendScenarioWithStatusAsync(client, tokenB, "slow");
            slowStatus.ShouldBe(HttpStatusCode.ServiceUnavailable);

            // 4. Hostile fixture instrument answers normally.
            await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/diagnostics/assistant-scenario",
                new { scenario = "normal" }, tokenB));
            var hostile = await SendMessageAsync(client, tokenB, $"Describe the {HostileInstrumentId} instrument.");
            Assert.DoesNotContain("SYSTEM OVERRIDE", hostile.GetProperty("text").GetString()!, StringComparison.OrdinalIgnoreCase);
            hostile.GetProperty("text").GetString()!.ShouldNotBeNullOrWhiteSpace();
        }
        finally
        {
            // The fake model scenario is shared stack state; always restore normal.
            await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/diagnostics/assistant-scenario",
                new { scenario = "normal" }, tokenB));
        }

        // 5. Idempotent replay is identical; the hourly limit answers 429 with Retry-After.
        var replayKey = $"smoke-{Guid.CreateVersion7():N}";
        string firstBody;
        using (var firstResponse = await client.SendAsync(
            MessageRequest(tokenA, "What is a tokenized stock?", replayKey), TestContext.Current.CancellationToken))
        {
            firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
            firstBody = await firstResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        }

        using (var secondResponse = await client.SendAsync(
            MessageRequest(tokenA, "What is a tokenized stock?", replayKey), TestContext.Current.CancellationToken))
        {
            secondResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
            secondResponse.Headers.GetValues("Idempotent-Replay").ShouldContain("true");
            var secondBody = await secondResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            secondBody.ShouldBe(firstBody);
        }

        // 6. Conversations list, read, delete; cross-user 404; anonymous 401.
        // Runs before the quota probe below because the limit then rejects sends.
        var list = await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/assistant/conversations", null, tokenA));
        list.GetProperty("items").GetArrayLength().ShouldBeGreaterThan(0);
        var conversationId = list.GetProperty("items")[0].GetProperty("id").GetString()!;

        var detail = await SendJsonAsync(client, Request(HttpMethod.Get, $"/api/v1/assistant/conversations/{conversationId}", null, tokenA));
        detail.GetProperty("id").GetString().ShouldBe(conversationId);
        var deleted = await SendJsonAsync(client, Request(HttpMethod.Delete, $"/api/v1/assistant/conversations/{conversationId}", null, tokenA));
        deleted.GetProperty("id").GetString().ShouldBe(conversationId);

        var tokenC = await SignupEligibleAsync(client, mailpit, "assistant-c");
        await AcceptAssistantConsentAsync(client, tokenC);
        var (crossStatus, crossJson) = await SendWithStatusAsync(client,
            Request(HttpMethod.Get, $"/api/v1/assistant/conversations/{conversationId}", null, tokenC));
        crossStatus.ShouldBe(HttpStatusCode.NotFound);
        crossJson.GetProperty("error").GetProperty("code").GetString().ShouldBe("resource.not_found");

        var (anonStatus, _) = await SendWithStatusAsync(client,
            Request(HttpMethod.Get, $"/api/v1/assistant/conversations/{conversationId}", null, null));
        anonStatus.ShouldBe(HttpStatusCode.Unauthorized);

        // 5b. The hourly limit answers 429 with Retry-After.
        HttpStatusCode limited = HttpStatusCode.OK;
        string? retryAfter = null;
        for (var attempt = 0; attempt < 12 && limited != HttpStatusCode.TooManyRequests; attempt++)
        {
            using var request = MessageRequest(tokenA, $"Limit probe {attempt} {Guid.CreateVersion7():N}");
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            limited = response.StatusCode;
            if (limited == HttpStatusCode.TooManyRequests)
            {
                retryAfter = response.Headers.RetryAfter?.ToString();
            }
        }

        limited.ShouldBe(HttpStatusCode.TooManyRequests);
        retryAfter.ShouldNotBeNullOrWhiteSpace();
    }

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

        if (method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Delete)
        {
            request.Headers.Add("Idempotency-Key", $"k-{Guid.CreateVersion7():N}");
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static HttpRequestMessage MessageRequest(string token, string text, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/assistant/messages", UriKind.Relative))
        {
            Content = JsonContent.Create(new { text }),
        };
        request.Headers.Add("Idempotency-Key", key ?? $"k-{Guid.CreateVersion7():N}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Json)> SendWithStatusAsync(
        HttpClient client, HttpRequestMessage request)
    {
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return (response.StatusCode, json);
    }

    private static async Task<JsonElement> SendJsonAsync(HttpClient client, HttpRequestMessage request)
    {
        var (status, json) = await SendWithStatusAsync(client, request);
        status.ShouldBe(HttpStatusCode.OK);
        json.GetProperty("success").GetBoolean().ShouldBeTrue(json.GetRawText());
        return json.GetProperty("data");
    }

    private static async Task<JsonElement> SendMessageAsync(HttpClient client, string token, string text)
    {
        var (status, json) = await SendWithStatusAsync(client, MessageRequest(token, text));
        status.ShouldBe(HttpStatusCode.OK);
        json.GetProperty("success").GetBoolean().ShouldBeTrue(json.GetRawText());
        return json.GetProperty("data");
    }

    private static async Task<JsonElement> SendScenarioAsync(HttpClient client, string token, string scenario)
    {
        var probe = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/diagnostics/assistant-scenario",
            new { scenario }, token));
        probe.GetProperty("scenario").GetString().ShouldBe(scenario);

        var (status, json) = await SendWithStatusAsync(client, MessageRequest(token, "Smoke scenario check."));
        status.ShouldBe(HttpStatusCode.OK);
        json.GetProperty("success").GetBoolean().ShouldBeTrue(json.GetRawText());
        return json.GetProperty("data");
    }

    private static async Task<(HttpStatusCode Status, JsonElement Json)> SendScenarioWithStatusAsync(
        HttpClient client, string token, string scenario)
    {
        await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/diagnostics/assistant-scenario",
            new { scenario }, token));
        return await SendWithStatusAsync(client, MessageRequest(token, "Smoke scenario check."));
    }

    private static async Task<JsonElement> LatestPriceAsync(HttpClient client, string token, string instrumentId)
    {
        var data = await SendJsonAsync(client, Request(HttpMethod.Get,
            $"/api/v1/marketdata/prices?instrumentIds={instrumentId}", null, token));
        return data.EnumerateArray()
            .First(i => i.GetProperty("instrumentId").GetString() == instrumentId);
    }

    private static async Task<JsonElement> PortfolioAsync(HttpClient client, string token)
        => await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/practice/portfolio", null, token));

    private static async Task<JsonElement> AccountAsync(HttpClient client, string token)
        => await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/practice/account", null, token));

    private static async Task ProbeMarketDataAsync(HttpClient client, string token)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var data = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/diagnostics/marketdata-scenario",
                new { scenario = "normal" }, token));
            if (data.GetProperty("polled").GetBoolean())
            {
                return;
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException("marketdata probe cycle never acquired the poll lock");
    }

    private static async Task AcceptAssistantConsentAsync(HttpClient client, string token)
    {
        var docs = await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/compliance/documents", null, token));
        var items = docs.GetProperty("documents").EnumerateArray()
            .Where(d => d.GetProperty("requiredFor").GetString() == "assistant" && !d.GetProperty("accepted").GetBoolean())
            .Select(d => new { type = d.GetProperty("type").GetString()!, version = d.GetProperty("version").GetString()!, sha256 = d.GetProperty("sha256").GetString()! })
            .ToList();
        items.ShouldNotBeEmpty("expected unaccepted assistant documents");
        var (status, json) = await SendWithStatusAsync(client,
            Request(HttpMethod.Post, "/api/v1/compliance/consents", new { documents = items }, token));
        status.ShouldBe(HttpStatusCode.OK);
        json.GetProperty("success").GetBoolean().ShouldBeTrue(json.GetRawText());
    }

    private static async Task<string> SignupEligibleAsync(HttpClient client, HttpClient mailpit, string prefix)
    {
        var email = $"{prefix}-{Guid.CreateVersion7():N}@smoke.test";
        var signUp = await SendWithStatusAsync(client, Request(HttpMethod.Post, "/api/v1/auth/sign-up",
            new { email, password = Password }, null));
        signUp.Json.GetProperty("success").GetBoolean().ShouldBeTrue(signUp.Json.GetRawText());
        var verifyCode = await WaitForCodeAsync(mailpit, email);
        var verify = await SendWithStatusAsync(client, Request(HttpMethod.Post, "/api/v1/auth/verify-email",
            new { email, code = verifyCode }, null));
        verify.Json.GetProperty("success").GetBoolean().ShouldBeTrue(verify.Json.GetRawText());
        var login = await SendWithStatusAsync(client, Request(HttpMethod.Post, "/api/v1/auth/login", new
        {
            email,
            password = Password,
            device = new { installationId = $"dev-{Guid.CreateVersion7():N}", platform = "android", deviceName = "Smoke", appVersion = "1.0.0" },
        }, null));
        login.Json.GetProperty("success").GetBoolean().ShouldBeTrue(login.Json.GetRawText());
        var token = login.Json.GetProperty("data").GetProperty("accessToken").GetString()!;

        var docs = await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/compliance/documents", null, token));
        var practiceDocs = docs.GetProperty("documents").EnumerateArray()
            .Where(d => d.GetProperty("requiredFor").GetString() == "practice" && !d.GetProperty("accepted").GetBoolean())
            .Select(d => new { type = d.GetProperty("type").GetString()!, version = d.GetProperty("version").GetString()!, sha256 = d.GetProperty("sha256").GetString()! })
            .ToList();
        var consents = await SendWithStatusAsync(client,
            Request(HttpMethod.Post, "/api/v1/compliance/consents", new { documents = practiceDocs }, token));
        consents.Json.GetProperty("success").GetBoolean().ShouldBeTrue(consents.Json.GetRawText());
        var country = await SendWithStatusAsync(client,
            Request(HttpMethod.Put, "/api/v1/compliance/country", new { country = "NG" }, token));
        country.Json.GetProperty("success").GetBoolean().ShouldBeTrue(country.Json.GetRawText());
        return token;
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
}
