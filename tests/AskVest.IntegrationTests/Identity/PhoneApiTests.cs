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
public sealed class PhoneApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private AuthApiFactory _factory = null!;
    private AuthApiFactory _flagOffFactory = null!;

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
        _flagOffFactory = new AuthApiFactory(fixture.AppConnectionString);
        _factory.CreateClient();
        _flagOffFactory.CreateClient();
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _flagOffFactory.DisposeAsync();
    }

    private const string GoodPassword = "CorrectHorse9";
    private const string SmsInbox = "sms-capture@example.com";

    private static string NewEmail() => $"ph-{Guid.CreateVersion7():N}@example.com";

    private static object Device(string id) => new
    {
        installationId = id,
        platform = "android",
        deviceName = "Test Phone",
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

    private async Task<string> ActivateAndLoginAsync(HttpClient client, string email, CapturingEmailTransport? emails = null)
    {
        using var signUp = await client.SendAsync(
            Post("/api/v1/auth/sign-up", new { email, password = GoodPassword }),
            TestContext.Current.CancellationToken);
        signUp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var code = await WaitForCodeAsync(emails ?? _factory.Emails, email, 0);
        using var verify = await client.SendAsync(
            Post("/api/v1/auth/verify-email", new { email, code }),
            TestContext.Current.CancellationToken);
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var login = await client.SendAsync(
            Post("/api/v1/auth/login", new { email, password = GoodPassword, device = Device("ph-device") }),
            TestContext.Current.CancellationToken);
        var json = await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return json.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    private static async Task<string> WaitForCodeAsync(
        CapturingEmailTransport emails, string to, int skip = 0)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(30);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var matches = emails.Sent.Where(m => m.To == to).ToList();
            if (matches.Count > skip)
            {
                var match = Regex.Match(matches[^1].TextBody, @"code is (\d{6})");
                match.Success.ShouldBeTrue();
                return match.Groups[1].Value;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"No email captured for {to}.");
    }

    private async Task<(string? Phone, DateTime? VerifiedAt)> PhoneStateAsync(string email)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT phone_e164, phone_verified_at FROM identity.users WHERE normalized_email = @e",
            connection);
        command.Parameters.AddWithValue("e", email);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        return (
            await reader.IsDBNullAsync(0, TestContext.Current.CancellationToken) ? null : reader.GetString(0),
            await reader.IsDBNullAsync(1, TestContext.Current.CancellationToken)
                ? null
                : await reader.GetFieldValueAsync<DateTime>(1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Phone_Routes_Do_Not_Exist_When_Flag_Is_Off()
    {
        var client = _flagOffFactory.CreateClient();
        var email = NewEmail();
        var token = await ActivateAndLoginAsync(client, email, _flagOffFactory.Emails);

        using var add = await client.SendAsync(
            PostAuth("/api/v1/account/phone", new { phone = "+14155552001" }, token),
            TestContext.Current.CancellationToken);
        add.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var verify = await client.SendAsync(
            PostAuth("/api/v1/account/phone/verify", new { code = "123456" }, token),
            TestContext.Current.CancellationToken);
        verify.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Add_And_Verify_Phone()
    {
        var client = _factory.CreateClient();
        var email = NewEmail();
        var token = await ActivateAndLoginAsync(client, email);

        using var add = await client.SendAsync(
            PostAuth("/api/v1/account/phone", new { phone = "+1 (415) 555-2002" }, token),
            TestContext.Current.CancellationToken);
        add.StatusCode.ShouldBe(HttpStatusCode.OK);

        var code = await WaitForCodeAsync(_factory.Emails, SmsInbox, skip: 0);
        var state = await PhoneStateAsync(email);
        state.Phone.ShouldBeNull();

        using var verify = await client.SendAsync(
            PostAuth("/api/v1/account/phone/verify", new { code }, token),
            TestContext.Current.CancellationToken);
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);

        state = await PhoneStateAsync(email);
        state.Phone.ShouldBe("+14155552002");
        state.VerifiedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Invalid_Phone_And_Code_Rejected()
    {
        var client = _factory.CreateClient();
        var email = NewEmail();
        var token = await ActivateAndLoginAsync(client, email);

        using var badPhone = await client.SendAsync(
            PostAuth("/api/v1/account/phone", new { phone = "not-a-phone" }, token),
            TestContext.Current.CancellationToken);
        badPhone.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var noCode = await client.SendAsync(
            PostAuth("/api/v1/account/phone/verify", new { code = "123456" }, token),
            TestContext.Current.CancellationToken);
        noCode.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        using var add = await client.SendAsync(
            PostAuth("/api/v1/account/phone", new { phone = "+14155552003" }, token),
            TestContext.Current.CancellationToken);
        add.StatusCode.ShouldBe(HttpStatusCode.OK);
        await WaitForCodeAsync(_factory.Emails, SmsInbox);

        using var wrongCode = await client.SendAsync(
            PostAuth("/api/v1/account/phone/verify", new { code = "000000" }, token),
            TestContext.Current.CancellationToken);
        wrongCode.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Verified_Number_Is_Unique_And_Conflict_Looks_Like_Invalid_Code()
    {
        var clientA = _factory.CreateClient();
        var clientB = _factory.CreateClient();
        var emailA = NewEmail();
        var emailB = NewEmail();
        var tokenA = await ActivateAndLoginAsync(clientA, emailA);
        var tokenB = await ActivateAndLoginAsync(clientB, emailB);

        const string sharedPhone = "+14155552004";
        using var addA = await clientA.SendAsync(
            PostAuth("/api/v1/account/phone", new { phone = sharedPhone }, tokenA),
            TestContext.Current.CancellationToken);
        addA.StatusCode.ShouldBe(HttpStatusCode.OK);
        var codeA = await WaitForCodeAsync(_factory.Emails, SmsInbox, skip: 0);

        using var addB = await clientB.SendAsync(
            PostAuth("/api/v1/account/phone", new { phone = sharedPhone }, tokenB),
            TestContext.Current.CancellationToken);
        addB.StatusCode.ShouldBe(HttpStatusCode.OK);
        var codeB = await WaitForCodeAsync(_factory.Emails, SmsInbox, skip: 1);

        using var verifyA = await clientA.SendAsync(
            PostAuth("/api/v1/account/phone/verify", new { code = codeA }, tokenA),
            TestContext.Current.CancellationToken);
        verifyA.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var verifyB = await clientB.SendAsync(
            PostAuth("/api/v1/account/phone/verify", new { code = codeB }, tokenB),
            TestContext.Current.CancellationToken);
        verifyB.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await verifyB.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("auth.invalid_code");

        var stateB = await PhoneStateAsync(emailB);
        stateB.Phone.ShouldBeNull();
    }
}
