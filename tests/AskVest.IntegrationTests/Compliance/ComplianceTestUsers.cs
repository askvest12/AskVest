using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AskVest.IntegrationTests.Identity;
using AskVest.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Compliance;

internal static class ComplianceTestUsers
{
    public const string GoodPassword = "CorrectHorse9";
    private const string TermsHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PrivacyHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string RiskHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string TokenHash = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    public static IReadOnlyDictionary<string, string?> Settings(
        bool probesEnabled = true,
        bool realMoneyEnabled = true,
        string documentVersion = "v1")
    {
        var settings = new Dictionary<string, string?>
        {
            ["Diagnostics:EnableProbes"] = probesEnabled ? "true" : "false",
            ["Compliance:RealMoneyEnabled"] = realMoneyEnabled ? "true" : "false",
            ["Compliance:RealMoneyCountries:0"] = "NG",
            ["Compliance:Documents:0:Type"] = "terms",
            ["Compliance:Documents:0:Version"] = documentVersion,
            ["Compliance:Documents:0:Url"] = "https://askvest.example/terms",
            ["Compliance:Documents:0:Sha256"] = TermsHash,
            ["Compliance:Documents:0:RequiredFor"] = "practice",
            ["Compliance:Documents:1:Type"] = "privacy",
            ["Compliance:Documents:1:Version"] = documentVersion,
            ["Compliance:Documents:1:Url"] = "https://askvest.example/privacy",
            ["Compliance:Documents:1:Sha256"] = PrivacyHash,
            ["Compliance:Documents:1:RequiredFor"] = "practice",
            ["Compliance:Documents:2:Type"] = "risk_disclosure",
            ["Compliance:Documents:2:Version"] = documentVersion,
            ["Compliance:Documents:2:Url"] = "https://askvest.example/risk",
            ["Compliance:Documents:2:Sha256"] = RiskHash,
            ["Compliance:Documents:2:RequiredFor"] = "real",
            ["Compliance:Documents:3:Type"] = "token_disclosure",
            ["Compliance:Documents:3:Version"] = documentVersion,
            ["Compliance:Documents:3:Url"] = "https://askvest.example/token",
            ["Compliance:Documents:3:Sha256"] = TokenHash,
            ["Compliance:Documents:3:RequiredFor"] = "real",
        };
        return settings;
    }

    public static AuthApiFactory CreateFactory(PostgresFixture fixture, IReadOnlyDictionary<string, string?>? settings = null)
        => new(fixture.AppConnectionString, settings ?? Settings());

    public static async Task<string> CreateVerifiedUserAsync(AuthApiFactory factory)
    {
        var email = $"compliance-{Guid.CreateVersion7():N}@example.com";
        var client = factory.CreateClient();
        using var signUp = await client.SendAsync(
            Post("/api/v1/auth/sign-up", new { email, password = GoodPassword }),
            TestContext.Current.CancellationToken);
        signUp.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        var code = await WaitForCodeAsync(factory, email);
        using var verify = await client.SendAsync(
            Post("/api/v1/auth/verify-email", new { email, code }),
            TestContext.Current.CancellationToken);
        verify.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        using var login = await client.SendAsync(
            Post("/api/v1/auth/login", new
            {
                email,
                password = GoodPassword,
                device = new { installationId = $"dev-{Guid.CreateVersion7():N}", platform = "android", deviceName = "Test", appVersion = "1.0.0" },
            }),
            TestContext.Current.CancellationToken);
        login.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var json = await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return json.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    public static async Task<string> WaitForCodeAsync(AuthApiFactory factory, string to)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(30);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var match = factory.Emails.Sent.LastOrDefault(m => m.To == to);
            if (match is not null)
            {
                var regex = Regex.Match(match.TextBody, @"code is (\d{6})");
                regex.Success.ShouldBeTrue();
                return regex.Groups[1].Value;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"No email captured for {to}.");
    }

    public static HttpRequestMessage Post(string path, object body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? $"k-{Guid.CreateVersion7():N}");
        return request;
    }

    public static HttpRequestMessage Put(string path, object body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? $"k-{Guid.CreateVersion7():N}");
        return request;
    }

    public static HttpRequestMessage Get(string path)
        => new(HttpMethod.Get, new Uri(path, UriKind.Relative));

    public static HttpRequestMessage Auth(this HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return json.GetProperty("data");
    }

    public static async Task<JsonElement> EnvelopeAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static string ErrorCode(JsonElement envelope)
        => envelope.GetProperty("error").GetProperty("code").GetString()!;

    public static List<string> ErrorDetailFields(JsonElement envelope)
        => envelope.GetProperty("error").GetProperty("details")
            .EnumerateArray()
            .Select(d => d.GetProperty("field").GetString()!)
            .ToList();
}
