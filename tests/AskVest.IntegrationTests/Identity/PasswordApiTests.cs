using System.Net;
using System.Net.Http.Headers;
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
public sealed class PasswordApiTests(PostgresFixture fixture) : IAsyncLifetime
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

    private static string NewEmail() => $"pw-{Guid.CreateVersion7():N}@example.com";

    private const string GoodPassword = "CorrectHorse9";
    private const string NewPassword = "NewCorrectHorse10";

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

    private static HttpRequestMessage PostAuth(string path, object body, string token)
    {
        var request = Post(path, body);
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

    private static async Task<(string AccessToken, string RefreshToken)> LoginForTokensAsync(
        HttpClient client, string email, string deviceId = "device-1")
    {
        using var response = await client.SendAsync(
            Post("/api/v1/auth/login", new { email, password = GoodPassword, device = Device(deviceId) }),
            TestContext.Current.CancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var data = json.GetProperty("data");
        data.GetProperty("result").GetString().ShouldBe("authenticated");
        return (data.GetProperty("accessToken").GetString()!, data.GetProperty("refreshToken").GetString()!);
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
    public async Task Forgot_And_Reset_Password_Flow()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);
        var (_, refreshToken) = await LoginForTokensAsync(client, email);

        using var forgot = await client.SendAsync(
            Post("/api/v1/auth/forgot-password", new { email }),
            TestContext.Current.CancellationToken);
        forgot.StatusCode.ShouldBe(HttpStatusCode.OK);

        var resetEmail = await WaitForEmailAsync(email, skip: 1);
        resetEmail.Subject.ShouldContain("reset");
        var code = ExtractCode(resetEmail);

        using var reset = await client.SendAsync(
            Post("/api/v1/auth/reset-password", new { email, code, password = NewPassword }),
            TestContext.Current.CancellationToken);
        reset.StatusCode.ShouldBe(HttpStatusCode.OK);

        var sessions = await ScalarLongAsync(
            "SELECT count(*) FROM identity.sessions s JOIN identity.users u ON u.id = s.user_id WHERE u.normalized_email = @p0 AND s.revoked_at IS NULL",
            email);
        sessions.ShouldBe(0);

        using var oldLogin = await client.SendAsync(
            Post("/api/v1/auth/login", new { email, password = GoodPassword, device = Device("device-1") }),
            TestContext.Current.CancellationToken);
        var oldJson = await oldLogin.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        oldJson.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.invalid_credentials");

        using var newLogin = await client.SendAsync(
            Post("/api/v1/auth/login", new { email, password = NewPassword, device = Device("device-1") }),
            TestContext.Current.CancellationToken);
        var newJson = await newLogin.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        newJson.GetProperty("data").GetProperty("result").GetString().ShouldBe("authenticated");

        var alert = await WaitForEmailAsync(email, skip: 2);
        alert.Subject.ShouldContain("password");
    }

    [Fact]
    public async Task Forgot_Password_Is_Uniform_For_Unknown_Email()
    {
        var client = Client();
        using var response = await client.SendAsync(
            Post("/api/v1/auth/forgot-password", new { email = NewEmail() }),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("success").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Reset_Password_Rejects_Invalid_And_Reused_Code()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);

        using var forgot = await client.SendAsync(
            Post("/api/v1/auth/forgot-password", new { email }),
            TestContext.Current.CancellationToken);
        forgot.StatusCode.ShouldBe(HttpStatusCode.OK);
        var code = ExtractCode(await WaitForEmailAsync(email, skip: 1));

        using var bad = await client.SendAsync(
            Post("/api/v1/auth/reset-password", new { email, code = "000000", password = NewPassword }),
            TestContext.Current.CancellationToken);
        var badJson = await bad.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        badJson.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.invalid_code");

        using var good = await client.SendAsync(
            Post("/api/v1/auth/reset-password", new { email, code, password = NewPassword }),
            TestContext.Current.CancellationToken);
        good.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var reuse = await client.SendAsync(
            Post("/api/v1/auth/reset-password", new { email, code, password = "AnotherPass123" }),
            TestContext.Current.CancellationToken);
        var reuseJson = await reuse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        reuseJson.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.invalid_code");
    }

    [Fact]
    public async Task Change_Password_Revokes_Other_Sessions_Keeps_Current()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);
        var (tokenA, _) = await LoginForTokensAsync(client, email, "device-1");
        var (tokenB, _) = await LoginForTokensAsync(client, email, "device-1");

        using var change = await client.SendAsync(
            PostAuth("/api/v1/account/change-password", new { currentPassword = GoodPassword, password = NewPassword }, tokenA),
            TestContext.Current.CancellationToken);
        change.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var logoutA = await client.SendAsync(
            PostAuth("/api/v1/auth/logout", new { }, tokenA),
            TestContext.Current.CancellationToken);
        logoutA.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var logoutB = await client.SendAsync(
            PostAuth("/api/v1/auth/logout", new { }, tokenB),
            TestContext.Current.CancellationToken);
        var bodyB = await logoutB.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        bodyB.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.session_revoked");

        var alert = await WaitForEmailAsync(email, skip: 1);
        alert.Subject.ShouldContain("password");
    }

    [Fact]
    public async Task Change_Password_Requires_Current_Password()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);
        var (token, _) = await LoginForTokensAsync(client, email);

        using var response = await client.SendAsync(
            PostAuth("/api/v1/account/change-password", new { currentPassword = "WrongPass999", password = NewPassword }, token),
            TestContext.Current.CancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.invalid_credentials");
    }
}
