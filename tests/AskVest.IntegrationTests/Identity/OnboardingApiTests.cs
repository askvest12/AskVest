using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Identity.Messaging;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Identity;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class OnboardingApiTests(PostgresFixture fixture) : IAsyncLifetime
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

    private static string NewEmail() => $"onboarding-{Guid.CreateVersion7():N}@example.com";

    private const string GoodPassword = "CorrectHorse9";

    private static readonly string[] TwoGoals = ["learn_investing", "practice_first"];
    private static readonly string[] OneGoal = ["build_wealth"];
    private static readonly string[] DiversifyGoal = ["diversify"];
    private static readonly string[] UnknownGoal = ["get_rich"];

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

    private static HttpRequestMessage PutAuth(string path, object body, string token, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", key ?? $"k-{Guid.CreateVersion7():N}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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

    private async Task<string> ActivateAndLoginAsync(HttpClient client, string email, string? fullName = null)
    {
        using var signUp = await client.SendAsync(
            Post("/api/v1/auth/sign-up", new { email, password = GoodPassword, fullName }),
            TestContext.Current.CancellationToken);
        signUp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var code = ExtractCode(await WaitForEmailAsync(email));
        using var verify = await client.SendAsync(
            Post("/api/v1/auth/verify-email", new { email, code }),
            TestContext.Current.CancellationToken);
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var login = await client.SendAsync(
            Post("/api/v1/auth/login", new { email, password = GoodPassword, device = Device("device-1") }),
            TestContext.Current.CancellationToken);
        var json = await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var data = json.GetProperty("data");
        data.GetProperty("result").GetString().ShouldBe("authenticated");
        return data.GetProperty("accessToken").GetString()!;
    }

    private static async Task<JsonElement> MeAsync(HttpClient client, string token)
    {
        using var response = await client.SendAsync(
            Get("/api/v1/account/me", token), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return json.GetProperty("data");
    }

    [Fact]
    public async Task SignUp_With_FullName_Shows_In_Me()
    {
        var client = Client();
        var email = NewEmail();
        var token = await ActivateAndLoginAsync(client, email, "Amara Okafor");

        var me = await MeAsync(client, token);
        me.GetProperty("fullName").GetString().ShouldBe("Amara Okafor");
        me.GetProperty("onboarding").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Onboarding_Save_And_Me_Return_Profile()
    {
        var client = Client();
        var token = await ActivateAndLoginAsync(client, NewEmail());

        using var save = await client.SendAsync(
            PutAuth("/api/v1/account/onboarding", new
            {
                fullName = "Amara Okafor",
                familiarity = "newcomer",
                goals = TwoGoals,
                preferredCurrency = "ngn",
            }, token),
            TestContext.Current.CancellationToken);
        save.StatusCode.ShouldBe(HttpStatusCode.OK);
        var saved = await save.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var data = saved.GetProperty("data");
        data.GetProperty("familiarity").GetString().ShouldBe("newcomer");
        data.GetProperty("preferredCurrency").GetString().ShouldBe("NGN");
        data.GetProperty("completedAt").ValueKind.ShouldBe(JsonValueKind.String);
        var goals = data.GetProperty("goals").EnumerateArray().Select(g => g.GetString()).ToList();
        goals.ShouldBe(["learn_investing", "practice_first"]);

        var me = await MeAsync(client, token);
        me.GetProperty("fullName").GetString().ShouldBe("Amara Okafor");
        var onboarding = me.GetProperty("onboarding");
        onboarding.GetProperty("familiarity").GetString().ShouldBe("newcomer");
        onboarding.GetProperty("preferredCurrency").GetString().ShouldBe("NGN");
        onboarding.GetProperty("completedAt").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task Onboarding_Partial_Updates_Then_Completes()
    {
        var client = Client();
        var token = await ActivateAndLoginAsync(client, NewEmail());

        using var first = await client.SendAsync(
            PutAuth("/api/v1/account/onboarding", new { familiarity = "some" }, token),
            TestContext.Current.CancellationToken);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstData = (await first.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("data");
        firstData.GetProperty("completedAt").ValueKind.ShouldBe(JsonValueKind.Null);

        using var second = await client.SendAsync(
            PutAuth("/api/v1/account/onboarding", new { goals = OneGoal }, token),
            TestContext.Current.CancellationToken);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var secondData = (await second.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("data");
        secondData.GetProperty("familiarity").GetString().ShouldBe("some");
        secondData.GetProperty("completedAt").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task Onboarding_Rejects_Unknown_Values()
    {
        var client = Client();
        var token = await ActivateAndLoginAsync(client, NewEmail());

        using var badFamiliarity = await client.SendAsync(
            PutAuth("/api/v1/account/onboarding", new { familiarity = "expert" }, token),
            TestContext.Current.CancellationToken);
        badFamiliarity.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var badGoal = await client.SendAsync(
            PutAuth("/api/v1/account/onboarding", new { goals = UnknownGoal }, token),
            TestContext.Current.CancellationToken);
        badGoal.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var badCurrency = await client.SendAsync(
            PutAuth("/api/v1/account/onboarding", new { preferredCurrency = "NGNX" }, token),
            TestContext.Current.CancellationToken);
        badCurrency.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Onboarding_Requires_Authentication()
    {
        var client = Client();
        using var response = await client.SendAsync(
            PutAuth("/api/v1/account/onboarding", new { familiarity = "newcomer" }, "not-a-token"),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Onboarding_Replay_Returns_Stored_Response()
    {
        var client = Client();
        var token = await ActivateAndLoginAsync(client, NewEmail());
        var key = $"k-{Guid.CreateVersion7():N}";
        var body = new { familiarity = "experienced", goals = DiversifyGoal };

        using var first = await client.SendAsync(
            PutAuth("/api/v1/account/onboarding", body, token, key),
            TestContext.Current.CancellationToken);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstJson = await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using var replay = await client.SendAsync(
            PutAuth("/api/v1/account/onboarding", body, token, key),
            TestContext.Current.CancellationToken);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        var replayJson = await replay.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        replayJson.ShouldBe(firstJson);
    }
}
