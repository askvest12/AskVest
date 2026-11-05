using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Identity.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Identity;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class SessionApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private AuthApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new AuthApiFactory(fixture.AppConnectionString);
        _factory.CreateClient();
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient Client() => _factory.CreateClient();

    private static string NewEmail() => $"session-{Guid.CreateVersion7():N}@example.com";

    private const string GoodPassword = "CorrectHorse9";

    private static object Device(string id) => new
    {
        installationId = id,
        platform = "android",
        deviceName = "Test Phone",
        appVersion = "1.0.0",
    };

    private static HttpRequestMessage Post(string path, object body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", key ?? $"k-{Guid.CreateVersion7():N}");
        return request;
    }

    private static HttpRequestMessage PostAuth(string path, object body, string token, string? key = null)
    {
        var request = Post(path, body, key);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<EmailMessage> WaitForEmailAsync(string to, int skip = 0)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(30);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var matches = _factory.Emails.Sent.Where(m => m.To == to).ToList();
            if (matches.Count > skip)
            {
                return matches[^1];
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"No email captured for {to}.");
    }

    private static string ExtractCode(EmailMessage email)
    {
        var match = Regex.Match(email.TextBody, @"code is (\d{6})");
        match.Success.ShouldBeTrue();
        return match.Groups[1].Value;
    }

    private async Task ActivateAsync(HttpClient client, string email)
    {
        using var signUp = await client.SendAsync(
            Post("/api/v1/auth/sign-up", new { email, password = GoodPassword }),
            TestContext.Current.CancellationToken);
        signUp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var code = ExtractCode(await WaitForEmailAsync(email));
        using var verify = await client.SendAsync(
            Post("/api/v1/auth/verify-email", new { email, code }),
            TestContext.Current.CancellationToken);
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<JsonElement> LoginAsync(HttpClient client, string email, string deviceId = "device-1")
    {
        using var response = await client.SendAsync(
            Post("/api/v1/auth/login", new { email, password = GoodPassword, device = Device(deviceId) }),
            TestContext.Current.CancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private static async Task<(string AccessToken, string RefreshToken, Guid SessionId)> LoginForTokensAsync(
        HttpClient client, string email, string deviceId = "device-1")
    {
        var json = await LoginAsync(client, email, deviceId);
        var data = json.GetProperty("data");
        data.GetProperty("result").GetString().ShouldBe("authenticated");
        return (
            data.GetProperty("accessToken").GetString()!,
            data.GetProperty("refreshToken").GetString()!,
            data.GetProperty("sessionId").GetGuid());
    }

    private static async Task<JsonElement> RefreshAsync(HttpClient client, string refreshToken, string installationId = "device-1")
    {
        using var response = await client.SendAsync(
            Post("/api/v1/auth/refresh", new { refreshToken, installationId }),
            TestContext.Current.CancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> LogoutAsync(HttpClient client, string accessToken, string? key = null)
    {
        return await client.SendAsync(
            PostAuth("/api/v1/auth/logout", new { }, accessToken, key),
            TestContext.Current.CancellationToken);
    }

    private async Task<long> ScalarLongAsync(string sql, params object[] args)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        for (var i = 0; i < args.Length; i++)
        {
            command.Parameters.AddWithValue($"p{i}", args[i]);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Refresh_Rotates_Token_Pair_And_Old_Token_Fails()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);
        var (_, refreshToken, sessionId) = await LoginForTokensAsync(client, email);

        var refreshed = await RefreshAsync(client, refreshToken);
        refreshed.GetProperty("success").GetBoolean().ShouldBeTrue();
        var data = refreshed.GetProperty("data");
        data.GetProperty("refreshToken").GetString().ShouldNotBe(refreshToken);
        data.GetProperty("sessionId").GetGuid().ShouldBe(sessionId);

        var oldAgain = await RefreshAsync(client, refreshToken);
        oldAgain.GetProperty("success").GetBoolean().ShouldBeFalse();
        oldAgain.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.refresh_conflict");

        var newPair = await RefreshAsync(client, data.GetProperty("refreshToken").GetString()!);
        newPair.GetProperty("success").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Refresh_With_Unknown_Token_Returns_Invalid()
    {
        var client = Client();
        var json = await RefreshAsync(client, "not-a-real-token");
        json.GetProperty("success").GetBoolean().ShouldBeFalse();
        json.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.refresh_invalid");
    }

    [Fact]
    public async Task Parallel_Refresh_Issues_Exactly_One_Pair()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);
        var (_, refreshToken, _) = await LoginForTokensAsync(client, email);

        var results = await Task.WhenAll(
            RefreshAsync(client, refreshToken),
            RefreshAsync(client, refreshToken),
            RefreshAsync(client, refreshToken));

        results.Count(r => r.GetProperty("success").GetBoolean()).ShouldBe(1);
        results.Count(r => !r.GetProperty("success").GetBoolean()
            && r.GetProperty("error").GetProperty("code").GetString() == "auth.refresh_conflict").ShouldBe(2);
    }

    [Fact]
    public async Task Refresh_Reuse_After_Grace_Revokes_Session_And_Alerts()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);
        var (accessToken, refreshToken, sessionId) = await LoginForTokensAsync(client, email);

        var first = await RefreshAsync(client, refreshToken);
        first.GetProperty("success").GetBoolean().ShouldBeTrue();

        await Task.Delay(TimeSpan.FromSeconds(11), TestContext.Current.CancellationToken);

        var reuse = await RefreshAsync(client, refreshToken);
        reuse.GetProperty("success").GetBoolean().ShouldBeFalse();
        reuse.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.refresh_invalid");

        var revoked = await ScalarLongAsync(
            "SELECT count(*) FROM identity.sessions WHERE id = @p0 AND revoked_at IS NOT NULL AND revoked_reason = 'refresh_reuse'",
            sessionId);
        revoked.ShouldBe(1);

        var events = await ScalarLongAsync(
            "SELECT count(*) FROM identity.security_events WHERE event_type = 'RefreshReuseDetected'");
        events.ShouldBeGreaterThanOrEqualTo(1);

        var alert = await WaitForEmailAsync(email, skip: 1);
        alert.Subject.ShouldContain("signed out");

        using var authed = await LogoutAsync(client, accessToken);
        var body = await authed.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.session_revoked");
    }

    [Fact]
    public async Task Logout_Revokes_Session_And_Refresh_Token()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);
        var (accessToken, refreshToken, sessionId) = await LoginForTokensAsync(client, email);

        var key = $"logout-{Guid.CreateVersion7():N}";
        using var logout = await LogoutAsync(client, accessToken, key);
        logout.StatusCode.ShouldBe(HttpStatusCode.OK);

        var replay = await LogoutAsync(client, accessToken, key);
        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var replayBody = await replay.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        replayBody.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.session_revoked");

        var refresh = await RefreshAsync(client, refreshToken);
        refresh.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.refresh_invalid");

        using var again = await LogoutAsync(client, accessToken);
        var body = await again.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.session_revoked");
    }

    [Fact]
    public async Task Access_Token_Without_Session_Is_Rejected()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);
        var (accessToken, _, _) = await LoginForTokensAsync(client, email);

        using var response = await LogoutAsync(client, accessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var second = await LogoutAsync(client, accessToken);
        second.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        second.Headers.GetValues("WWW-Authenticate").ShouldContain(h => h.Contains("Bearer"));
    }

    [Fact]
    public async Task Idempotency_Scope_Is_Per_User()
    {
        var client = Client();
        var emailA = NewEmail();
        var emailB = NewEmail();
        await ActivateAsync(client, emailA);
        await ActivateAsync(client, emailB);
        var (tokenA, _, _) = await LoginForTokensAsync(client, emailA, "device-a");
        var (tokenB, _, _) = await LoginForTokensAsync(client, emailB, "device-b");

        var sharedKey = $"shared-{Guid.CreateVersion7():N}";
        using var first = await LogoutAsync(client, tokenA, sharedKey);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var second = await LogoutAsync(client, tokenB, sharedKey);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.Headers.Contains("Idempotent-Replay").ShouldBeFalse();
    }

    [Fact]
    public async Task Eleventh_Session_Evicts_The_Oldest()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);

        var sessionIds = new List<Guid>();
        for (var i = 0; i < 11; i++)
        {
            var (_, _, sessionId) = await LoginForTokensAsync(client, email, "device-cap");
            sessionIds.Add(sessionId);
        }

        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/account/sessions", UriKind.Relative))
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", (await LoginForTokensAsync(client, email, "device-cap")).AccessToken) },
            },
            TestContext.Current.CancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var active = json.GetProperty("data").EnumerateArray()
            .Select(s => s.GetProperty("id").GetGuid())
            .ToList();

        active.Count.ShouldBe(10);
        active.ShouldNotContain(sessionIds[0], "the oldest session must be evicted");
        active.ShouldContain(sessionIds[^1]);
    }

    [Fact]
    public async Task Tokens_In_Wrong_Places_Are_Rejected()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);
        var (accessToken, refreshToken, _) = await LoginForTokensAsync(client, email);

        using var refreshAsBearer = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/account/me", UriKind.Relative))
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", refreshToken) },
            },
            TestContext.Current.CancellationToken);
        refreshAsBearer.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var queryString = await client.GetAsync(
            new Uri($"/api/v1/account/me?access_token={Uri.EscapeDataString(accessToken)}", UriKind.Relative),
            TestContext.Current.CancellationToken);
        queryString.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var tampered = accessToken[..^1] + (accessToken[^1] == 'A' ? 'B' : 'A');
        using var tamperedResponse = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/account/me", UriKind.Relative))
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", tampered) },
            },
            TestContext.Current.CancellationToken);
        tamperedResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task UseFakeClockAsync(FakeTimeProvider clock)
    {
        await _factory.DisposeAsync();
        _factory = new AuthApiFactory(
            fixture.AppConnectionString,
            configureServices: services => services.Replace(
                ServiceDescriptor.Singleton<TimeProvider>(_ => clock)));
        _factory.CreateClient();
    }

    [Fact]
    public async Task Refresh_Idle_And_Absolute_Expiries_Are_Enforced()
    {
        var clock = new FakeTimeProvider(TimeProvider.System.GetUtcNow());
        await UseFakeClockAsync(clock);
        var client = Client();

        var email = NewEmail();
        await ActivateAsync(client, email);
        var (_, refreshToken, _) = await LoginForTokensAsync(client, email);

        clock.Advance(TimeSpan.FromDays(31));
        var idleExpired = await RefreshAsync(client, refreshToken);
        idleExpired.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.refresh_invalid");

        var (_, refreshToken2, _) = await LoginForTokensAsync(client, email);
        clock.Advance(TimeSpan.FromDays(91));
        var absoluteExpired = await RefreshAsync(client, refreshToken2);
        absoluteExpired.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.refresh_invalid");
    }

    [Fact]
    public async Task Access_Token_Expiry_Is_Enforced()
    {
        var clock = new FakeTimeProvider(TimeProvider.System.GetUtcNow());
        await UseFakeClockAsync(clock);
        var client = Client();

        var email = NewEmail();
        await ActivateAsync(client, email);
        var (accessToken, _, _) = await LoginForTokensAsync(client, email);

        clock.Advance(TimeSpan.FromMinutes(11));
        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/account/me", UriKind.Relative))
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", accessToken) },
            },
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
