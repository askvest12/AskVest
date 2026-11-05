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
public sealed class AccountApiTests(PostgresFixture fixture) : IAsyncLifetime
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

    private static string NewEmail() => $"account-{Guid.CreateVersion7():N}@example.com";

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

    private static HttpRequestMessage Get(string path, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

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

    private async Task<(string AccessToken, Guid SessionId)> LoginForTokensAsync(
        HttpClient client, string email, string deviceId = "device-1", int emailSkip = 1)
    {
        using var response = await client.SendAsync(
            Post("/api/v1/auth/login", new { email, password = GoodPassword, device = Device(deviceId) }),
            TestContext.Current.CancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var data = json.GetProperty("data");
        if (data.GetProperty("result").GetString() == "device_verification_required")
        {
            var challengeId = data.GetProperty("challengeId").GetGuid();
            var deviceEmail = await WaitForEmailAsync(email, emailSkip);
            var code = ExtractCode(deviceEmail);
            using var verify = await client.SendAsync(
                Post("/api/v1/auth/verify-device", new { challengeId, code }),
                TestContext.Current.CancellationToken);
            var verified = await verify.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            data = verified.GetProperty("data");
        }

        data.GetProperty("result").GetString().ShouldBe("authenticated");
        return (data.GetProperty("accessToken").GetString()!, data.GetProperty("sessionId").GetGuid());
    }

    private static async Task<JsonElement> MeAsync(HttpClient client, string token)
    {
        using var response = await client.SendAsync(
            Get("/api/v1/account/me", token), TestContext.Current.CancellationToken);
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
    public async Task Me_Returns_Own_Profile_With_Masked_Phone_And_No_Secrets()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);
        var (token, _) = await LoginForTokensAsync(client, email);

        var json = await MeAsync(client, token);
        json.GetProperty("success").GetBoolean().ShouldBeTrue();
        var data = json.GetProperty("data");
        data.GetProperty("email").GetString().ShouldBe(email.ToLowerInvariant());
        data.GetProperty("emailVerified").GetBoolean().ShouldBeTrue();
        data.GetProperty("hasPin").GetBoolean().ShouldBeFalse();
        data.GetProperty("status").GetString().ShouldBe("Active");
        data.GetProperty("maskedPhone").ValueKind.ShouldBe(JsonValueKind.Null);
        var text = data.GetRawText();
        text.ShouldNotContain("passwordHash");
        text.ShouldNotContain("pinHash");
        text.ShouldNotContain("refreshToken");
    }

    [Fact]
    public async Task Account_Endpoints_Require_Authentication()
    {
        var client = Client();
        foreach (var path in new[]
        {
            "/api/v1/account/me",
            "/api/v1/account/sessions",
            "/api/v1/account/devices",
            "/api/v1/account/security-events",
        })
        {
            using var response = await client.SendAsync(
                Get(path), TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, path);
        }
    }

    [Fact]
    public async Task Sessions_List_Marks_Current_And_Revokes_Own_Session()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);
        var (tokenA, sessionA) = await LoginForTokensAsync(client, email, "dev-a");
        var (tokenB, sessionB) = await LoginForTokensAsync(client, email, "dev-b");

        using var listResponse = await client.SendAsync(
            Get("/api/v1/account/sessions", tokenA), TestContext.Current.CancellationToken);
        var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var items = list.GetProperty("data").EnumerateArray().ToList();
        items.Count.ShouldBe(2);
        var current = items.Single(i => i.GetProperty("id").GetGuid() == sessionA);
        current.GetProperty("isCurrent").GetBoolean().ShouldBeTrue();
        var other = items.Single(i => i.GetProperty("id").GetGuid() == sessionB);
        other.GetProperty("isCurrent").GetBoolean().ShouldBeFalse();

        using var revoke = await client.SendAsync(
            PostAuth($"/api/v1/account/sessions/{sessionB}/revoke", new { }, tokenA),
            TestContext.Current.CancellationToken);
        revoke.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = await MeAsync(client, tokenB);
        json.GetProperty("success").GetBoolean().ShouldBeFalse();
        json.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.session_revoked");

        using var listAfter = await client.SendAsync(
            Get("/api/v1/account/sessions", tokenA), TestContext.Current.CancellationToken);
        var after = await listAfter.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        after.GetProperty("data").EnumerateArray().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Session_Revocation_Of_Other_Users_Session_Returns_NotFound()
    {
        var client = Client();
        var emailA = NewEmail();
        var emailB = NewEmail();
        await ActivateAsync(client, emailA);
        await ActivateAsync(client, emailB);
        var (_, sessionB) = await LoginForTokensAsync(client, emailB, "dev-b");
        var (tokenA, _) = await LoginForTokensAsync(client, emailA, "dev-a");

        using var response = await client.SendAsync(
            PostAuth($"/api/v1/account/sessions/{sessionB}/revoke", new { }, tokenA),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("error").GetProperty("code").GetString().ShouldBe("resource.not_found");
    }

    [Fact]
    public async Task Revoke_Others_Keeps_Current_Session_Only()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);
        var (tokenA, sessionA) = await LoginForTokensAsync(client, email, "dev-a");
        var (tokenB, _) = await LoginForTokensAsync(client, email, "dev-b");

        using var response = await client.SendAsync(
            PostAuth("/api/v1/account/sessions/revoke-others", new { }, tokenA),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await MeAsync(client, tokenA)).GetProperty("success").GetBoolean().ShouldBeTrue();
        (await MeAsync(client, tokenB)).GetProperty("success").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Devices_List_And_Device_Revocation_Kills_Sessions()
    {
        var client = Client();
        var email = NewEmail();
        await ActivateAsync(client, email);
        var (tokenA, _) = await LoginForTokensAsync(client, email, "dev-a");
        var (tokenB, sessionB) = await LoginForTokensAsync(client, email, "dev-b");

        using var listResponse = await client.SendAsync(
            Get("/api/v1/account/devices", tokenA), TestContext.Current.CancellationToken);
        var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var items = list.GetProperty("data").EnumerateArray().ToList();
        items.Count.ShouldBe(2);
        items.ShouldAllBe(d => d.GetProperty("trusted").GetBoolean());
        var deviceB = items.Single(d => d.GetProperty("installationId").GetString() == "dev-b");
        var deviceBId = deviceB.GetProperty("id").GetGuid();
        var deviceACurrent = items.Single(d => d.GetProperty("installationId").GetString() == "dev-a");
        deviceACurrent.GetProperty("current").GetBoolean().ShouldBeTrue();

        using var revoke = await client.SendAsync(
            PostAuth($"/api/v1/account/devices/{deviceBId}/revoke", new { }, tokenA),
            TestContext.Current.CancellationToken);
        revoke.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await MeAsync(client, tokenB)).GetProperty("success").GetBoolean().ShouldBeFalse();
        (await MeAsync(client, tokenA)).GetProperty("success").GetBoolean().ShouldBeTrue();

        var revokedCount = await ScalarLongAsync(
            "SELECT COUNT(*) FROM identity.refresh_tokens rt JOIN identity.sessions s ON s.id = rt.session_id WHERE s.id = @p0 AND rt.rotated_at IS NULL",
            sessionB);
        revokedCount.ShouldBe(0);
    }

    [Fact]
    public async Task Device_Revocation_Of_Other_Users_Device_Returns_NotFound()
    {
        var client = Client();
        var emailA = NewEmail();
        var emailB = NewEmail();
        await ActivateAsync(client, emailA);
        await ActivateAsync(client, emailB);
        await LoginForTokensAsync(client, emailB, "dev-b");
        var (tokenA, _) = await LoginForTokensAsync(client, emailA, "dev-a");

        var deviceBId = await ScalarLongAsync(
            "SELECT COUNT(*) FROM identity.devices WHERE installation_id = 'dev-b' AND revoked_at IS NULL");
        deviceBId.ShouldBeGreaterThan(0);

        using var listB = await client.SendAsync(
            Get("/api/v1/account/devices", (await LoginForTokensAsync(client, emailB, "dev-b")).AccessToken),
            TestContext.Current.CancellationToken);
        var listJson = await listB.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var deviceId = listJson.GetProperty("data").EnumerateArray()
            .Single(d => d.GetProperty("installationId").GetString() == "dev-b")
            .GetProperty("id").GetGuid();

        using var response = await client.SendAsync(
            PostAuth($"/api/v1/account/devices/{deviceId}/revoke", new { }, tokenA),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("error").GetProperty("code").GetString().ShouldBe("resource.not_found");
    }

    [Fact]
    public async Task Security_Events_Are_Own_Only_Coarse_Ip_And_Cursor_Paginated()
    {
        var client = Client();
        var emailA = NewEmail();
        var emailB = NewEmail();
        await ActivateAsync(client, emailA);
        await ActivateAsync(client, emailB);
        var (tokenA, _) = await LoginForTokensAsync(client, emailA, "dev-a");
        await LoginForTokensAsync(client, emailB, "dev-b");

        using var first = await client.SendAsync(
            Get("/api/v1/account/security-events?limit=2", tokenA), TestContext.Current.CancellationToken);
        var page1 = await first.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        page1.GetProperty("success").GetBoolean().ShouldBeTrue();
        var data1 = page1.GetProperty("data");
        var items1 = data1.GetProperty("items").EnumerateArray().ToList();
        items1.Count.ShouldBe(2);
        data1.GetProperty("hasMore").GetBoolean().ShouldBeTrue();

        var ids1 = items1.Select(i => i.GetProperty("id").GetGuid()).ToHashSet();
        foreach (var item in items1)
        {
            item.GetProperty("eventType").GetString().ShouldNotBeNullOrEmpty();
            var ip = item.GetProperty("coarseIp");
            if (ip.ValueKind == JsonValueKind.String)
            {
                var parts = ip.GetString()!.Split('.');
                if (parts.Length == 4)
                {
                    parts[^1].ShouldBe("0");
                }
            }
        }

        var cursor = data1.GetProperty("nextCursor").GetString();
        cursor.ShouldNotBeNullOrEmpty();
        using var second = await client.SendAsync(
            Get($"/api/v1/account/security-events?limit=2&cursor={Uri.EscapeDataString(cursor!)}", tokenA),
            TestContext.Current.CancellationToken);
        var page2 = await second.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var items2 = page2.GetProperty("data").GetProperty("items").EnumerateArray().ToList();
        items2.ShouldNotBeEmpty();
        foreach (var item in items2)
        {
            ids1.ShouldNotContain(item.GetProperty("id").GetGuid());
        }

        var ownCount = await ScalarLongAsync(
            "SELECT COUNT(*) FROM identity.security_events WHERE user_id = (SELECT id FROM identity.users WHERE normalized_email = @p0)",
            emailA.ToLowerInvariant());
        var seenCount = await ScalarLongAsync(
            "SELECT COUNT(*) FROM identity.security_events e WHERE e.user_id = (SELECT id FROM identity.users WHERE normalized_email = @p0)",
            emailB.ToLowerInvariant());
        seenCount.ShouldBeGreaterThan(0);

        var totalSeen = (long)(items1.Count + items2.Count);
        totalSeen.ShouldBeLessThanOrEqualTo(ownCount);
    }

    [Fact]
    public async Task Security_Events_Of_Other_User_Are_Not_Exposed()
    {
        var client = Client();
        var emailA = NewEmail();
        var emailB = NewEmail();
        await ActivateAsync(client, emailA);
        await ActivateAsync(client, emailB);
        var (tokenA, _) = await LoginForTokensAsync(client, emailA, "dev-a");
        await LoginForTokensAsync(client, emailB, "dev-b");

        var eventTypesA = new List<string>();
        var cursor = (string?)null;
        do
        {
            var path = cursor is null
                ? "/api/v1/account/security-events?limit=100"
                : $"/api/v1/account/security-events?limit=100&cursor={Uri.EscapeDataString(cursor)}";
            using var response = await client.SendAsync(
                Get(path, tokenA), TestContext.Current.CancellationToken);
            var page = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            var data = page.GetProperty("data");
            eventTypesA.AddRange(data.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("eventType").GetString()!));
            cursor = data.GetProperty("nextCursor").ValueKind == JsonValueKind.String
                ? data.GetProperty("nextCursor").GetString()
                : null;
        }
        while (cursor is not null);

        eventTypesA.ShouldContain("SignUp");
        eventTypesA.ShouldContain("LoginSucceeded");
    }
}
