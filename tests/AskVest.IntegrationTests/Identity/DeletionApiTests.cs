using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Identity.Contracts;
using AskVest.Modules.Identity.Messaging;
using AskVest.Modules.Identity.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Identity;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class DeletionApiTests(PostgresFixture fixture) : IAsyncLifetime
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

    private static string NewEmail() => $"delete-{Guid.CreateVersion7():N}@example.com";

    private const string GoodPassword = "CorrectHorse9";
    private const string Pin = "482913";

    private static object Device(string id) => new
    {
        installationId = id,
        platform = "android",
        deviceName = "Test Phone",
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

    private static HttpRequestMessage Get(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage PostAuth(string path, object? body, string token, string? key = null)
    {
        var request = Post(path, body, key);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<EmailMessage> WaitForEmailAsync(string to, int skip = 0, string? subjectContains = null)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(30);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var matches = _factory.Emails.Sent
                .Where(m => m.To == to && (subjectContains is null || m.Subject.Contains(subjectContains, StringComparison.OrdinalIgnoreCase)))
                .ToList();
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

    private static async Task<(string AccessToken, Guid SessionId)> LoginWithPinAsync(HttpClient client, string email)
    {
        using var login = await client.SendAsync(
            Post("/api/v1/auth/login", new { email, password = GoodPassword, device = Device("del-device") }),
            TestContext.Current.CancellationToken);
        var loginJson = await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var data = loginJson.GetProperty("data");
        var token = data.GetProperty("accessToken").GetString()!;
        var sessionId = data.GetProperty("sessionId").GetGuid();

        using var setPin = await client.SendAsync(
            PostAuth("/api/v1/account/pin", new { password = GoodPassword, pin = Pin }, token),
            TestContext.Current.CancellationToken);
        setPin.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (token, sessionId);
    }

    private static async Task<string> IssueDeleteGrantAsync(HttpClient client, string token)
    {
        using var response = await client.SendAsync(
            PostAuth("/api/v1/auth/step-up", new { purpose = "delete_account", pin = Pin }, token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return json.GetProperty("data").GetProperty("grantToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> RequestDeletionAsync(HttpClient client, string token, string password, string grant)
    {
        return await client.SendAsync(
            PostAuth("/api/v1/account/deletion-request", new { password, stepUpToken = grant }, token),
            TestContext.Current.CancellationToken);
    }

    private async Task ExecuteSqlAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task Deletion_Request_Requires_Password_And_StepUp_Grant()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);
        var (token, _) = await LoginWithPinAsync(client, email);

        var grant = await IssueDeleteGrantAsync(client, token);

        using var wrongPassword = await RequestDeletionAsync(client, token, "WrongPassword1", grant);
        wrongPassword.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var wrongJson = await wrongPassword.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        wrongJson.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.invalid_credentials");

        var grant2 = await IssueDeleteGrantAsync(client, token);
        using var noGrant = await client.SendAsync(
            PostAuth("/api/v1/account/deletion-request", new { password = GoodPassword, stepUpToken = "bogus" }, token),
            TestContext.Current.CancellationToken);
        noGrant.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var noGrantJson = await noGrant.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        noGrantJson.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.step_up_invalid");

        var grant3 = await IssueDeleteGrantAsync(client, token);
        using var ok = await RequestDeletionAsync(client, token, GoodPassword, grant3);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);

        var status = await ScalarAsync<string>(
            "SELECT status FROM identity.users WHERE normalized_email = @e", ("e", email.ToLowerInvariant()));
        status.ShouldBe("DeletionRequested");
        var scheduled = await ScalarAsync<DateTime>(
            "SELECT deletion_scheduled_for FROM identity.users WHERE normalized_email = @e", ("e", email.ToLowerInvariant()));
        scheduled.ShouldBeGreaterThan(TimeProvider.System.GetUtcNow().UtcDateTime.AddDays(29));

        var alert = await WaitForEmailAsync(email, subjectContains: "deletion");
        alert.Subject.ShouldContain("deletion");
    }

    [Fact]
    public async Task Deletion_Request_With_Blocker_Returns_Reasons()
    {
        await using var blockedFactory = new AuthApiFactory(
            fixture.AppConnectionString,
            configureServices: services => services.AddSingleton<IDeletionBlocker>(new FakeBlocker()));
        blockedFactory.CreateClient();
        var client = blockedFactory.CreateClient();
        var email = NewEmail();

        using var signUp = await client.SendAsync(
            Post("/api/v1/auth/sign-up", new { email, password = GoodPassword }),
            TestContext.Current.CancellationToken);
        signUp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var code = ExtractCode(await WaitForEmailAsyncOn(blockedFactory, email));
        using var verify = await client.SendAsync(
            Post("/api/v1/auth/verify-email", new { email, code }),
            TestContext.Current.CancellationToken);
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (token, _) = await LoginWithPinAsyncOn(client, email);
        var grant = await IssueDeleteGrantOn(client, token);

        using var response = await RequestDeletionAsync(client, token, GoodPassword, grant);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("error").GetProperty("code").GetString().ShouldBe("account.deletion_blocked");
        var details = json.GetProperty("error").GetProperty("details");
        details.GetRawText().ShouldContain("open position");

        var status = await ScalarAsync<string>(
            "SELECT status FROM identity.users WHERE normalized_email = @e", ("e", email.ToLowerInvariant()));
        status.ShouldBe("Active");
    }

    private static async Task<EmailMessage> WaitForEmailAsyncOn(AuthApiFactory factory, string to, int skip = 0)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(30);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var matches = factory.Emails.Sent.Where(m => m.To == to).ToList();
            if (matches.Count > skip)
            {
                return matches[^1];
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"No email captured for {to}.");
    }

    private static async Task<(string AccessToken, Guid SessionId)> LoginWithPinAsyncOn(HttpClient client, string email)
    {
        using var login = await client.SendAsync(
            Post("/api/v1/auth/login", new { email, password = GoodPassword, device = Device("del-device") }),
            TestContext.Current.CancellationToken);
        var loginJson = await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var data = loginJson.GetProperty("data");
        var token = data.GetProperty("accessToken").GetString()!;
        var sessionId = data.GetProperty("sessionId").GetGuid();

        using var setPin = await client.SendAsync(
            PostAuth("/api/v1/account/pin", new { password = GoodPassword, pin = Pin }, token),
            TestContext.Current.CancellationToken);
        setPin.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (token, sessionId);
    }

    private static async Task<string> IssueDeleteGrantOn(HttpClient client, string token)
    {
        using var response = await client.SendAsync(
            PostAuth("/api/v1/auth/step-up", new { purpose = "delete_account", pin = Pin }, token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return json.GetProperty("data").GetProperty("grantToken").GetString()!;
    }

    private sealed class FakeBlocker : IDeletionBlocker
    {
        public Task<IReadOnlyList<string>> GetBlockReasonsAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(["An open position must be closed first."]);
    }

    [Fact]
    public async Task Pending_Deletion_Restricts_Endpoints_Until_Cancelled()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);
        var (token, _) = await LoginWithPinAsync(client, email);

        using var login2 = await client.SendAsync(
            Post("/api/v1/auth/login", new { email, password = GoodPassword, device = Device("del-device") }),
            TestContext.Current.CancellationToken);
        var secondToken = (await login2.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("data").GetProperty("accessToken").GetString()!;

        var grant = await IssueDeleteGrantAsync(client, token);
        using var request = await RequestDeletionAsync(client, token, GoodPassword, grant);
        request.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var sessions = await client.SendAsync(
            Get("/api/v1/account/sessions", token), TestContext.Current.CancellationToken);
        sessions.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var sessionsJson = await sessions.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        sessionsJson.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.account_pending_deletion");

        using var me = await client.SendAsync(
            Get("/api/v1/account/me", token), TestContext.Current.CancellationToken);
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        var meJson = await me.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        meJson.GetProperty("data").GetProperty("status").GetString().ShouldBe("DeletionRequested");
        meJson.GetProperty("data").GetProperty("deletionScheduledFor").GetString().ShouldNotBeNullOrEmpty();

        using var otherMe = await client.SendAsync(
            Get("/api/v1/account/me", secondToken), TestContext.Current.CancellationToken);
        otherMe.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var cancel = await client.SendAsync(
            PostAuth("/api/v1/account/deletion-cancel", new { }, token),
            TestContext.Current.CancellationToken);
        cancel.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var sessionsAfter = await client.SendAsync(
            Get("/api/v1/account/sessions", token), TestContext.Current.CancellationToken);
        sessionsAfter.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var cancelAgain = await client.SendAsync(
            PostAuth("/api/v1/account/deletion-cancel", new { }, token),
            TestContext.Current.CancellationToken);
        cancelAgain.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }


    [Fact]
    public async Task Anonymization_Removes_Personal_Data_And_Is_Idempotent()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);
        var (token, _) = await LoginWithPinAsync(client, email);
        var grant = await IssueDeleteGrantAsync(client, token);
        using var request = await RequestDeletionAsync(client, token, GoodPassword, grant);
        request.StatusCode.ShouldBe(HttpStatusCode.OK);

        var userId = await ScalarAsync<Guid>(
            "SELECT id FROM identity.users WHERE normalized_email = @e", ("e", email.ToLowerInvariant()));

        await ExecuteSqlAsync(
            "UPDATE identity.users SET deletion_scheduled_for = @d WHERE id = @id",
            ("d", TimeProvider.System.GetUtcNow().UtcDateTime.AddDays(-1)), ("id", userId));

        int count;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<AccountAnonymizationService>();
            count = await service.RunOnceAsync(TestContext.Current.CancellationToken);
        }

        count.ShouldBe(1);

        var row = await ScalarAsync<string>(
            "SELECT status || '|' || normalized_email || '|' || COALESCE(password_hash, '<null>') || '|' || COALESCE(pin_hash, '<null>') || '|' || COALESCE(phone_e164, '<null>') FROM identity.users WHERE id = @id",
            ("id", userId));
        var parts = row.Split('|');
        parts[0].ShouldBe("Anonymized");
        parts[1].ShouldBe($"deleted-{userId:N}@anonymized.invalid");
        parts[2].ShouldBe(string.Empty);
        parts[3].ShouldBe("<null>");
        parts[4].ShouldBe("<null>");

        var devices = await ScalarAsync<long>(
            "SELECT COUNT(*) FROM identity.devices WHERE user_id = @id", ("id", userId));
        devices.ShouldBe(0);
        var sessions = await ScalarAsync<long>(
            "SELECT COUNT(*) FROM identity.sessions WHERE user_id = @id", ("id", userId));
        sessions.ShouldBe(0);
        var tokens = await ScalarAsync<long>(
            "SELECT COUNT(*) FROM identity.refresh_tokens rt JOIN identity.sessions s ON s.id = rt.session_id WHERE s.user_id = @id",
            ("id", userId));
        tokens.ShouldBe(0);

        var dirtyEvents = await ScalarAsync<long>(
            "SELECT COUNT(*) FROM identity.security_events WHERE user_id = @id AND (ip IS NOT NULL OR user_agent IS NOT NULL)",
            ("id", userId));
        dirtyEvents.ShouldBe(0);
        var anonymizedEvents = await ScalarAsync<long>(
            "SELECT COUNT(*) FROM identity.security_events WHERE user_id = @id AND event_type = 'AccountAnonymized'",
            ("id", userId));
        anonymizedEvents.ShouldBe(1);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<AccountAnonymizationService>();
            (await service.RunOnceAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        }

        using var me = await client.SendAsync(
            Get("/api/v1/account/me", token), TestContext.Current.CancellationToken);
        me.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Disabled_Account_Token_Is_Rejected()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);
        var (token, _) = await LoginWithPinAsync(client, email);
        var userId = await ScalarAsync<Guid>(
            "SELECT id FROM identity.users WHERE normalized_email = @e", ("e", email.ToLowerInvariant()));
        await ExecuteSqlAsync(
            "UPDATE identity.users SET status = 'Disabled' WHERE id = @id", ("id", userId));

        using var me = await client.SendAsync(
            Get("/api/v1/account/me", token), TestContext.Current.CancellationToken);
        me.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var json = await me.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.account_disabled");
    }
}
