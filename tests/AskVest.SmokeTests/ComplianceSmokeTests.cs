using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace AskVest.SmokeTests;

public sealed class ComplianceSmokeTests
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

    private static string NewEmail() => $"smoke-c-{Guid.CreateVersion7():N}@example.com";

    private static HttpRequestMessage Post(string path, object? body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Add("Idempotency-Key", key ?? $"k-{Guid.CreateVersion7():N}");
        return request;
    }

    private static HttpRequestMessage Put(string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", $"k-{Guid.CreateVersion7():N}");
        return request;
    }

    private static HttpRequestMessage Get(string path, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static HttpRequestMessage PostAuth(string path, object? body, string token)
    {
        var request = Post(path, body, $"k-{Guid.CreateVersion7():N}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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

    private static JsonElement RequireError(JsonElement json)
    {
        if (!json.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Expected error object, got: {json.GetRawText()}");
        }

        return error;
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
                var match = Regex.Match(text, @"code is (\d{6})");
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
        var signUp = await SendJsonAsync(client, Post("/api/v1/auth/sign-up", new { email, password = Password }));
        signUp.GetProperty("success").GetBoolean().ShouldBeTrue();
        var verifyCode = await WaitForCodeAsync(mailpit, email, "Verify");
        var verify = await SendJsonAsync(client, Post("/api/v1/auth/verify-email", new { email, code = verifyCode }));
        verify.GetProperty("success").GetBoolean().ShouldBeTrue();
        var login = await SendJsonAsync(client, Post("/api/v1/auth/login", new
        {
            email,
            password = Password,
            device = new { installationId = $"dev-{Guid.CreateVersion7():N}", platform = "android", deviceName = "Smoke", appVersion = "1.0.0" },
        }));
        var data = RequireData(login);
        data.GetProperty("result").GetString().ShouldBe("authenticated");
        return data.GetProperty("accessToken").GetString()!;
    }

    private static async Task AcceptDocumentsAsync(HttpClient client, string token, string requiredFor)
    {
        var docs = await SendJsonAsync(client, Get("/api/v1/compliance/documents", token));
        var items = docs.GetProperty("data").GetProperty("documents").EnumerateArray()
            .Where(d => d.GetProperty("requiredFor").GetString() == requiredFor && !d.GetProperty("accepted").GetBoolean())
            .Select(d => new { type = d.GetProperty("type").GetString()!, version = d.GetProperty("version").GetString()!, sha256 = d.GetProperty("sha256").GetString()! })
            .ToList();
        items.ShouldNotBeEmpty($"expected unaccepted {requiredFor} documents");
        var accepted = await SendJsonAsync(client, PostAuth("/api/v1/compliance/consents",
            new { documents = items }, token));
        accepted.GetProperty("success").GetBoolean().ShouldBeTrue();
    }

    private static async Task SetCountryAsync(HttpClient client, string token, string country)
    {
        var result = await SendJsonAsync(client, PutAuth("/api/v1/compliance/country", new { country }, token));
        result.GetProperty("success").GetBoolean().ShouldBeTrue();
    }

    private static HttpRequestMessage PutAuth(string path, object body, string token)
    {
        var request = Put(path, body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<string> StartKycAsync(HttpClient client, string token)
    {
        var result = await SendJsonAsync(client, PostAuth("/api/v1/compliance/kyc/start", null, token));
        result.GetProperty("success").GetBoolean().ShouldBeTrue();
        return RequireData(result).GetProperty("providerReference").GetString()!;
    }

    private static string WebhookSignature(string timestamp, string rawBody, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{rawBody}"))).ToLowerInvariant();
    }

    private static HttpRequestMessage WebhookRequest(string body, string? timestamp = null, string? signature = null, string secret = "")
    {
        var ts = timestamp ?? TimeProvider.System.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/webhooks/kyc/fake", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Webhook-Timestamp", ts);
        request.Headers.Add("X-Webhook-Signature", signature ?? WebhookSignature(ts, body, secret));
        return request;
    }

    private static string KycBody(string eventId, string providerReference, string outcome,
        string? verifiedCountry = null, string? fullName = null, string? dateOfBirth = null, string? rejectionCode = null)
    {
        var parts = new List<string>
        {
            $"\"eventId\":\"{eventId}\"",
            $"\"providerReference\":\"{providerReference}\"",
            $"\"outcome\":\"{outcome}\"",
        };
        if (verifiedCountry is not null)
        {
            parts.Add($"\"verifiedCountry\":\"{verifiedCountry}\"");
        }

        if (fullName is not null)
        {
            parts.Add($"\"fullName\":\"{fullName}\"");
        }

        if (dateOfBirth is not null)
        {
            parts.Add($"\"dateOfBirth\":\"{dateOfBirth}\"");
        }

        if (rejectionCode is not null)
        {
            parts.Add($"\"rejectionCode\":\"{rejectionCode}\"");
        }

        return "{" + string.Join(",", parts) + "}";
    }

    private static async Task<JsonElement> EligibilityAsync(HttpClient client, string token)
    {
        var result = await SendJsonAsync(client, Get("/api/v1/compliance/eligibility", token));
        result.GetProperty("success").GetBoolean().ShouldBeTrue();
        return RequireData(result);
    }

    private static IEnumerable<string> BlockerCodes(JsonElement eligibility, string mode)
        => eligibility.GetProperty("blockers").GetProperty(mode).EnumerateArray()
            .Select(b => b.GetProperty("code").GetString()!);

    [Fact]
    public async Task Compliance_Journey_New_User_To_Real_And_Webhook_Abuse()
    {
        RequireWebhookSecret();
        using var client = Client();
        using var mailpit = Mailpit();
        var email = NewEmail();
        var token = await SignupVerifiedAsync(client, mailpit, email);

        // 1. Fresh user: both modes blocked with explicit reasons.
        var fresh = await EligibilityAsync(client, token);
        fresh.GetProperty("practiceAllowed").GetBoolean().ShouldBeFalse();
        fresh.GetProperty("realAllowed").GetBoolean().ShouldBeFalse();
        BlockerCodes(fresh, "practice").ShouldContain("country_required");
        BlockerCodes(fresh, "practice").ShouldContain("consent_required");

        var practiceDenied = await SendJsonWithStatusAsync(
            client, Get("/api/v1/practice/diagnostics/ping", token), HttpStatusCode.Forbidden);
        RequireError(practiceDenied).GetProperty("code").GetString().ShouldBe("compliance.not_eligible");
        var deniedDetails = RequireError(practiceDenied).GetProperty("details").EnumerateArray()
            .Select(d => d.GetProperty("field").GetString()).ToList();
        deniedDetails.ShouldContain("country_required");
        deniedDetails.ShouldContain("consent_required");

        // 2. Practice consents and an enabled country open Practice only.
        await AcceptDocumentsAsync(client, token, "practice");
        await SetCountryAsync(client, token, "NG");

        var practiceReady = await EligibilityAsync(client, token);
        practiceReady.GetProperty("practiceAllowed").GetBoolean().ShouldBeTrue();
        practiceReady.GetProperty("realAllowed").GetBoolean().ShouldBeFalse();
        BlockerCodes(practiceReady, "real").ShouldContain("consent_required");
        BlockerCodes(practiceReady, "real").ShouldContain("kyc_required");

        var practicePing = await SendJsonWithStatusAsync(
            client, Get("/api/v1/practice/diagnostics/ping", token), HttpStatusCode.OK);
        RequireData(practicePing).GetProperty("mode").GetString().ShouldBe("practice");

        var realStillBlocked = await SendJsonWithStatusAsync(
            client, Get("/api/v1/real/diagnostics/ping", token), HttpStatusCode.Forbidden);
        RequireError(realStillBlocked).GetProperty("code").GetString().ShouldBe("compliance.not_eligible");

        // 3. Real documents, KYC start, signed approval webhook opens Real.
        await AcceptDocumentsAsync(client, token, "real");
        var reference = await StartKycAsync(client, token);

        var approvedBody = KycBody(
            $"evt-{Guid.CreateVersion7():N}", reference, "approved",
            verifiedCountry: "NG", fullName: "Smoke User", dateOfBirth: "1990-01-01");
        var approved = await SendJsonWithStatusAsync(
            client, WebhookRequest(approvedBody, secret: WebhookSecret!), HttpStatusCode.OK);
        approved.GetProperty("success").GetBoolean().ShouldBeTrue();

        var realReady = await EligibilityAsync(client, token);
        realReady.GetProperty("realAllowed").GetBoolean().ShouldBeTrue();
        realReady.GetProperty("kycStatus").GetString().ShouldBe("Approved");

        var realPing = await SendJsonWithStatusAsync(
            client, Get("/api/v1/real/diagnostics/ping", token), HttpStatusCode.OK);
        RequireData(realPing).GetProperty("mode").GetString().ShouldBe("real");

        // 4. Replay is a no-op, bad signature 401, stale timestamp 400.
        var replayBody = KycBody($"evt-{Guid.CreateVersion7():N}", reference, "approved",
            verifiedCountry: "NG", fullName: "Smoke User", dateOfBirth: "1990-01-01");
        var firstReplay = await SendJsonWithStatusAsync(
            client, WebhookRequest(replayBody, secret: WebhookSecret!), HttpStatusCode.OK);
        var secondReplay = await SendJsonWithStatusAsync(
            client, WebhookRequest(replayBody, secret: WebhookSecret!), HttpStatusCode.OK);
        secondReplay.GetProperty("success").GetBoolean().ShouldBeTrue();
        secondReplay.GetProperty("message").GetString().ShouldBe(firstReplay.GetProperty("message").GetString());

        var tamperedBody = KycBody($"evt-{Guid.CreateVersion7():N}", reference, "approved");
        var badSignature = await SendJsonWithStatusAsync(
            client, WebhookRequest(tamperedBody, signature: new string('0', 64), secret: WebhookSecret!),
            HttpStatusCode.Unauthorized);
        RequireError(badSignature).GetProperty("code").GetString().ShouldBe("webhook.invalid_signature");

        var staleTimestamp = (TimeProvider.System.GetUtcNow().ToUnixTimeSeconds() - 3600).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var staleBody = KycBody($"evt-{Guid.CreateVersion7():N}", reference, "approved");
        var stale = await SendJsonWithStatusAsync(
            client, WebhookRequest(staleBody, timestamp: staleTimestamp, secret: WebhookSecret!),
            HttpStatusCode.BadRequest);
        RequireError(stale).GetProperty("code").GetString().ShouldBe("webhook.stale_timestamp");
    }

    [Fact]
    public async Task Compliance_Journey_Rejection_Attempts_Blocked_Country_And_Screening()
    {
        RequireWebhookSecret();
        using var client = Client();
        using var mailpit = Mailpit();

        // Rejection path: three rejected attempts, then the fourth start is refused.
        var rejectEmail = NewEmail();
        var rejectToken = await SignupVerifiedAsync(client, mailpit, rejectEmail);
        await AcceptDocumentsAsync(client, rejectToken, "practice");
        await SetCountryAsync(client, rejectToken, "NG");
        await AcceptDocumentsAsync(client, rejectToken, "real");

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var reference = await StartKycAsync(client, rejectToken);
            var rejectBody = KycBody(
                $"evt-{Guid.CreateVersion7():N}", reference, "rejected",
                verifiedCountry: null, fullName: null, rejectionCode: "document_unreadable");
            var rejected = await SendJsonWithStatusAsync(
                client, WebhookRequest(rejectBody, secret: WebhookSecret!), HttpStatusCode.OK);
            rejected.GetProperty("success").GetBoolean().ShouldBeTrue();
        }

        var exhaustedEligibility = await EligibilityAsync(client, rejectToken);
        BlockerCodes(exhaustedEligibility, "real").ShouldContain("kyc_attempts_exhausted");

        var exhaustedStart = await SendJsonWithStatusAsync(
            client, PostAuth("/api/v1/compliance/kyc/start", null, rejectToken), HttpStatusCode.Conflict);
        RequireError(exhaustedStart).GetProperty("code").GetString().ShouldBe("compliance.kyc_attempts_exhausted");

        // Blocked country: practice and real both refuse with country_not_supported.
        var blockedEmail = NewEmail();
        var blockedToken = await SignupVerifiedAsync(client, mailpit, blockedEmail);
        await AcceptDocumentsAsync(client, blockedToken, "practice");
        await SetCountryAsync(client, blockedToken, "KP");

        var blocked = await EligibilityAsync(client, blockedToken);
        blocked.GetProperty("practiceAllowed").GetBoolean().ShouldBeFalse();
        blocked.GetProperty("realAllowed").GetBoolean().ShouldBeFalse();
        BlockerCodes(blocked, "practice").ShouldContain("country_not_supported");
        BlockerCodes(blocked, "real").ShouldContain("country_not_supported");

        var blockedPing = await SendJsonWithStatusAsync(
            client, Get("/api/v1/practice/diagnostics/ping", blockedToken), HttpStatusCode.Forbidden);
        RequireError(blockedPing).GetProperty("code").GetString().ShouldBe("compliance.not_eligible");
        RequireError(blockedPing).GetProperty("details").EnumerateArray()
            .Select(d => d.GetProperty("field").GetString()).ShouldContain("country_not_supported");

        // Screening hit: approved KYC with the marker name restricts the account.
        var hitEmail = NewEmail();
        var hitToken = await SignupVerifiedAsync(client, mailpit, hitEmail);
        await AcceptDocumentsAsync(client, hitToken, "practice");
        await SetCountryAsync(client, hitToken, "NG");
        await AcceptDocumentsAsync(client, hitToken, "real");
        var hitReference = await StartKycAsync(client, hitToken);
        var hitBody = KycBody(
            $"evt-{Guid.CreateVersion7():N}", hitReference, "approved",
            verifiedCountry: "NG", fullName: "SMOKE-SANCTIONED Person", dateOfBirth: "1985-05-05");
        var hit = await SendJsonWithStatusAsync(
            client, WebhookRequest(hitBody, secret: WebhookSecret!), HttpStatusCode.OK);
        hit.GetProperty("success").GetBoolean().ShouldBeTrue();

        var hitEligibility = await EligibilityAsync(client, hitToken);
        hitEligibility.GetProperty("realAllowed").GetBoolean().ShouldBeFalse();
        BlockerCodes(hitEligibility, "real").ShouldContain("account_restricted");

        var hitPing = await SendJsonWithStatusAsync(
            client, Get("/api/v1/real/diagnostics/ping", hitToken), HttpStatusCode.Forbidden);
        RequireError(hitPing).GetProperty("code").GetString().ShouldBe("compliance.not_eligible");
        RequireError(hitPing).GetProperty("details").EnumerateArray()
            .Select(d => d.GetProperty("field").GetString()).ShouldContain("account_restricted");
    }
}
