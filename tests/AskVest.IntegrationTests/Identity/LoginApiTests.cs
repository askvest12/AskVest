using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Identity.Messaging;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Identity;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class LoginApiTests(PostgresFixture fixture) : IAsyncLifetime
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

    private static string NewEmail() => $"login-{Guid.CreateVersion7():N}@example.com";

    private const string GoodPassword = "CorrectHorse9";

    private static object Device(string id) => new
    {
        installationId = id,
        platform = "android",
        deviceName = "Test Phone",
        appVersion = "1.0.0",
    };

    private static HttpRequestMessage Post(string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", $"k-{Guid.CreateVersion7():N}");
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

    private async Task<string> SignUpVerifyAndGetCodeAsync(HttpClient client, string email)
    {
        using var signUp = await client.SendAsync(
            Post("/api/v1/auth/sign-up", new { email, password = GoodPassword }),
            TestContext.Current.CancellationToken);
        signUp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var verification = await WaitForEmailAsync(email);
        return ExtractCode(verification);
    }

    private async Task ActivateAsync(HttpClient client, string email)
    {
        var code = await SignUpVerifyAndGetCodeAsync(client, email);
        using var response = await client.SendAsync(
            Post("/api/v1/auth/verify-email", new { email, code }),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<JsonElement> LoginAsync(HttpClient client, string email, string password, string deviceId)
    {
        using var response = await client.SendAsync(
            Post("/api/v1/auth/login", new { email, password, device = Device(deviceId) }),
            TestContext.Current.CancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
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
    public async Task Login_First_Device_Is_AutoTrusted_And_Returns_Tokens()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);

        var json = await LoginAsync(client, email, GoodPassword, "device-1");
        json.GetProperty("success").GetBoolean().ShouldBeTrue();
        var data = json.GetProperty("data");
        data.GetProperty("result").GetString().ShouldBe("authenticated");
        data.GetProperty("accessToken").GetString().ShouldNotBeNullOrEmpty();
        data.GetProperty("refreshToken").GetString().ShouldNotBeNullOrEmpty();
        data.GetProperty("sessionId").GetString().ShouldNotBeNullOrEmpty();

        var sessions = await ScalarLongAsync(
            "SELECT count(*) FROM identity.sessions s JOIN identity.users u ON u.id = s.user_id WHERE u.normalized_email = @p0 AND s.revoked_at IS NULL",
            email.ToLowerInvariant());
        sessions.ShouldBe(1);

        var refreshTokens = await ScalarLongAsync(
            "SELECT count(*) FROM identity.refresh_tokens rt JOIN identity.sessions s ON s.id = rt.session_id JOIN identity.users u ON u.id = s.user_id WHERE u.normalized_email = @p0",
            email.ToLowerInvariant());
        refreshTokens.ShouldBe(1);
    }

    [Fact]
    public async Task Login_Second_Device_Requires_Verification_Then_Authenticates()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);
        await LoginAsync(client, email, GoodPassword, "device-1");

        var challenge = await LoginAsync(client, email, GoodPassword, "device-2");
        var data = challenge.GetProperty("data");
        data.GetProperty("result").GetString().ShouldBe("device_verification_required");
        var challengeId = data.GetProperty("challengeId").GetGuid();
        data.GetProperty("expiresAt").GetDateTimeOffset().ShouldBeGreaterThan(TimeProvider.System.GetUtcNow());

        var deviceEmail = await WaitForEmailAsync(email, skip: 1);
        var code = ExtractCode(deviceEmail);
        deviceEmail.Subject.ShouldContain("device");

        using var verifyResponse = await client.SendAsync(
            Post("/api/v1/auth/verify-device", new { challengeId, code }),
            TestContext.Current.CancellationToken);
        var verified = await verifyResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        verified.GetProperty("data").GetProperty("result").GetString().ShouldBe("authenticated");
        verified.GetProperty("data").GetProperty("accessToken").GetString().ShouldNotBeNullOrEmpty();

        var alert = await WaitForEmailAsync(email, skip: 2);
        alert.Subject.ShouldContain("sign-in");
    }

    [Fact]
    public async Task Login_Wrong_Password_And_Unknown_Email_Are_Uniform()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);

        var wrong = await LoginAsync(client, email, "WrongPass999", "device-x");
        var unknown = await LoginAsync(client, NewEmail(), GoodPassword, "device-y");

        wrong.GetProperty("success").GetBoolean().ShouldBeFalse();
        unknown.GetProperty("success").GetBoolean().ShouldBeFalse();
        wrong.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.invalid_credentials");
        unknown.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.invalid_credentials");
        wrong.GetProperty("message").GetString().ShouldBe(unknown.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Login_Unverified_Email_Returns_Email_Not_Verified()
    {
        var client = Client();
        var email = NewEmail();
        using var signUp = await client.SendAsync(
            Post("/api/v1/auth/sign-up", new { email, password = GoodPassword }),
            TestContext.Current.CancellationToken);
        signUp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await LoginAsync(client, email, GoodPassword, "device-1");
        json.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.email_not_verified");
    }

    [Fact]
    public async Task Login_Throttles_After_Failure_Limit_With_Retry_After()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);

        for (var i = 0; i < 5; i++)
        {
            var json = await LoginAsync(client, email, "WrongPass999", "device-t");
            json.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.invalid_credentials");
        }

        using var response = await client.SendAsync(
            Post("/api/v1/auth/login", new { email, password = GoodPassword, device = Device("device-t") }),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter.ShouldNotBeNull();
        var throttledJson = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        throttledJson.GetProperty("error").GetProperty("code").GetString().ShouldBe("rate_limit.exceeded");

        var throttled = await ScalarLongAsync(
            "SELECT count(*) FROM identity.security_events WHERE event_type = 'LoginThrottled'");
        throttled.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Login_Success_Resets_Throttle()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);

        for (var i = 0; i < 4; i++)
        {
            await LoginAsync(client, email, "WrongPass999", "device-r");
        }

        var success = await LoginAsync(client, email, GoodPassword, "device-r");
        success.GetProperty("data").GetProperty("result").GetString().ShouldBe("authenticated");

        for (var i = 0; i < 4; i++)
        {
            var json = await LoginAsync(client, email, "WrongPass999", "device-r");
            json.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.invalid_credentials");
        }

        var stillOk = await LoginAsync(client, email, GoodPassword, "device-r");
        stillOk.GetProperty("data").GetProperty("result").GetString().ShouldBe("authenticated");
    }

    [Fact]
    public async Task VerifyDevice_Wrong_Code_Returns_Invalid_Code()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);
        await LoginAsync(client, email, GoodPassword, "device-1");
        var challenge = await LoginAsync(client, email, GoodPassword, "device-2");
        var challengeId = challenge.GetProperty("data").GetProperty("challengeId").GetGuid();

        using var response = await client.SendAsync(
            Post("/api/v1/auth/verify-device", new { challengeId, code = "000000" }),
            TestContext.Current.CancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.invalid_code");
    }
}
