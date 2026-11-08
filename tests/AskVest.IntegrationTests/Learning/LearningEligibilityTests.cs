using System.Net;
using System.Text;
using System.Text.Json;
using AskVest.IntegrationTests.Compliance;
using AskVest.IntegrationTests.Identity;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Compliance.Providers;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Learning;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class LearningEligibilityTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string WebhookSecret = "test-kyc-webhook-secret-0123456789abcdef";
    private AuthApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        Environment.SetEnvironmentVariable(
            "Learning__ContentPath", fixture.LearningContentDirectory);
        LearningTestContent.WriteValidContent(fixture.LearningContentDirectory);
        _factory = ComplianceTestUsers.CreateFactory(fixture);
        _factory.CreateClient();
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        LearningTestContent.WriteContent(fixture.LearningContentDirectory, """{"tracks":[]}""");
    }

    private HttpClient Client() => _factory.CreateClient();

    private async Task<string> RealReadyUserAsync()
    {
        var client = Client();
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        await ComplianceTestUsers.AcceptDocumentsAsync(client, token, ComplianceTestUsers.AllDocuments);
        await ComplianceTestUsers.SetCountryAsync(client, token, "NG");

        using var start = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/compliance/kyc/start", new { }).Auth(token),
            TestContext.Current.CancellationToken);
        start.StatusCode.ShouldBe(HttpStatusCode.OK);
        var data = await ComplianceTestUsers.DataAsync(start);
        var reference = data.GetProperty("providerReference").GetString()!;

        var body = JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["eventId"] = $"evt-{Guid.CreateVersion7():N}",
            ["providerReference"] = reference,
            ["outcome"] = "approved",
            ["verifiedCountry"] = "NG",
            ["fullName"] = "Amara Okafor",
            ["dateOfBirth"] = "1990-04-12",
        });
        var timestamp = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds().ToString();
        using var webhook = await client.SendAsync(
            SignedWebhook(body, timestamp),
            TestContext.Current.CancellationToken);
        webhook.StatusCode.ShouldBe(HttpStatusCode.OK);
        return token;
    }

    private static HttpRequestMessage SignedWebhook(string body, string timestamp)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/v1/webhooks/kyc/fake", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Webhook-Timestamp", timestamp);
        request.Headers.Add(
            "X-Webhook-Signature",
            FakeKycProvider.Signature(timestamp, body, WebhookSecret));
        return request;
    }

    private static async Task<JsonElement> EligibilityAsync(HttpClient client, string token)
    {
        using var response = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/compliance/eligibility").Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await ComplianceTestUsers.DataAsync(response);
    }

    private static async Task<int> RealProbeStatusAsync(HttpClient client, string token)
    {
        using var response = await client.SendAsync(
            ComplianceTestUsers.Get("/api/v1/real/diagnostics/ping").Auth(token),
            TestContext.Current.CancellationToken);
        return (int)response.StatusCode;
    }

    [Fact]
    public async Task Required_Lesson_Blocks_Real_Until_Passed_At_Current_Version()
    {
        var client = Client();
        var token = await RealReadyUserAsync();

        var blocked = await EligibilityAsync(client, token);
        blocked.GetProperty("practiceAllowed").GetBoolean().ShouldBeTrue();
        blocked.GetProperty("realAllowed").GetBoolean().ShouldBeFalse();
        var realBlockers = blocked.GetProperty("blockers").GetProperty("real");
        var learning = realBlockers.EnumerateArray()
            .Single(b => b.GetProperty("code").GetString() == "learning_required");
        learning.GetProperty("message").GetString()!.ShouldContain("required-lesson");
        (await RealProbeStatusAsync(client, token)).ShouldBe(403);

        using var attempt = await client.SendAsync(
            ComplianceTestUsers.Post(
                "/api/v1/learning/lessons/required-lesson/quiz-attempts",
                new
                {
                    answers = new[] { new { questionId = "q1", optionId = "a" } },
                },
                "gate-attempt-key-1").Auth(token),
            TestContext.Current.CancellationToken);
        attempt.StatusCode.ShouldBe(HttpStatusCode.OK);

        var cleared = await EligibilityAsync(client, token);
        cleared.GetProperty("realAllowed").GetBoolean().ShouldBeTrue();
        cleared.GetProperty("blockers").GetProperty("real").GetArrayLength().ShouldBe(0);
        (await RealProbeStatusAsync(client, token)).ShouldBe(200);
    }

    [Fact]
    public async Task No_Flagged_Lessons_Means_No_Learning_Blocker()
    {
        LearningTestContent.WriteContent(fixture.LearningContentDirectory, """{"tracks":[]}""");
        await _factory.DisposeAsync();
        _factory = ComplianceTestUsers.CreateFactory(fixture);
        _factory.CreateClient();

        var client = Client();
        var token = await RealReadyUserAsync();

        var eligibility = await EligibilityAsync(client, token);
        eligibility.GetProperty("realAllowed").GetBoolean().ShouldBeTrue();
        eligibility.GetProperty("blockers").GetProperty("real").GetArrayLength().ShouldBe(0);
        (await RealProbeStatusAsync(client, token)).ShouldBe(200);
    }
}
