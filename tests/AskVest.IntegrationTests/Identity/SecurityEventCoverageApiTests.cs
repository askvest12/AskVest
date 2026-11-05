using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Messaging;
using AskVest.Modules.Identity.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Identity;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class SecurityEventCoverageApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private AuthApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new AuthApiFactory(
            fixture.AppConnectionString,
            new Dictionary<string, string?>
            {
                ["Identity:PhoneVerificationEnabled"] = "true",
                ["Messaging:Sms:Mode"] = "Development",
                ["Messaging:Sms:ForwardTo"] = "sms-capture@example.com",
                ["Identity:DefaultPhoneRegion"] = "US",
            });
        _factory.CreateClient();
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient Client() => _factory.CreateClient();

    private const string GoodPassword = "CorrectHorse9";
    private const string NewPassword = "NewHorse10";
    private const string Pin = "135790";
    private const string WrongPin = "000000";
    private const string SmsInbox = "sms-capture@example.com";

    private static string NewEmail() => $"sec-{Guid.CreateVersion7():N}@example.com";

    private static object Device(string id) => new
    {
        installationId = id,
        platform = "android",
        deviceName = "Coverage Device",
        appVersion = "1.0.0",
    };

    private static HttpRequestMessage Post(string path, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Add("Idempotency-Key", $"k-{Guid.CreateVersion7():N}");
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
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private async Task<string> WaitForCodeAsync(string to, int skip = 0)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(30);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var matches = _factory.Emails.Sent.Where(m => m.To == to).ToList();
            if (matches.Count > skip)
            {
                var match = Regex.Match(matches[^1].TextBody, @"code is (\d{6})");
                match.Success.ShouldBeTrue($"No code in email: {matches[^1].TextBody}");
                return match.Groups[1].Value;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"No email captured for {to}.");
    }

    private static async Task<(string Access, string Refresh)> LoginTokensAsync(
        HttpClient client, string email, string password, string deviceId)
    {
        var json = await SendJsonAsync(client, Post("/api/v1/auth/login", new
        {
            email,
            password,
            device = Device(deviceId),
        }));
        var data = json.GetProperty("data");
        data.GetProperty("result").GetString().ShouldBe("authenticated");
        return (
            data.GetProperty("accessToken").GetString()!,
            data.GetProperty("refreshToken").GetString()!);
    }

    private async Task<string[]> EventTypesForAsync(string email)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT DISTINCT event_type FROM identity.security_events
            WHERE user_id = (SELECT id FROM identity.users WHERE normalized_email = @e)
            """, connection);
        command.Parameters.AddWithValue("e", email.ToLowerInvariant());
        var types = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            types.Add(reader.GetString(0));
        }

        return [.. types];
    }

    private async Task<string[]> EventTypesForUserAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT DISTINCT event_type FROM identity.security_events WHERE user_id = @id",
            connection);
        command.Parameters.AddWithValue("id", userId);
        var types = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            types.Add(reader.GetString(0));
        }

        return [.. types];
    }

    private async Task<long> EventTypeCountAsync(string eventType)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM identity.security_events WHERE event_type = @t",
            connection);
        command.Parameters.AddWithValue("t", eventType);
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Every_Security_Event_Type_Is_Written_And_Immutable()
    {
        var client = Client();
        var email = NewEmail();

        await SendJsonAsync(client, Post("/api/v1/auth/sign-up", new { email, password = GoodPassword }));
        var verifyCode = await WaitForCodeAsync(email);
        await SendJsonAsync(client, Post("/api/v1/auth/verify-email", new { email, code = verifyCode }));

        var (accessA, refreshA) = await LoginTokensAsync(client, email, GoodPassword, "cov-a");
        using var authedA = Client();
        authedA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessA);

        var loginB = await SendJsonAsync(client, Post("/api/v1/auth/login", new
        {
            email,
            password = GoodPassword,
            device = Device("cov-b"),
        }));
        loginB.GetProperty("data").GetProperty("result").GetString()
            .ShouldBe("device_verification_required");
        var deviceCode = await WaitForCodeAsync(email);
        var deviceB = await SendJsonAsync(client, Post("/api/v1/auth/verify-device", new
        {
            challengeId = loginB.GetProperty("data").GetProperty("challengeId").GetString(),
            code = deviceCode,
        }));
        var refreshB = deviceB.GetProperty("data").GetProperty("refreshToken").GetString()!;

        var refreshedB = await SendJsonAsync(client, Post("/api/v1/auth/refresh", new
        {
            refreshToken = refreshB,
            installationId = "cov-b",
        }));
        refreshedB.GetProperty("data").GetProperty("result").GetString().ShouldBe("authenticated");

        var theft = await SendJsonAsync(client, Post("/api/v1/auth/refresh", new
        {
            refreshToken = refreshB,
            installationId = "cov-b-impostor",
        }));
        theft.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.refresh_invalid");

        await SendJsonAsync(client, Post("/api/v1/auth/login", new
        {
            email,
            password = "WrongPass999",
            device = Device("cov-a"),
        }));

        var sessions = await SendJsonAsync(authedA, new HttpRequestMessage(
            HttpMethod.Get, new Uri("/api/v1/account/sessions", UriKind.Relative)));
        var remaining = sessions.GetProperty("data").EnumerateArray().ToList();
        remaining.Count.ShouldBe(1);
        var revoke = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"/api/v1/account/sessions/{remaining[0].GetProperty("id").GetString()}/revoke", UriKind.Relative));
        revoke.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessA);
        revoke.Headers.Add("Idempotency-Key", $"k-{Guid.CreateVersion7():N}");
        await SendJsonAsync(authedA, revoke);

        var (accessA2, _) = await LoginTokensAsync(client, email, GoodPassword, "cov-a");
        authedA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessA2);

        await SendJsonAsync(authedA, PostAuth("/api/v1/account/change-password", new
        {
            currentPassword = GoodPassword,
            password = NewPassword,
        }, accessA2));

        await SendJsonAsync(client, Post("/api/v1/auth/forgot-password", new { email }));
        var resetCode = await WaitForCodeAsync(email);
        await SendJsonAsync(client, Post("/api/v1/auth/reset-password", new
        {
            email,
            code = resetCode,
            password = GoodPassword,
        }));

        var (accessA3, _) = await LoginTokensAsync(client, email, GoodPassword, "cov-a");
        authedA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessA3);

        await SendJsonAsync(authedA, PostAuth("/api/v1/account/pin", new
        {
            password = GoodPassword,
            pin = Pin,
        }, accessA3));
        await SendJsonAsync(authedA, PostAuth("/api/v1/account/pin/change", new
        {
            currentPin = Pin,
            pin = "246810",
        }, accessA3));

        await SendJsonAsync(authedA, PostAuth("/api/v1/account/pin/reset-request", null, accessA3));
        var pinResetCode = await WaitForCodeAsync(email);
        await SendJsonAsync(authedA, PostAuth("/api/v1/account/pin/reset", new
        {
            code = pinResetCode,
            password = GoodPassword,
            pin = Pin,
        }, accessA3));

        var stepUp = await SendJsonAsync(authedA, PostAuth("/api/v1/auth/step-up", new
        {
            purpose = "delete_account",
            pin = Pin,
        }, accessA3));
        var grant = stepUp.GetProperty("data").GetProperty("grantToken").GetString()!;

        for (var i = 0; i < 8; i++)
        {
            await SendJsonAsync(authedA, PostAuth("/api/v1/auth/step-up", new
            {
                purpose = "delete_account",
                pin = WrongPin,
            }, accessA3));
        }

        await SendJsonAsync(authedA, PostAuth("/api/v1/account/deletion-request", new
        {
            password = GoodPassword,
            stepUpToken = grant,
        }, accessA3));
        await SendJsonAsync(authedA, PostAuth("/api/v1/account/deletion-cancel", null, accessA3));

        await SendJsonAsync(authedA, PostAuth("/api/v1/account/phone", new
        {
            phone = "+14155552671",
        }, accessA3));
        var phoneCode = await WaitForCodeAsync(SmsInbox);
        await SendJsonAsync(authedA, PostAuth("/api/v1/account/phone/verify", new
        {
            code = phoneCode,
        }, accessA3));

        var throttleEmail = NewEmail();
        await SendJsonAsync(client, Post("/api/v1/auth/sign-up", new { email = throttleEmail, password = GoodPassword }));
        var throttleCode = await WaitForCodeAsync(throttleEmail);
        await SendJsonAsync(client, Post("/api/v1/auth/verify-email", new { email = throttleEmail, code = throttleCode }));
        var throttledBefore = await EventTypeCountAsync("LoginThrottled");
        for (var i = 0; i < 6; i++)
        {
            await SendJsonAsync(client, Post("/api/v1/auth/login", new
            {
                email = throttleEmail,
                password = "WrongPass999",
                device = Device("cov-throttle"),
            }));
        }

        var expected = Enum.GetValues<SecurityEventType>()
            .Select(t => t.ToString())
            .Where(t => t is not (nameof(SecurityEventType.AccountAnonymized)
                or nameof(SecurityEventType.LoginThrottled)))
            .ToHashSet(StringComparer.Ordinal);
        var actual = (await EventTypesForAsync(email)).ToHashSet(StringComparer.Ordinal);
        var missing = expected.Except(actual).ToList();
        missing.ShouldBeEmpty($"Missing security event types for {email}: {string.Join(", ", missing)}");

        var throttleActual = (await EventTypesForAsync(throttleEmail)).ToHashSet(StringComparer.Ordinal);
        throttleActual.ShouldContain(nameof(SecurityEventType.LoginFailed));
        (await EventTypeCountAsync("LoginThrottled")).ShouldBeGreaterThan(throttledBefore);

        var anonEmail = NewEmail();
        await SendJsonAsync(client, Post("/api/v1/auth/sign-up", new { email = anonEmail, password = GoodPassword }));
        var anonCode = await WaitForCodeAsync(anonEmail);
        await SendJsonAsync(client, Post("/api/v1/auth/verify-email", new { email = anonEmail, code = anonCode }));
        var (anonAccess, _) = await LoginTokensAsync(client, anonEmail, GoodPassword, "cov-anon");
        using var anonClient = Client();
        anonClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", anonAccess);
        await SendJsonAsync(anonClient, PostAuth("/api/v1/account/pin", new
        {
            password = GoodPassword,
            pin = Pin,
        }, anonAccess));
        var anonStepUp = await SendJsonAsync(anonClient, PostAuth("/api/v1/auth/step-up", new
        {
            purpose = "delete_account",
            pin = Pin,
        }, anonAccess));
        var anonGrant = anonStepUp.GetProperty("data").GetProperty("grantToken").GetString()!;
        await SendJsonAsync(anonClient, PostAuth("/api/v1/account/deletion-request", new
        {
            password = GoodPassword,
            stepUpToken = anonGrant,
        }, anonAccess));

        Guid anonUserId;
        await using (var connection = new NpgsqlConnection(fixture.AppConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var idCommand = new NpgsqlCommand(
                "SELECT id FROM identity.users WHERE normalized_email = @e",
                connection);
            idCommand.Parameters.AddWithValue("e", anonEmail.ToLowerInvariant());
            anonUserId = (Guid)(await idCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

            await using var command = new NpgsqlCommand(
                "UPDATE identity.users SET deletion_scheduled_for = now() - interval '1 day' WHERE normalized_email = @e",
                connection);
            command.Parameters.AddWithValue("e", anonEmail.ToLowerInvariant());
            (await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var anonymizer = scope.ServiceProvider.GetRequiredService<AccountAnonymizationService>();
        (await anonymizer.RunOnceAsync(TestContext.Current.CancellationToken)).ShouldBeGreaterThan(0);

        var anonymizedTypes = (await EventTypesForUserAsync(anonUserId)).ToHashSet(StringComparer.Ordinal);
        anonymizedTypes.ShouldContain(nameof(SecurityEventType.AccountAnonymized));
    }
}
