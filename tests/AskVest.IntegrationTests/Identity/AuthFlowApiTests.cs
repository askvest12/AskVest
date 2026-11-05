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
public sealed class AuthFlowApiTests(PostgresFixture fixture) : IAsyncLifetime
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

    private static string NewEmail() => $"user-{Guid.CreateVersion7():N}@example.com";

    private const string GoodPassword = "CorrectHorse9";

    private static readonly string[] AdminRoles = ["admin"];

    private static HttpRequestMessage Post(string path, object body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", key ?? $"k-{Guid.CreateVersion7():N}");
        return request;
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string path, object body, string? key = null)
    {
        using var response = await client.SendAsync(Post(path, body, key), TestContext.Current.CancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return json;
    }

    private async Task<EmailMessage> WaitForEmailAsync(string to, int minCount = 1)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(30);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var matches = _factory.Emails.Sent.Where(m => m.To == to).ToList();
            if (matches.Count >= minCount)
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
        match.Success.ShouldBeTrue($"No code found in email body: {email.TextBody}");
        return match.Groups[1].Value;
    }

    private async Task<string> SignUpAndGetCodeAsync(HttpClient client, string email)
    {
        var response = await client.SendAsync(
            Post("/api/v1/auth/sign-up", new { email, password = GoodPassword }),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var emailMessage = await WaitForEmailAsync(email);
        return ExtractCode(emailMessage);
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

    [Fact]
    public async Task SignUp_Creates_Pending_User_And_Queues_Verification_Email()
    {
        var client = Client();
        var email = NewEmail();

        var json = await PostJsonAsync(client, "/api/v1/auth/sign-up", new { email, password = GoodPassword });
        json.GetProperty("success").GetBoolean().ShouldBeTrue();
        json.GetProperty("message").GetString().ShouldBe("If this email can be verified, a verification code has been sent.");

        var status = await ScalarLongAsync(
            "SELECT count(*) FROM identity.users WHERE normalized_email = @p0 AND status = 'PendingVerification'",
            email.ToLowerInvariant());
        status.ShouldBe(1);

        var codeCount = await ScalarLongAsync(
            "SELECT count(*) FROM identity.one_time_codes WHERE purpose = 'EmailVerification' AND consumed_at IS NULL");
        codeCount.ShouldBeGreaterThanOrEqualTo(1);

        var sent = await WaitForEmailAsync(email);
        sent.Subject.ShouldContain("Verify");
        ExtractCode(sent).ShouldMatch("^\\d{6}$");

        var events = await ScalarLongAsync(
            "SELECT count(*) FROM identity.security_events WHERE event_type = 'SignUp'");
        events.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task SignUp_Response_Is_Uniform_Across_Account_States()
    {
        var client = Client();
        var email = NewEmail();

        var first = await PostJsonAsync(client, "/api/v1/auth/sign-up", new { email, password = GoodPassword });
        var second = await PostJsonAsync(client, "/api/v1/auth/sign-up", new { email, password = "AnotherPass9" });
        var third = await PostJsonAsync(client, "/api/v1/auth/sign-up", new { email = NewEmail(), password = GoodPassword });

        first.GetProperty("message").GetString().ShouldBe(second.GetProperty("message").GetString());
        second.GetProperty("message").GetString().ShouldBe(third.GetProperty("message").GetString());
        first.GetProperty("success").GetBoolean().ShouldBeTrue();
        second.GetProperty("success").GetBoolean().ShouldBeTrue();
        third.GetProperty("success").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task SignUp_Existing_Active_Account_Gets_Notice_And_No_Code()
    {
        var client = Client();
        var email = NewEmail();
        var code = await SignUpAndGetCodeAsync(client, email);

        var verify = await client.SendAsync(
            Post("/api/v1/auth/verify-email", new { email, code }),
            TestContext.Current.CancellationToken);
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);

        var before = _factory.Emails.Sent.Count(m => m.To == email);
        var response = await client.SendAsync(
            Post("/api/v1/auth/sign-up", new { email, password = "DifferentPass9" }),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(15);
        EmailMessage? notice = null;
        while (TimeProvider.System.GetUtcNow() < deadline && notice is null)
        {
            notice = _factory.Emails.Sent.LastOrDefault(m => m.To == email && m.Subject.Contains("register your email"));
            if (notice is null)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }

        notice.ShouldNotBeNull();
        _factory.Emails.Sent.Count(m => m.To == email).ShouldBe(before + 1);
        Regex.IsMatch(notice.TextBody, @"code is \d{6}").ShouldBeFalse();

        var pendingCodes = await ScalarLongAsync(
            "SELECT count(*) FROM identity.one_time_codes c JOIN identity.users u ON u.id = c.user_id WHERE u.normalized_email = @p0 AND c.purpose = 'EmailVerification' AND c.consumed_at IS NULL",
            email.ToLowerInvariant());
        pendingCodes.ShouldBe(0);
    }

    [Fact]
    public async Task Concurrent_SignUps_Create_Exactly_One_User()
    {
        var client = Client();
        var email = NewEmail();

        var tasks = Enumerable.Range(0, 20).Select(i => client.SendAsync(
            Post("/api/v1/auth/sign-up", new { email, password = $"Concurrent{i}Pass" }),
            TestContext.Current.CancellationToken)).ToList();
        var responses = await Task.WhenAll(tasks);

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        var users = await ScalarLongAsync(
            "SELECT count(*) FROM identity.users WHERE normalized_email = @p0",
            email.ToLowerInvariant());
        users.ShouldBe(1);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("averyverylongpasswordthatexceedsthemaximumallowedlengthofthepolicycheck1234567890ANDMOREANDMOREANDMOREANDMOREANDMORE12345678901234567890")]
    [InlineData("BreachedMarker99")]
    public async Task SignUp_Weak_Passwords_Return_Field_Errors(string password)
    {
        var client = Client();
        var email = password == "BreachedMarker99" ? NewEmail() : $"weak-{Guid.CreateVersion7():N}@example.com";

        var json = await PostJsonAsync(client, "/api/v1/auth/sign-up", new { email, password });
        json.GetProperty("success").GetBoolean().ShouldBeFalse();
        json.GetProperty("error").GetProperty("code").GetString().ShouldBe("validation.failed");
        var details = json.GetProperty("error").GetProperty("details");
        details.EnumerateArray().ShouldContain(d => d.GetProperty("field").GetString() == "password");
    }

    [Fact]
    public async Task SignUp_Password_Containing_Email_Local_Part_Is_Rejected()
    {
        var client = Client();
        var local = $"localpart{Guid.CreateVersion7():N}"[..20];
        var email = $"{local}@example.com";

        var json = await PostJsonAsync(client, "/api/v1/auth/sign-up", new { email, password = $"{local}X9" });
        json.GetProperty("success").GetBoolean().ShouldBeFalse();
        json.GetProperty("error").GetProperty("code").GetString().ShouldBe("validation.failed");
    }

    [Fact]
    public async Task SignUp_Unknown_Property_Is_Rejected()
    {
        var client = Client();
        var json = await PostJsonAsync(client, "/api/v1/auth/sign-up",
            new Dictionary<string, object?> { ["email"] = NewEmail(), ["password"] = GoodPassword, ["roles"] = AdminRoles });
        json.GetProperty("success").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task SignUp_Idempotent_Replay_Returns_Identical_Response()
    {
        var client = Client();
        var email = NewEmail();
        var key = $"idem-{Guid.CreateVersion7():N}";
        var body = new { email, password = GoodPassword };

        var first = await client.SendAsync(Post("/api/v1/auth/sign-up", body, key), TestContext.Current.CancellationToken);
        var second = await client.SendAsync(Post("/api/v1/auth/sign-up", body, key), TestContext.Current.CancellationToken);

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.Headers.Contains("Idempotent-Replay").ShouldBeTrue();
        var firstJson = await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var secondJson = await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        secondJson.ShouldBe(firstJson);
    }

    [Fact]
    public async Task SignUp_Same_Key_Different_Body_Returns_Conflict()
    {
        var client = Client();
        var key = $"idem-{Guid.CreateVersion7():N}";

        await client.SendAsync(Post("/api/v1/auth/sign-up", new { email = NewEmail(), password = GoodPassword }, key),
            TestContext.Current.CancellationToken);
        using var response = await client.SendAsync(
            Post("/api/v1/auth/sign-up", new { email = NewEmail(), password = "Different9x" }, key),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }
}
