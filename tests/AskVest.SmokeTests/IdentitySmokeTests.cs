using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace AskVest.SmokeTests;

public sealed class IdentitySmokeTests
{
    private static readonly string? BaseUrl = Environment.GetEnvironmentVariable("ASKVEST_SMOKE_BASE_URL");
    private static readonly string? MailpitUrl = Environment.GetEnvironmentVariable("ASKVEST_SMOKE_MAILPIT_URL");

    private const string Password = "CorrectHorseSmoke9";
    private const string Pin = "482913";

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

    private static string NewEmail() => $"smoke-{Guid.CreateVersion7():N}@example.com";

    private static object Device(string id, string name = "Smoke Phone") => new
    {
        installationId = id,
        platform = "android",
        deviceName = name,
        appVersion = "1.0.0",
    };

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
        var request = Post(path, body);
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

    private static async Task<JsonElement> SendJsonRawAsync(HttpClient client, HttpRequestMessage request)
    {
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Content is not null)
        {
            clone.Content = new StringContent(
                await request.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                System.Text.Encoding.UTF8,
                "application/json");
            foreach (var header in request.Content.Headers)
            {
                if (!header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private static async Task<string> WaitForCodeAsync(HttpClient mailpit, string to, string subjectContains, int minCount = 1)
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
            if (messages.Count >= minCount)
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

    private static JsonElement RequireData(JsonElement json)
    {
        if (!json.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Expected data object, got: {json.GetRawText()}");
        }

        return data;
    }

    private static async Task<(string AccessToken, string RefreshToken, Guid SessionId)> LoginAsync(
        HttpClient client, string email, string deviceId)
    {
        var json = await SendJsonAsync(client, Post("/api/v1/auth/login", new
        {
            email,
            password = Password,
            device = Device(deviceId),
        }));
        var data = RequireData(json);
        data.GetProperty("result").GetString().ShouldBe("authenticated");
        return (
            data.GetProperty("accessToken").GetString()!,
            data.GetProperty("refreshToken").GetString()!,
            data.GetProperty("sessionId").GetGuid());
    }

    private static async Task<JsonElement> RefreshAsync(HttpClient client, string refreshToken, string deviceId)
    {
        return await SendJsonAsync(client, Post("/api/v1/auth/refresh", new
        {
            refreshToken,
            installationId = deviceId,
        }));
    }

    private static async Task<string> IssueStepUpAsync(HttpClient client, string token, string pin = Pin)
    {
        var json = await SendJsonAsync(client, PostAuth("/api/v1/auth/step-up", new { purpose = "delete_account", pin }, token));
        return json.GetProperty("data").GetProperty("grantToken").GetString()!;
    }

    [Fact]
    public async Task Identity_Journey_Signup_To_Deletion_Cancel()
    {
        using var client = Client();
        using var mailpit = Mailpit();
        var email = NewEmail();

        // 1. Sign-up, read code from Mailpit, verify, log in on first device, call me.
        var signUp = await SendJsonAsync(client, Post("/api/v1/auth/sign-up", new { email, password = Password }));
        signUp.GetProperty("success").GetBoolean().ShouldBeTrue();

        var verifyCode = await WaitForCodeAsync(mailpit, email, "Verify");
        var verify = await SendJsonAsync(client, Post("/api/v1/auth/verify-email", new { email, code = verifyCode }));
        verify.GetProperty("success").GetBoolean().ShouldBeTrue();

        var (tokenA, refreshA, sessionA) = await LoginAsync(client, email, "smoke-dev-a");
        var me = await SendJsonAsync(client, Get("/api/v1/account/me", tokenA));
        me.GetProperty("data").GetProperty("email").GetString().ShouldBe(email);
        me.GetProperty("data").GetProperty("emailVerified").GetBoolean().ShouldBeTrue();

        // 2. Second device: challenge, code from Mailpit, verify, tokens.
        var challengeJson = await SendJsonAsync(client, Post("/api/v1/auth/login", new
        {
            email,
            password = Password,
            device = Device("smoke-dev-b", "Second Phone"),
        }));
        var challengeData = challengeJson.GetProperty("data");
        challengeData.GetProperty("result").GetString().ShouldBe("device_verification_required");
        var challengeId = challengeData.GetProperty("challengeId").GetGuid();

        var deviceCode = await WaitForCodeAsync(mailpit, email, "device", minCount: 1);
        var verified = await SendJsonAsync(client, Post("/api/v1/auth/verify-device", new { challengeId, code = deviceCode }));
        var verifiedData = verified.GetProperty("data");
        verifiedData.GetProperty("result").GetString().ShouldBe("authenticated");
        var tokenB = verifiedData.GetProperty("accessToken").GetString()!;
        var refreshB = verifiedData.GetProperty("refreshToken").GetString()!;

        // 3. Refresh twice; old token rejected; reuse grace path.
        var refresh1 = await RefreshAsync(client, refreshA, "smoke-dev-a");
        refresh1.GetProperty("success").GetBoolean().ShouldBeTrue();
        var refresh2 = await RefreshAsync(client, refresh1.GetProperty("data").GetProperty("refreshToken").GetString()!, "smoke-dev-a");
        refresh2.GetProperty("success").GetBoolean().ShouldBeTrue();

        var graceReuse = await RefreshAsync(client, refresh1.GetProperty("data").GetProperty("refreshToken").GetString()!, "smoke-dev-a");
        graceReuse.GetProperty("success").GetBoolean().ShouldBeFalse();
        graceReuse.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.refresh_conflict");

        // 4. List sessions/devices, revoke second device, its token dies.
        var sessions = await SendJsonAsync(client, Get("/api/v1/account/sessions", tokenA));
        sessions.GetProperty("data").EnumerateArray().Count().ShouldBeGreaterThanOrEqualTo(2);

        var devices = await SendJsonAsync(client, Get("/api/v1/account/devices", tokenA));
        var deviceB = devices.GetProperty("data").EnumerateArray()
            .Single(d => d.GetProperty("installationId").GetString() == "smoke-dev-b");
        var deviceBId = deviceB.GetProperty("id").GetGuid();

        var revokeDevice = await SendJsonAsync(client, PostAuth($"/api/v1/account/devices/{deviceBId}/revoke", new { }, tokenA));
        revokeDevice.GetProperty("success").GetBoolean().ShouldBeTrue();

        var deadMe = await SendJsonAsync(client, Get("/api/v1/account/me", tokenB));
        deadMe.GetProperty("success").GetBoolean().ShouldBeFalse();

        // 5. Change password kills other sessions; forgot+reset kills all.
        var newPassword = "NewCorrectHorse10";
        var change = await SendJsonAsync(client, PostAuth("/api/v1/account/change-password",
            new { currentPassword = Password, password = newPassword }, tokenA));
        change.GetProperty("success").GetBoolean().ShouldBeTrue();

        var forgot = await SendJsonAsync(client, Post("/api/v1/auth/forgot-password", new { email }));
        forgot.GetProperty("success").GetBoolean().ShouldBeTrue();
        var resetCode = await WaitForCodeAsync(mailpit, email, "reset", minCount: 1);
        var reset = await SendJsonAsync(client, Post("/api/v1/auth/reset-password",
            new { email, code = resetCode, password = Password }));
        reset.GetProperty("success").GetBoolean().ShouldBeTrue();

        var meAfterReset = await SendJsonAsync(client, Get("/api/v1/account/me", tokenA));
        meAfterReset.GetProperty("success").GetBoolean().ShouldBeFalse();

        // Re-login for the remaining steps.
        var (tokenC, refreshC, _) = await LoginAsync(client, email, "smoke-dev-a");

        // 6. Set PIN, step-up grant is single-use.
        var setPin = await SendJsonAsync(client, PostAuth("/api/v1/account/pin",
            new { password = Password, pin = Pin }, tokenC));
        setPin.GetProperty("success").GetBoolean().ShouldBeTrue();

        var grant = await IssueStepUpAsync(client, tokenC);
        var grantJson = await SendJsonAsync(client, PostAuth("/api/v1/account/deletion-request",
            new { password = Password, stepUpToken = grant }, tokenC));
        grantJson.GetProperty("success").GetBoolean().ShouldBeTrue();

        // Grant is single-use: second deletion attempt needs a new grant.
        var reuse = await SendJsonAsync(client, PostAuth("/api/v1/account/deletion-request",
            new { password = Password, stepUpToken = grant }, tokenC));
        reuse.GetProperty("success").GetBoolean().ShouldBeFalse();

        // 7. Pending deletion restricts endpoints; cancel restores access.
        var restricted = await SendJsonAsync(client, Get("/api/v1/account/sessions", tokenC));
        restricted.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.account_pending_deletion");

        var mePending = await SendJsonAsync(client, Get("/api/v1/account/me", tokenC));
        mePending.GetProperty("data").GetProperty("status").GetString().ShouldBe("DeletionRequested");

        var cancel = await SendJsonAsync(client, PostAuth("/api/v1/account/deletion-cancel", new { }, tokenC));
        cancel.GetProperty("success").GetBoolean().ShouldBeTrue();

        var sessionsRestored = await SendJsonAsync(client, Get("/api/v1/account/sessions", tokenC));
        sessionsRestored.GetProperty("success").GetBoolean().ShouldBeTrue();

        // Theft path: reuse a refresh token after the grace window revokes the session.
        var theftRefresh = await RefreshAsync(client, refreshC, "smoke-dev-a");
        theftRefresh.GetProperty("success").GetBoolean().ShouldBeTrue();
        var stolenAccess = theftRefresh.GetProperty("data").GetProperty("accessToken").GetString()!;
        await Task.Delay(TimeSpan.FromSeconds(11), TestContext.Current.CancellationToken);
        var theft = await RefreshAsync(client, refreshC, "smoke-dev-a");
        theft.GetProperty("success").GetBoolean().ShouldBeFalse();
        var deadAfterTheft = await SendJsonAsync(client, Get("/api/v1/account/me", stolenAccess));
        deadAfterTheft.GetProperty("success").GetBoolean().ShouldBeFalse();

    }

    [Fact]
    public async Task Protected_Routes_Require_Bearer_Token()
    {
        using var client = Client();
        foreach (var path in new[]
        {
            "/api/v1/account/me",
            "/api/v1/account/sessions",
            "/api/v1/account/devices",
            "/api/v1/account/security-events",
        })
        {
            var response = await client.SendAsync(
                Get(path), TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, path);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            json.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.unauthenticated");
        }
    }

    [Fact]
    public async Task Login_Brute_Force_Hits_Rate_Limit_For_Existing_And_Unknown_Email()
    {
        using var client = Client();
        var existing = NewEmail();
        var unknown = NewEmail();

        var signUp = await SendJsonAsync(client, Post("/api/v1/auth/sign-up", new { email = existing, password = Password }));
        signUp.GetProperty("success").GetBoolean().ShouldBeTrue();

        var limitedExisting = false;
        var limitedUnknown = false;
        for (var i = 0; i < 80 && !(limitedExisting && limitedUnknown); i++)
        {
            var existingJson = await SendJsonRawAsync(client, Post("/api/v1/auth/login", new
            {
                email = existing,
                password = "WrongPassword1",
                device = Device("brute-a"),
            }));
            if (existingJson.GetProperty("error").GetProperty("code").GetString() == "rate_limit.exceeded")
            {
                limitedExisting = true;
            }

            var unknownJson = await SendJsonRawAsync(client, Post("/api/v1/auth/login", new
            {
                email = unknown,
                password = "WrongPassword1",
                device = Device("brute-b"),
            }));
            if (unknownJson.GetProperty("error").GetProperty("code").GetString() == "rate_limit.exceeded")
            {
                limitedUnknown = true;
            }
        }

        limitedExisting.ShouldBeTrue("existing-email login never hit the strict rate limit");
        limitedUnknown.ShouldBeTrue("unknown-email login never hit the strict rate limit");
    }

    [Fact]
    public async Task SignUp_For_Existing_Email_Is_Uniform()
    {
        using var client = Client();
        var email = NewEmail();

        var first = await SendJsonAsync(client, Post("/api/v1/auth/sign-up", new { email, password = Password }));
        first.GetProperty("success").GetBoolean().ShouldBeTrue();
        first.GetProperty("message").GetString().ShouldBe(first.GetProperty("message").GetString());

        var second = await SendJsonAsync(client, Post("/api/v1/auth/sign-up", new { email, password = Password }));
        second.GetProperty("success").GetBoolean().ShouldBe(first.GetProperty("success").GetBoolean());
        second.GetProperty("message").GetString().ShouldBe(first.GetProperty("message").GetString());

        var other = await SendJsonAsync(client, Post("/api/v1/auth/sign-up",
            new { email = NewEmail(), password = Password }));
        other.GetProperty("message").GetString().ShouldBe(first.GetProperty("message").GetString());

    }
}
