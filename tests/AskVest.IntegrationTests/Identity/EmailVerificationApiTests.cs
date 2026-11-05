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
public sealed class EmailVerificationApiTests(PostgresFixture fixture) : IAsyncLifetime
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

    private static string NewEmail() => $"verify-{Guid.CreateVersion7():N}@example.com";

    private const string GoodPassword = "CorrectHorse9";

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

    private async Task<string> SignUpAndGetCodeAsync(HttpClient client, string email)
    {
        using var response = await client.SendAsync(
            Post("/api/v1/auth/sign-up", new { email, password = GoodPassword }),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return ExtractCode(await WaitForEmailAsync(email));
    }

    private static async Task<JsonElement> VerifyAsync(HttpClient client, string email, string code)
    {
        using var response = await client.SendAsync(
            Post("/api/v1/auth/verify-email", new { email, code }),
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

    private async Task ExecSqlAsync(string sql, params object[] args)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        for (var i = 0; i < args.Length; i++)
        {
            command.Parameters.AddWithValue($"p{i}", args[i]);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> UserIdAsync(string email)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT id::text FROM identity.users WHERE normalized_email = @p0", connection);
        command.Parameters.AddWithValue("p0", email.ToLowerInvariant());
        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task VerifyEmail_Valid_Code_Activates_User_Without_Tokens()
    {
        var client = Client();
        var email = NewEmail();
        var code = await SignUpAndGetCodeAsync(client, email);

        var json = await VerifyAsync(client, email, code);
        json.GetProperty("success").GetBoolean().ShouldBeTrue();
        json.TryGetProperty("data", out var data).ShouldBeTrue();
        data.ValueKind.ShouldBe(JsonValueKind.Null);
        var raw = json.ToString();
        raw.ShouldNotContain("accessToken");
        raw.ShouldNotContain("refreshToken");

        var status = await ScalarLongAsync(
            "SELECT count(*) FROM identity.users WHERE normalized_email = @p0 AND status = 'Active'",
            email.ToLowerInvariant());
        status.ShouldBe(1);

        var verifiedAt = await ScalarLongAsync(
            "SELECT count(*) FROM identity.users WHERE normalized_email = @p0 AND email_verified_at IS NOT NULL",
            email.ToLowerInvariant());
        verifiedAt.ShouldBe(1);

        var consumed = await ScalarLongAsync(
            "SELECT count(*) FROM identity.one_time_codes WHERE consumed_at IS NOT NULL");
        consumed.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task VerifyEmail_Wrong_Code_Returns_Invalid_Code()
    {
        var client = Client();
        var email = NewEmail();
        var code = await SignUpAndGetCodeAsync(client, email);
        var wrong = code == "000000" ? "000001" : "000000";

        var json = await VerifyAsync(client, email, wrong);
        json.GetProperty("success").GetBoolean().ShouldBeFalse();
        json.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.invalid_code");

        var stillPending = await ScalarLongAsync(
            "SELECT count(*) FROM identity.users WHERE normalized_email = @p0 AND status = 'PendingVerification'",
            email.ToLowerInvariant());
        stillPending.ShouldBe(1);
    }

    [Fact]
    public async Task VerifyEmail_Consumed_Code_Returns_Invalid_Code()
    {
        var client = Client();
        var email = NewEmail();
        var code = await SignUpAndGetCodeAsync(client, email);

        (await VerifyAsync(client, email, code)).GetProperty("success").GetBoolean().ShouldBeTrue();
        var replay = await VerifyAsync(client, email, code);
        replay.GetProperty("success").GetBoolean().ShouldBeFalse();
        replay.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.invalid_code");
    }

    [Fact]
    public async Task VerifyEmail_Expired_Code_Returns_Invalid_Code()
    {
        var client = Client();
        var email = NewEmail();
        var code = await SignUpAndGetCodeAsync(client, email);
        var userId = await UserIdAsync(email);

        await ExecSqlAsync(
            "UPDATE identity.one_time_codes SET expires_at = now() - interval '1 minute' WHERE user_id = @p0::uuid AND purpose = 'EmailVerification'",
            userId);

        var json = await VerifyAsync(client, email, code);
        json.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.invalid_code");
    }

    [Fact]
    public async Task VerifyEmail_Exhausted_Attempts_Return_Invalid_Code()
    {
        var client = Client();
        var email = NewEmail();
        var code = await SignUpAndGetCodeAsync(client, email);
        var wrong = code == "000000" ? "000001" : "000000";

        for (var i = 0; i < 5; i++)
        {
            var json = await VerifyAsync(client, email, wrong);
            json.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.invalid_code");
        }

        var final = await VerifyAsync(client, email, code);
        final.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.invalid_code");
    }

    [Fact]
    public async Task VerifyEmail_Unknown_Email_Returns_Invalid_Code()
    {
        var client = Client();
        var json = await VerifyAsync(client, NewEmail(), "123456");
        json.GetProperty("success").GetBoolean().ShouldBeFalse();
        json.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.invalid_code");
    }

    [Fact]
    public async Task Concurrent_Verifications_Produce_Exactly_One_Success()
    {
        var client = Client();
        var email = NewEmail();
        var code = await SignUpAndGetCodeAsync(client, email);

        var tasks = Enumerable.Range(0, 20).Select(_ => client.SendAsync(
            Post("/api/v1/auth/verify-email", new { email, code }),
            TestContext.Current.CancellationToken)).ToList();
        var responses = await Task.WhenAll(tasks);

        var successes = 0;
        foreach (var response in responses)
        {
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            if (json.GetProperty("success").GetBoolean())
            {
                successes++;
            }
            else
            {
                json.GetProperty("error").GetProperty("code").GetString().ShouldBe("auth.invalid_code");
            }
        }

        successes.ShouldBe(1);
        var status = await ScalarLongAsync(
            "SELECT count(*) FROM identity.users WHERE normalized_email = @p0 AND status = 'Active'",
            email.ToLowerInvariant());
        status.ShouldBe(1);
    }

    [Fact]
    public async Task Resend_Within_Cooldown_Sends_Nothing_But_Stays_Uniform()
    {
        var client = Client();
        var email = NewEmail();
        await SignUpAndGetCodeAsync(client, email);
        var before = _factory.Emails.Sent.Count(m => m.To == email);

        using var response = await client.SendAsync(
            Post("/api/v1/auth/resend-verification", new { email }),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("success").GetBoolean().ShouldBeTrue();
        json.GetProperty("message").GetString().ShouldBe("If this email can be verified, a verification code has been sent.");

        await Task.Delay(500, TestContext.Current.CancellationToken);
        _factory.Emails.Sent.Count(m => m.To == email).ShouldBe(before);
    }

    [Fact]
    public async Task Resend_After_Cooldown_Sends_New_Code()
    {
        var client = Client();
        var email = NewEmail();
        var firstCode = await SignUpAndGetCodeAsync(client, email);
        var userId = await UserIdAsync(email);

        await ExecSqlAsync(
            "UPDATE identity.one_time_codes SET created_at = now() - interval '2 minutes' WHERE user_id = @p0::uuid",
            userId);

        using var response = await client.SendAsync(
            Post("/api/v1/auth/resend-verification", new { email }),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var second = await WaitForEmailAsync(email, skip: 1);
        var secondCode = ExtractCode(second);
        secondCode.ShouldNotBe(firstCode);

        var json = await VerifyAsync(client, email, secondCode);
        json.GetProperty("success").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Resend_Hourly_Cap_Suppresses_Send_Without_Revealing_State()
    {
        var client = Client();
        var email = NewEmail();
        await SignUpAndGetCodeAsync(client, email);
        var userId = await UserIdAsync(email);

        await ExecSqlAsync(
            "UPDATE identity.one_time_codes SET created_at = now() - interval '2 minutes' WHERE user_id = @p0::uuid",
            userId);
        await ExecSqlAsync(
            @"INSERT INTO identity.one_time_codes
                (id, user_id, purpose, destination_type, destination, code_hash, attempts_used, attempts_allowed, expires_at, created_at)
              SELECT gen_random_uuid(), @p0::uuid, 'EmailVerification', 'Email', @p1,
                     md5(g::text) || md5(g::text || 'x'), 0, 5,
                     now() + interval '10 minutes', now() - interval '90 seconds'
              FROM generate_series(1, 4) AS g",
            userId, email.ToLowerInvariant());

        var before = _factory.Emails.Sent.Count(m => m.To == email);
        using var response = await client.SendAsync(
            Post("/api/v1/auth/resend-verification", new { email }),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("success").GetBoolean().ShouldBeTrue();

        await Task.Delay(500, TestContext.Current.CancellationToken);
        _factory.Emails.Sent.Count(m => m.To == email).ShouldBe(before);
    }

    [Fact]
    public async Task Resend_Unknown_Email_Is_Uniform()
    {
        var client = Client();
        using var response = await client.SendAsync(
            Post("/api/v1/auth/resend-verification", new { email = NewEmail() }),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("success").GetBoolean().ShouldBeTrue();
        json.GetProperty("message").GetString().ShouldBe("If this email can be verified, a verification code has been sent.");
    }
}
