using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Identity.Messaging;
using AskVest.Modules.Identity.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Identity;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class MaintenanceApiTests(PostgresFixture fixture) : IAsyncLifetime
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

    private static string NewEmail() => $"maint-{Guid.CreateVersion7():N}@example.com";

    private const string GoodPassword = "CorrectHorse9";

    private static object Device(string id) => new
    {
        installationId = id,
        platform = "android",
        deviceName = "Test Phone",
        appVersion = "1.0.0",
    };

    private static System.Net.Http.HttpRequestMessage Post(string path, object body)
    {
        var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", $"k-{Guid.CreateVersion7():N}");
        return request;
    }

    private async Task<EmailMessage> WaitForEmailAsync(string to)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(30);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var match = _factory.Emails.Sent.LastOrDefault(m => m.To == to);
            if (match is not null)
            {
                return match;
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

    private async Task<Guid> CreateVerifiedUserAsync(HttpClient client, string email)
    {
        using var signUp = await client.SendAsync(
            Post("/api/v1/auth/sign-up", new { email, password = GoodPassword }),
            TestContext.Current.CancellationToken);
        signUp.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var code = ExtractCode(await WaitForEmailAsync(email));
        using var verify = await client.SendAsync(
            Post("/api/v1/auth/verify-email", new { email, code }),
            TestContext.Current.CancellationToken);
        verify.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        return await ScalarAsync<Guid>(
            "SELECT id FROM identity.users WHERE normalized_email = @e", ("e", email.ToLowerInvariant()));
    }

    private static async Task<(string AccessToken, string RefreshToken, Guid SessionId)> LoginAsync(HttpClient client, string email)
    {
        using var response = await client.SendAsync(
            Post("/api/v1/auth/login", new { email, password = GoodPassword, device = Device("maint-device") }),
            TestContext.Current.CancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var data = json.GetProperty("data");
        return (
            data.GetProperty("accessToken").GetString()!,
            data.GetProperty("refreshToken").GetString()!,
            data.GetProperty("sessionId").GetGuid());
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

    private async Task<MaintenanceResult> RunMaintenanceAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<MaintenanceService>();
        return await service.RunOnceAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Maintenance_Removes_Only_Expired_Rows_And_Keeps_Live_And_Rotated_Tokens()
    {
        var client = Client();
        var email = NewEmail();
        var userId = await CreateVerifiedUserAsync(client, email);
        var (_, refreshToken, sessionId) = await LoginAsync(client, email);

        using var refreshResponse = await client.SendAsync(
            Post("/api/v1/auth/refresh", new { refreshToken, installationId = "maint-device" }),
            TestContext.Current.CancellationToken);
        refreshResponse.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        var rotatedTokenCount = await ScalarAsync<long>(
            "SELECT COUNT(*) FROM identity.refresh_tokens WHERE session_id = @s AND rotated_at IS NOT NULL",
            ("s", sessionId));
        rotatedTokenCount.ShouldBe(1);

        var secondLogin = await LoginAsync(client, email);
        await ExecuteSqlAsync(
            "UPDATE identity.sessions SET absolute_expires_at = @past WHERE id = @id",
            ("past", TimeProvider.System.GetUtcNow().UtcDateTime.AddDays(-1)), ("id", secondLogin.SessionId));

        var now = TimeProvider.System.GetUtcNow();
        var past = now.UtcDateTime.AddDays(-1);
        var future = now.UtcDateTime.AddDays(1);

        await ExecuteSqlAsync(
            """
            INSERT INTO identity.one_time_codes (id, user_id, purpose, destination_type, destination, code_hash, attempts_used, attempts_allowed, expires_at, created_at)
            VALUES (@e1, @u, 'PasswordReset', 'Email', 'x@example.com', 'h', 0, 5, @past, @now),
                   (@e2, @u, 'PasswordReset', 'Email', 'x@example.com', 'h', 0, 5, @future, @now);
            INSERT INTO identity.step_up_grants (id, user_id, session_id, purpose, token_hash, expires_at, created_at)
            VALUES (@g1, @u, @s, 'delete_account', 'h1', @past, @now),
                   (@g2, @u, @s, 'delete_account', 'h2', @future, @now);
            INSERT INTO identity.throttle_buckets (id, kind, key_hash, failure_count, window_start, locked_until, lock_repeat_count)
            VALUES (@b1, 'Account', 'kh1', 3, @old, NULL, 0),
                   (@b2, 'Account', 'kh2', 3, @now2, NULL, 0),
                   (@b3, 'Account', 'kh3', 9, @old, @future, 0);
            """,
            ("e1", Guid.CreateVersion7()), ("e2", Guid.CreateVersion7()),
            ("u", userId), ("past", past), ("future", future), ("now", now.UtcDateTime),
            ("g1", Guid.CreateVersion7()), ("g2", Guid.CreateVersion7()), ("s", sessionId),
            ("b1", Guid.CreateVersion7()), ("b2", Guid.CreateVersion7()), ("b3", Guid.CreateVersion7()),
            ("old", now.UtcDateTime.AddHours(-2)), ("now2", now.UtcDateTime),
            ("kh1", "kh1"), ("kh2", "kh2"), ("kh3", "kh3"));

        var result = await RunMaintenanceAsync();
        result.Codes.ShouldBe(1);
        result.StepUpGrants.ShouldBe(1);
        result.ThrottleBuckets.ShouldBe(1);
        result.Sessions.ShouldBe(1);

        var liveSessionTokens = await ScalarAsync<long>(
            "SELECT COUNT(*) FROM identity.refresh_tokens WHERE session_id = @s",
            ("s", sessionId));
        liveSessionTokens.ShouldBe(2);

        var expiredSessionTokens = await ScalarAsync<long>(
            "SELECT COUNT(*) FROM identity.refresh_tokens rt JOIN identity.sessions s ON s.id = rt.session_id WHERE s.id = @id",
            ("id", secondLogin.SessionId));
        expiredSessionTokens.ShouldBe(0);

        var remainingCodes = await ScalarAsync<long>(
            "SELECT COUNT(*) FROM identity.one_time_codes WHERE user_id = @u AND expires_at > @now",
            ("u", userId), ("now", now.UtcDateTime));
        remainingCodes.ShouldBeGreaterThan(0);

        var remainingBuckets = await ScalarAsync<long>(
            "SELECT COUNT(*) FROM identity.throttle_buckets WHERE key_hash IN ('kh2', 'kh3')");
        remainingBuckets.ShouldBe(2);

        var second = await RunMaintenanceAsync();
        second.Total.ShouldBe(0);
    }
}
