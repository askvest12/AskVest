using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Identity.Contracts;
using AskVest.Modules.Identity.Messaging;
using AskVest.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Identity;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class PinStepUpApiTests(PostgresFixture fixture) : IAsyncLifetime
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

    private static string NewEmail() => $"pin-{Guid.CreateVersion7():N}@example.com";

    private const string GoodPassword = "CorrectHorse9";
    private const string Pin = "482913";
    private const string Pin2 = "730156";

    private static object Device(string id) => new
    {
        installationId = id,
        platform = "android",
        deviceName = "Test Phone",
        appVersion = "1.0.0",
    };

    private static HttpRequestMessage Post(string path, object? body = null, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Add("Idempotency-Key", key ?? $"k-{Guid.CreateVersion7():N}");
        return request;
    }

    private static HttpRequestMessage PostAuth(string path, object? body, string token)
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

    private async Task<string> ActivateAndLoginAsync(HttpClient client, string email)
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

        using var login = await client.SendAsync(
            Post("/api/v1/auth/login", new { email, password = GoodPassword, device = Device("pin-device") }),
            TestContext.Current.CancellationToken);
        var json = await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return json.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    private async Task<Guid> UserIdAsync(string email)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT id FROM identity.users WHERE normalized_email = @e", connection);
        command.Parameters.AddWithValue("e", email);
        return (Guid)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task<(string? PinHash, DateTimeOffset? LockedUntil, int Failed, int Repeats)> PinStateAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        async Task<object?> ScalarAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("id", userId);
            return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        var hash = await ScalarAsync("SELECT pin_hash FROM identity.users WHERE id = @id");
        var locked = await ScalarAsync("SELECT pin_locked_until FROM identity.users WHERE id = @id");
        var failed = await ScalarAsync("SELECT pin_failed_count FROM identity.users WHERE id = @id");
        var repeats = await ScalarAsync("SELECT pin_lock_repeat_count FROM identity.users WHERE id = @id");
        return (
            hash as string,
            locked is DateTime lockedAt ? new DateTimeOffset(DateTime.SpecifyKind(lockedAt, DateTimeKind.Utc)) : null,
            failed is null ? 0 : Convert.ToInt32(failed),
            repeats is null ? 0 : Convert.ToInt32(repeats));
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

    private async Task SetPinLockedUntilAsync(Guid userId, DateTimeOffset until)
        => await ExecuteSqlAsync(
            "UPDATE identity.users SET pin_locked_until = @u WHERE id = @id",
            ("u", until), ("id", userId));

    private static async Task<string> IssueStepUpAsync(HttpClient client, string token, string purpose, string? binding = null, string pin = Pin)
    {
        using var response = await client.SendAsync(
            PostAuth("/api/v1/auth/step-up", new { purpose, pin, binding }, token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return json.GetProperty("data").GetProperty("grantToken").GetString()!;
    }

    private async Task<Result> ConsumeAsync(Guid userId, Guid sessionId, string purpose, string? binding, string token)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var authorizer = scope.ServiceProvider.GetRequiredService<IStepUpAuthorizer>();
        return await authorizer.ConsumeAsync(userId, sessionId, purpose, binding, token, TestContext.Current.CancellationToken);
    }

    private async Task<(Guid UserId, Guid SessionId)> IdentityAsync(string email)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT u.id, s.id
            FROM identity.users u
            JOIN identity.sessions s ON s.user_id = u.id AND s.revoked_at IS NULL
            WHERE u.normalized_email = @e
            ORDER BY s.created_at DESC
            LIMIT 1
            """,
            connection);
        command.Parameters.AddWithValue("e", email);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        return (reader.GetGuid(0), reader.GetGuid(1));
    }

    [Fact]
    public async Task Set_Pin_Requires_Password_Rejects_Weak_And_Stores_Hash()
    {
        var client = Client();
        var email = NewEmail();
        var token = await ActivateAndLoginAsync(client, email);
        var userId = await UserIdAsync(email);

        using var wrongPassword = await client.SendAsync(
            PostAuth("/api/v1/account/pin", new { password = "WrongPassword1", pin = Pin }, token),
            TestContext.Current.CancellationToken);
        wrongPassword.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        foreach (var weak in new[] { "111111", "123456", "654321", "121212", "112233", "123123", "012345" })
        {
            using var weakResponse = await client.SendAsync(
                PostAuth("/api/v1/account/pin", new { password = GoodPassword, pin = weak }, token),
                TestContext.Current.CancellationToken);
            weakResponse.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        using var nonNumeric = await client.SendAsync(
            PostAuth("/api/v1/account/pin", new { password = GoodPassword, pin = "48291a" }, token),
            TestContext.Current.CancellationToken);
        nonNumeric.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var set = await client.SendAsync(
            PostAuth("/api/v1/account/pin", new { password = GoodPassword, pin = Pin }, token),
            TestContext.Current.CancellationToken);
        set.StatusCode.ShouldBe(HttpStatusCode.OK);

        var state = await PinStateAsync(userId);
        state.PinHash.ShouldNotBeNull();
        state.PinHash.ShouldNotBe(Pin);
        state.PinHash.ShouldStartWith("argon2id$");
        state.Failed.ShouldBe(0);
    }

    [Fact]
    public async Task Pin_Failures_Lock_With_Doubling_And_Success_Resets()
    {
        var client = Client();
        var email = NewEmail();
        var token = await ActivateAndLoginAsync(client, email);
        var userId = await UserIdAsync(email);
        await client.SendAsync(
            PostAuth("/api/v1/account/pin", new { password = GoodPassword, pin = Pin }, token),
            TestContext.Current.CancellationToken);

        for (var i = 0; i < 4; i++)
        {
            using var miss = await client.SendAsync(
                PostAuth("/api/v1/account/pin/change", new { currentPin = "000000", pin = Pin2 }, token),
                TestContext.Current.CancellationToken);
            miss.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        using var locking = await client.SendAsync(
            PostAuth("/api/v1/account/pin/change", new { currentPin = "000000", pin = Pin2 }, token),
            TestContext.Current.CancellationToken);
        locking.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        locking.Headers.GetValues("Retry-After").ShouldNotBeEmpty();

        var firstLock = await PinStateAsync(userId);
        firstLock.LockedUntil.ShouldNotBeNull();
        var firstDuration = firstLock.LockedUntil!.Value - TimeProvider.System.GetUtcNow();
        firstDuration.ShouldBeGreaterThan(TimeSpan.FromMinutes(20));
        firstDuration.ShouldBeLessThan(TimeSpan.FromMinutes(35));

        using var correctWhileLocked = await client.SendAsync(
            PostAuth("/api/v1/account/pin/change", new { currentPin = Pin, pin = Pin2 }, token),
            TestContext.Current.CancellationToken);
        correctWhileLocked.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        await SetPinLockedUntilAsync(userId, TimeProvider.System.GetUtcNow().AddSeconds(-1));
        for (var i = 0; i < 4; i++)
        {
            using var miss = await client.SendAsync(
                PostAuth("/api/v1/account/pin/change", new { currentPin = "000000", pin = Pin2 }, token),
                TestContext.Current.CancellationToken);
            miss.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        using var relock = await client.SendAsync(
            PostAuth("/api/v1/account/pin/change", new { currentPin = "000000", pin = Pin2 }, token),
            TestContext.Current.CancellationToken);
        relock.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        var secondLock = await PinStateAsync(userId);
        var secondDuration = secondLock.LockedUntil!.Value - TimeProvider.System.GetUtcNow();
        secondDuration.ShouldBeGreaterThan(TimeSpan.FromMinutes(40));

        await SetPinLockedUntilAsync(userId, TimeProvider.System.GetUtcNow().AddSeconds(-1));
        using var hit = await client.SendAsync(
            PostAuth("/api/v1/account/pin/change", new { currentPin = "111111", pin = Pin2 }, token),
            TestContext.Current.CancellationToken);
        hit.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        using var success = await client.SendAsync(
            PostAuth("/api/v1/account/pin/change", new { currentPin = Pin, pin = Pin2 }, token),
            TestContext.Current.CancellationToken);
        success.StatusCode.ShouldBe(HttpStatusCode.OK);

        var after = await PinStateAsync(userId);
        after.Failed.ShouldBe(0);
        after.Repeats.ShouldBe(0);

        for (var i = 0; i < 3; i++)
        {
            using var miss = await client.SendAsync(
                PostAuth("/api/v1/account/pin/change", new { currentPin = "000000", pin = Pin }, token),
                TestContext.Current.CancellationToken);
            miss.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        using var stillOpen = await client.SendAsync(
            PostAuth("/api/v1/account/pin/change", new { currentPin = "000000", pin = Pin }, token),
            TestContext.Current.CancellationToken);
        stillOpen.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Reset_Pin_Revokes_Outstanding_Grants()
    {
        var client = Client();
        var email = NewEmail();
        var token = await ActivateAndLoginAsync(client, email);
        var (userId, sessionId) = await IdentityAsync(email);
        await client.SendAsync(
            PostAuth("/api/v1/account/pin", new { password = GoodPassword, pin = Pin }, token),
            TestContext.Current.CancellationToken);

        var grantToken = await IssueStepUpAsync(client, token, "authorize_order");

        using var requestReset = await client.SendAsync(
            PostAuth("/api/v1/account/pin/reset-request", null, token),
            TestContext.Current.CancellationToken);
        requestReset.StatusCode.ShouldBe(HttpStatusCode.OK);

        var resetEmail = await WaitForEmailAsync(email, skip: 1);
        var code = ExtractCode(resetEmail);

        using var badPassword = await client.SendAsync(
            PostAuth("/api/v1/account/pin/reset", new { code, password = "WrongPassword1", pin = Pin2 }, token),
            TestContext.Current.CancellationToken);
        badPassword.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var reset = await client.SendAsync(
            PostAuth("/api/v1/account/pin/reset", new { code, password = GoodPassword, pin = Pin2 }, token),
            TestContext.Current.CancellationToken);
        reset.StatusCode.ShouldBe(HttpStatusCode.OK);

        var consume = await ConsumeAsync(userId, sessionId, "authorize_order", null, grantToken);
        consume.IsFailure.ShouldBeTrue();
        consume.Error!.Code.ShouldBe("auth.step_up_invalid");

        var state = await PinStateAsync(userId);
        state.PinHash.ShouldNotBeNull();
        state.PinHash.ShouldNotBe(Pin);
    }

    [Fact]
    public async Task StepUp_Grant_Binds_User_Session_Purpose_And_Binding()
    {
        var client = Client();
        var email = NewEmail();
        var token = await ActivateAndLoginAsync(client, email);
        var (userId, sessionId) = await IdentityAsync(email);
        await client.SendAsync(
            PostAuth("/api/v1/account/pin", new { password = GoodPassword, pin = Pin }, token),
            TestContext.Current.CancellationToken);

        using var unknownPurpose = await client.SendAsync(
            PostAuth("/api/v1/auth/step-up", new { purpose = "transfer_all_money", pin = Pin }, token),
            TestContext.Current.CancellationToken);
        unknownPurpose.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var grantToken = await IssueStepUpAsync(client, token, "authorize_order", binding: "quote-123");

        (await ConsumeAsync(userId, sessionId, "authorize_withdrawal", "quote-123", grantToken))
            .Error!.Code.ShouldBe("auth.step_up_invalid");
        (await ConsumeAsync(userId, sessionId, "authorize_order", "quote-999", grantToken))
            .Error!.Code.ShouldBe("auth.step_up_invalid");
        (await ConsumeAsync(Guid.CreateVersion7(), sessionId, "authorize_order", "quote-123", grantToken))
            .Error!.Code.ShouldBe("auth.step_up_invalid");
        (await ConsumeAsync(userId, Guid.CreateVersion7(), "authorize_order", "quote-123", grantToken))
            .Error!.Code.ShouldBe("auth.step_up_invalid");

        var consumed = await ConsumeAsync(userId, sessionId, "authorize_order", "quote-123", grantToken);
        consumed.IsSuccess.ShouldBeTrue();

        (await ConsumeAsync(userId, sessionId, "authorize_order", "quote-123", grantToken))
            .Error!.Code.ShouldBe("auth.step_up_invalid");
    }

    [Fact]
    public async Task StepUp_Expired_Grant_And_Concurrent_Consumption()
    {
        var client = Client();
        var email = NewEmail();
        var token = await ActivateAndLoginAsync(client, email);
        var (userId, sessionId) = await IdentityAsync(email);
        await client.SendAsync(
            PostAuth("/api/v1/account/pin", new { password = GoodPassword, pin = Pin }, token),
            TestContext.Current.CancellationToken);

        var expiredToken = await IssueStepUpAsync(client, token, "enable_earn");
        await ExecuteSqlAsync(
            "UPDATE identity.step_up_grants SET expires_at = @e WHERE user_id = @u AND consumed_at IS NULL",
            ("e", TimeProvider.System.GetUtcNow().AddSeconds(-5)), ("u", userId));
        (await ConsumeAsync(userId, sessionId, "enable_earn", null, expiredToken))
            .Error!.Code.ShouldBe("auth.step_up_invalid");

        var liveToken = await IssueStepUpAsync(client, token, "enable_earn");
        var results = await Task.WhenAll(
            ConsumeAsync(userId, sessionId, "enable_earn", null, liveToken),
            ConsumeAsync(userId, sessionId, "enable_earn", null, liveToken));
        results.Count(r => r.IsSuccess).ShouldBe(1);
        results.Count(r => r.IsFailure).ShouldBe(1);
    }
}
