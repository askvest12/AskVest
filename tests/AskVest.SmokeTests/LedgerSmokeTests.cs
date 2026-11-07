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
public sealed class LedgerSmokeTests
{
    private static readonly string? BaseUrl = Environment.GetEnvironmentVariable("ASKVEST_SMOKE_BASE_URL");
    private static readonly string? MailpitUrl = Environment.GetEnvironmentVariable("ASKVEST_SMOKE_MAILPIT_URL");
    private static readonly string? WebhookSecret = Environment.GetEnvironmentVariable("ASKVEST_COMPLIANCE_KYC_WEBHOOK_SECRET");

    private const string Password = "CorrectHorseSmoke9";

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

    private static string NewEmail() => $"smoke-l-{Guid.CreateVersion7():N}@example.com";

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
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            await Task.Delay(TimeSpan.FromSeconds(11), TestContext.Current.CancellationToken);
            using var retry = await client.SendAsync(await CloneAsync(request), TestContext.Current.CancellationToken);
            return await retry.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Content is not null)
        {
            clone.Content = new StringContent(
                await request.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                Encoding.UTF8,
                "application/json");
        }

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
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

    private static async Task MakePracticeEligibleAsync(HttpClient client, string token)
    {
        var docs = await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/compliance/documents", null, null, token));
        var items = docs.GetProperty("data").GetProperty("documents").EnumerateArray()
            .Where(d => d.GetProperty("requiredFor").GetString() == "practice" && !d.GetProperty("accepted").GetBoolean())
            .Select(d => new { type = d.GetProperty("type").GetString()!, version = d.GetProperty("version").GetString()!, sha256 = d.GetProperty("sha256").GetString()! })
            .ToList();
        items.ShouldNotBeEmpty("expected unaccepted practice documents");
        var accepted = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/compliance/consents", new { documents = items }, null, token));
        accepted.GetProperty("success").GetBoolean().ShouldBeTrue();
        var country = await SendJsonAsync(client, Request(HttpMethod.Put, "/api/v1/compliance/country", new { country = "NG" }, null, token));
        country.GetProperty("success").GetBoolean().ShouldBeTrue();
    }

    private static async Task MakeRealEligibleAsync(HttpClient client, string token)
    {
        var docs = await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/compliance/documents", null, null, token));
        var items = docs.GetProperty("data").GetProperty("documents").EnumerateArray()
            .Where(d => d.GetProperty("requiredFor").GetString() == "real" && !d.GetProperty("accepted").GetBoolean())
            .Select(d => new { type = d.GetProperty("type").GetString()!, version = d.GetProperty("version").GetString()!, sha256 = d.GetProperty("sha256").GetString()! })
            .ToList();
        items.ShouldNotBeEmpty("expected unaccepted real documents");
        var accepted = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/compliance/consents", new { documents = items }, null, token));
        accepted.GetProperty("success").GetBoolean().ShouldBeTrue();

        var started = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/compliance/kyc/start", null, null, token));
        started.GetProperty("success").GetBoolean().ShouldBeTrue();
        var reference = RequireData(started).GetProperty("providerReference").GetString()!;

        var body = "{"
            + $"\"eventId\":\"evt-{Guid.CreateVersion7():N}\","
            + $"\"providerReference\":\"{reference}\","
            + "\"outcome\":\"approved\","
            + "\"verifiedCountry\":\"NG\","
            + "\"fullName\":\"Ledger Smoke\","
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
    }

    private static HttpRequestMessage Probe(string book, string kind, string amount, string? key = null, string? token = null)
        => Request(HttpMethod.Post, $"/api/v1/{book}/diagnostics/ledger-post",
            new { kind, asset = "USD", amount }, key, token);

    private static async Task<JsonElement> BalancesAsync(HttpClient client, string book, string token)
    {
        var result = await SendJsonAsync(client, Request(HttpMethod.Get, $"/api/v1/{book}/balances", null, null, token));
        result.GetProperty("success").GetBoolean().ShouldBeTrue();
        return result.GetProperty("data");
    }

    private static async Task<JsonElement> EntriesAsync(HttpClient client, string book, string token)
    {
        var result = await SendJsonAsync(client, Request(HttpMethod.Get, $"/api/v1/{book}/ledger/entries", null, null, token));
        result.GetProperty("success").GetBoolean().ShouldBeTrue();
        return RequireData(result);
    }

    private static string Available(JsonElement balances)
    {
        var items = balances.EnumerateArray().ToList();
        items.ShouldHaveSingleItem();
        return items[0].GetProperty("available").GetString()!;
    }

    [Fact]
    public async Task Ledger_Journey_Practice_Real_Separation_And_Isolation()
    {
        RequireWebhookSecret();
        using var client = Client();
        using var mailpit = Mailpit();

        var emailA = NewEmail();
        var tokenA = await SignupVerifiedAsync(client, mailpit, emailA);
        await MakePracticeEligibleAsync(client, tokenA);

        // Real routes stay gated until KYC approval.
        var realDenied = await SendJsonWithStatusAsync(
            client, Request(HttpMethod.Get, "/api/v1/real/balances", null, null, tokenA), HttpStatusCode.Forbidden);
        realDenied.GetProperty("error").GetProperty("code").GetString().ShouldBe("compliance.not_eligible");

        // Practice: credit 100, read 100, debit 30, read 70.
        var credit = await SendJsonWithStatusAsync(
            client, Probe("practice", "credit", "100", token: tokenA), HttpStatusCode.OK);
        var creditData = RequireData(credit);
        creditData.GetProperty("journalId").GetGuid().ShouldNotBe(Guid.Empty);

        Available(await BalancesAsync(client, "practice", tokenA)).ShouldBe("100");

        var debitKey = $"smoke-debit-{Guid.CreateVersion7():N}";
        await SendJsonWithStatusAsync(
            client, Probe("practice", "debit", "30", debitKey, tokenA), HttpStatusCode.OK);
        Available(await BalancesAsync(client, "practice", tokenA)).ShouldBe("70");

        // History shows both entries newest first with string amounts.
        var history = await EntriesAsync(client, "practice", tokenA);
        var items = history.GetProperty("items").EnumerateArray().ToList();
        items.Count.ShouldBe(2);
        items[0].GetProperty("amount").GetString().ShouldBe("-30");
        items[0].GetProperty("amount").ValueKind.ShouldBe(JsonValueKind.String);
        items[1].GetProperty("amount").GetString().ShouldBe("100");

        // Overdraft returns the standard envelope error and changes nothing.
        var overdraft = await SendJsonWithStatusAsync(
            client, Probe("practice", "debit", "100", token: tokenA), HttpStatusCode.UnprocessableEntity);
        overdraft.GetProperty("error").GetProperty("code").GetString().ShouldBe("ledger.insufficient_funds");
        Available(await BalancesAsync(client, "practice", tokenA)).ShouldBe("70");

        // Replaying the debit key returns the identical journal and moves nothing.
        var replay = await SendJsonWithStatusAsync(
            client, Probe("practice", "debit", "30", debitKey, tokenA), HttpStatusCode.OK);
        RequireData(replay).GetProperty("journalId").GetGuid()
            .ShouldBe(items[0].GetProperty("journalId").GetGuid());
        Available(await BalancesAsync(client, "practice", tokenA)).ShouldBe("70");

        // KYC approval opens Real; the books never mix.
        await MakeRealEligibleAsync(client, tokenA);
        await SendJsonWithStatusAsync(
            client, Probe("real", "credit", "55", token: tokenA), HttpStatusCode.OK);
        Available(await BalancesAsync(client, "real", tokenA)).ShouldBe("55");
        Available(await BalancesAsync(client, "practice", tokenA)).ShouldBe("70");

        var realHistory = await EntriesAsync(client, "real", tokenA);
        realHistory.GetProperty("items").EnumerateArray().Count().ShouldBe(1);

        // A second user sees none of the first user's entries or balances.
        var emailB = NewEmail();
        var tokenB = await SignupVerifiedAsync(client, mailpit, emailB);
        await MakePracticeEligibleAsync(client, tokenB);
        var otherHistory = await EntriesAsync(client, "practice", tokenB);
        otherHistory.GetProperty("items").EnumerateArray().ShouldBeEmpty();
        var otherBalances = await BalancesAsync(client, "practice", tokenB);
        otherBalances.EnumerateArray().ShouldBeEmpty();
    }
}
