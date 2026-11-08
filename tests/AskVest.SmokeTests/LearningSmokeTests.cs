using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace AskVest.SmokeTests;

[Collection("SmokeStack")]
public sealed class LearningSmokeTests
{
    private static readonly string? BaseUrl = Environment.GetEnvironmentVariable("ASKVEST_SMOKE_BASE_URL");
    private static readonly string? MailpitUrl = Environment.GetEnvironmentVariable("ASKVEST_SMOKE_MAILPIT_URL");
    private static readonly string? WebhookSecret = Environment.GetEnvironmentVariable("ASKVEST_COMPLIANCE_KYC_WEBHOOK_SECRET");

    private const string Password = "CorrectHorseSmoke9";

    private static HttpClient Client()
    {
        if (BaseUrl is null)
        {
            Assert.Skip("ASKVEST_SMOKE_BASE_URL is not set; smoke tests require a running stack.");
        }

        return new HttpClient { BaseAddress = new Uri(BaseUrl) };
    }

    private static HttpClient Mailpit()
    {
        if (MailpitUrl is null)
        {
            Assert.Skip("ASKVEST_SMOKE_MAILPIT_URL is not set; smoke tests require Mailpit.");
        }

        return new HttpClient { BaseAddress = new Uri(MailpitUrl) };
    }

    private static void RequireWebhookSecret()
    {
        if (WebhookSecret is null)
        {
            Assert.Skip("ASKVEST_COMPLIANCE_KYC_WEBHOOK_SECRET is not set; the smoke stack secret must be exported.");
        }
    }

    private static string NewEmail() => $"smoke-lrn-{Guid.CreateVersion7():N}@example.com";

    private static HttpRequestMessage Request(HttpMethod method, string path, object? body, string? token = null, string? key = null)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", key ?? $"k-{Guid.CreateVersion7():N}");
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static async Task<JsonElement> SendJsonAsync(HttpClient client, HttpRequestMessage request)
    {
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            await Task.Delay(TimeSpan.FromSeconds(11), TestContext.Current.CancellationToken);
            using var retry = await client.SendAsync(await CloneAsync(request), TestContext.Current.CancellationToken);
            return await retry.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("success").GetBoolean().ShouldBeTrue($"expected success, got: {json.GetRawText()}");
        return json;
    }

    private static async Task<JsonElement> SendJsonWithStatusAsync(
        HttpClient client, HttpRequestMessage request, HttpStatusCode expected)
    {
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests && expected != HttpStatusCode.TooManyRequests)
        {
            await Task.Delay(TimeSpan.FromSeconds(11), TestContext.Current.CancellationToken);
            using var retry = await client.SendAsync(await CloneAsync(request), TestContext.Current.CancellationToken);
            retry.StatusCode.ShouldBe(expected);
            return await retry.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        }

        response.StatusCode.ShouldBe(expected);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Content is not null)
        {
            clone.Content = new StringContent(
                await request.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                Encoding.UTF8,
                "application/json");
        }

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private static async Task<string> WaitForCodeAsync(HttpClient mailpit, string to)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(60);
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var matches = await mailpit.GetFromJsonAsync<JsonElement>(
                new Uri("/api/v1/messages?limit=50", UriKind.Relative), TestContext.Current.CancellationToken);
            var messages = matches.GetProperty("messages").EnumerateArray()
                .Where(m => m.GetProperty("To").EnumerateArray().Any(t => t.GetProperty("Address").GetString() == to))
                .Where(m => m.GetProperty("Subject").GetString()!.Contains("Verify", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (messages.Count > 0)
            {
                var latest = messages[^1];
                var detail = await mailpit.GetFromJsonAsync<JsonElement>(
                    new Uri($"/api/v1/message/{latest.GetProperty("ID").GetString()}", UriKind.Relative),
                    TestContext.Current.CancellationToken);
                var text = detail.GetProperty("Text").GetString() ?? string.Empty;
                var match = Regex.Match(text, @"code is (\d{6})");
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"No verification email captured for {to}.");
    }

    private static async Task<string> SignupVerifiedAsync(HttpClient client, HttpClient mailpit, string email)
    {
        await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/auth/sign-up", new { email, password = Password }));
        var code = await WaitForCodeAsync(mailpit, email);
        await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/auth/verify-email", new { email, code }));
        var login = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/auth/login", new
        {
            email,
            password = Password,
            device = new { installationId = $"dev-{Guid.CreateVersion7():N}", platform = "android", deviceName = "Smoke", appVersion = "1.0.0" },
        }));
        return login.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    private static async Task<JsonElement> EligibilityAsync(HttpClient client, string token)
    {
        var json = await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/compliance/eligibility", null, token));
        return json.GetProperty("data");
    }

    private static IEnumerable<string> BlockerCodes(JsonElement eligibility, string mode)
        => eligibility.GetProperty("blockers").GetProperty(mode).EnumerateArray()
            .Select(b => b.GetProperty("code").GetString()!);

    private static async Task MakeRealReadyAsync(HttpClient client, HttpClient mailpit, string token)
    {
        var docs = await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/compliance/documents", null, token));
        var items = docs.GetProperty("data").GetProperty("documents").EnumerateArray()
            .Where(d => !d.GetProperty("accepted").GetBoolean())
            .Select(d => new { type = d.GetProperty("type").GetString()!, version = d.GetProperty("version").GetString()!, sha256 = d.GetProperty("sha256").GetString()! })
            .ToList();
        await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/compliance/consents", new { documents = items }, token));
        await SendJsonAsync(client, Request(HttpMethod.Put, "/api/v1/compliance/country", new { country = "NG" }, token));

        var started = await SendJsonAsync(client, Request(HttpMethod.Post, "/api/v1/compliance/kyc/start", null, token));
        var reference = started.GetProperty("data").GetProperty("providerReference").GetString()!;

        var body = "{"
            + $"\"eventId\":\"evt-{Guid.CreateVersion7():N}\","
            + $"\"providerReference\":\"{reference}\","
            + "\"outcome\":\"approved\","
            + "\"verifiedCountry\":\"NG\","
            + "\"fullName\":\"Learning Smoke\","
            + "\"dateOfBirth\":\"1990-04-12\""
            + "}";
        var timestamp = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(WebhookSecret!));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();
        using var webhook = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/webhooks/kyc/fake", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        webhook.Headers.Add("X-Webhook-Timestamp", timestamp);
        webhook.Headers.Add("X-Webhook-Signature", signature);
        using var webhookResponse = await client.SendAsync(webhook, TestContext.Current.CancellationToken);
        webhookResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static object Answers(IEnumerable<(string QuestionId, string OptionId)> pairs)
        => new { answers = pairs.Select(p => new { questionId = p.QuestionId, optionId = p.OptionId }).ToArray() };

    [Fact]
    public async Task Learning_Journey_Reads_Grading_Completion_Streaks_And_Gate()
    {
        RequireWebhookSecret();
        using var client = Client();
        using var mailpit = Mailpit();

        // Anonymous calls are refused everywhere.
        using var anonymous = await client.SendAsync(
            Request(HttpMethod.Get, "/api/v1/learning/tracks", null),
            TestContext.Current.CancellationToken);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var anonymousProgress = await client.SendAsync(
            Request(HttpMethod.Get, "/api/v1/learning/progress", null),
            TestContext.Current.CancellationToken);
        anonymousProgress.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var tokenA = await SignupVerifiedAsync(client, mailpit, NewEmail());

        // 1. Tracks list lessons; lesson reads carry no answers or explanations.
        var tracks = await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/learning/tracks", null, tokenA));
        var lessons = tracks.GetProperty("data").GetProperty("tracks").EnumerateArray()
            .SelectMany(t => t.GetProperty("lessons").EnumerateArray())
            .ToList();
        lessons.ShouldContain(l => l.GetProperty("id").GetString() == "what-is-a-stock");
        lessons.ShouldContain(l => l.GetProperty("id").GetString() == "what-you-own"
            && l.GetProperty("requiredForReal").GetBoolean());

        using var detailResponse = await client.SendAsync(
            Request(HttpMethod.Get, "/api/v1/learning/lessons/what-is-a-stock", null, tokenA),
            TestContext.Current.CancellationToken);
        detailResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detailRaw = await detailResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        detailRaw.ShouldNotContain("\"correctOptionId\"");
        detailRaw.ShouldNotContain("\"explanation\"");

        // 2. A wrong attempt fails with per-question results; a correct one completes.
        var failKey = $"smoke-fail-{Guid.CreateVersion7():N}";
        var failed = await SendJsonWithStatusAsync(
            client,
            Request(HttpMethod.Post, "/api/v1/learning/lessons/what-is-a-stock/quiz-attempts",
                Answers(SmokeLearning.WrongAnswers("what-is-a-stock")), tokenA, failKey),
            HttpStatusCode.OK);
        var failedData = failed.GetProperty("data");
        failedData.GetProperty("passed").GetBoolean().ShouldBeFalse();
        failedData.GetProperty("score").GetInt32().ShouldBe(0);
        var questions = failedData.GetProperty("questions").EnumerateArray().ToList();
        questions.Count.ShouldBe(3);
        questions.ShouldAllBe(q => !q.GetProperty("correct").GetBoolean());
        questions.ShouldAllBe(q => q.GetProperty("explanation").GetString()!.Length > 0);

        (await SmokeLearning.LessonProgressAsync(client, tokenA, "what-is-a-stock", TestContext.Current.CancellationToken))
            .ShouldBe("attempted");

        var passKey = $"smoke-pass-{Guid.CreateVersion7():N}";
        var passed = await SendJsonWithStatusAsync(
            client,
            Request(HttpMethod.Post, "/api/v1/learning/lessons/what-is-a-stock/quiz-attempts",
                Answers(SmokeLearning.CorrectAnswers("what-is-a-stock")), tokenA, passKey),
            HttpStatusCode.OK);
        var passedData = passed.GetProperty("data");
        passedData.GetProperty("passed").GetBoolean().ShouldBeTrue();
        passedData.GetProperty("score").GetInt32().ShouldBe(100);
        passedData.GetProperty("attemptsCount").GetInt32().ShouldBe(2);
        passedData.GetProperty("lessonComplete").GetBoolean().ShouldBeTrue();

        var streakA = (await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/learning/progress", null, tokenA)))
            .GetProperty("data").GetProperty("streak");
        streakA.GetProperty("current").GetInt32().ShouldBe(1);
        streakA.GetProperty("todayCounted").GetBoolean().ShouldBeTrue();

        // 3. Replaying the passing key returns the identical stored response.
        var replay = await SendJsonWithStatusAsync(
            client,
            Request(HttpMethod.Post, "/api/v1/learning/lessons/what-is-a-stock/quiz-attempts",
                Answers(SmokeLearning.CorrectAnswers("what-is-a-stock")), tokenA, passKey),
            HttpStatusCode.OK);
        replay.GetRawText().ShouldBe(passed.GetRawText());

        // 4. Complete works for the quiz-less lesson; quiz lessons refuse it.
        var completed = await SendJsonWithStatusAsync(
            client,
            Request(HttpMethod.Post, "/api/v1/learning/lessons/risk-and-diversification/complete", new { }, tokenA),
            HttpStatusCode.OK);
        completed.GetProperty("data").GetProperty("status").GetString().ShouldBe("completed");
        var quizComplete = await SendJsonWithStatusAsync(
            client,
            Request(HttpMethod.Post, "/api/v1/learning/lessons/what-is-a-stock/complete", new { }, tokenA),
            HttpStatusCode.UnprocessableEntity);
        quizComplete.GetProperty("error").GetProperty("code").GetString().ShouldBe("learning.quiz_required");

        // 5. Missing and unknown answers are rejected before grading.
        var missing = SmokeLearning.CorrectAnswers("fees-and-costs").Take(2).ToArray();
        var missingAttempt = await SendJsonWithStatusAsync(
            client,
            Request(HttpMethod.Post, "/api/v1/learning/lessons/fees-and-costs/quiz-attempts",
                Answers(missing), tokenA),
            HttpStatusCode.BadRequest);
        missingAttempt.GetProperty("error").GetProperty("code").GetString().ShouldBe("validation.failed");

        var unknown = new[] { new { questionId = "q9", optionId = "a" } };
        var unknownAttempt = await SendJsonWithStatusAsync(
            client,
            Request(HttpMethod.Post, "/api/v1/learning/lessons/fees-and-costs/quiz-attempts",
                new { answers = unknown }, tokenA),
            HttpStatusCode.BadRequest);
        unknownAttempt.GetProperty("error").GetProperty("code").GetString().ShouldBe("validation.failed");

        // One real attempt on fees-and-costs, then a twenty-way burst.
        await SmokeLearning.SubmitAttemptAsync(
            client, tokenA, "fees-and-costs",
            Answers(SmokeLearning.CorrectAnswers("fees-and-costs")),
            TestContext.Current.CancellationToken);
        var burst = Enumerable.Range(0, 20).Select(index =>
            SmokeLearning.SubmitAttemptAsync(
                client, tokenA, "fees-and-costs",
                Answers(SmokeLearning.CorrectAnswers("fees-and-costs")),
                TestContext.Current.CancellationToken,
                $"smoke-burst-{index:D2}")).ToList();
        var burstResults = await Task.WhenAll(burst);
        burstResults
            .Select(r => r.GetProperty("data").GetProperty("attemptsCount").GetInt32())
            .OrderBy(count => count)
            .ShouldBe(Enumerable.Range(2, 20));

        // 6. A second fully eligible user hits learning_required, then clears it.
        var progressBeforeB = await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/learning/progress", null, tokenA));
        var tokenB = await SignupVerifiedAsync(client, mailpit, NewEmail());
        await MakeRealReadyAsync(client, mailpit, tokenB);

        var gated = await EligibilityAsync(client, tokenB);
        gated.GetProperty("realAllowed").GetBoolean().ShouldBeFalse();
        var learningBlocker = BlockerCodes(gated, "real")
            .Where(code => code == "learning_required").ToList();
        learningBlocker.ShouldHaveSingleItem();
        gated.GetProperty("blockers").GetProperty("real").EnumerateArray()
            .Single(b => b.GetProperty("code").GetString() == "learning_required")
            .GetProperty("message").GetString()!.ShouldContain("what-you-own");

        var realDenied = await SendJsonWithStatusAsync(
            client, Request(HttpMethod.Get, "/api/v1/real/diagnostics/ping", null, tokenB),
            HttpStatusCode.Forbidden);
        realDenied.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(d => d.GetProperty("field").GetString()).ShouldContain("learning_required");

        await SmokeLearning.CompleteRequiredLessonsAsync(client, tokenB, TestContext.Current.CancellationToken);

        var ungated = await EligibilityAsync(client, tokenB);
        ungated.GetProperty("realAllowed").GetBoolean().ShouldBeTrue();
        var realPing = await SendJsonWithStatusAsync(
            client, Request(HttpMethod.Get, "/api/v1/real/diagnostics/ping", null, tokenB),
            HttpStatusCode.OK);
        realPing.GetProperty("data").GetProperty("mode").GetString().ShouldBe("real");

        // 7. User A's progress is untouched by user B's activity.
        var progressAAfter = await SendJsonAsync(client, Request(HttpMethod.Get, "/api/v1/learning/progress", null, tokenA));
        progressAAfter.GetProperty("data").GetProperty("streak").GetProperty("current").GetInt32()
            .ShouldBe(progressBeforeB.GetProperty("data").GetProperty("streak").GetProperty("current").GetInt32());
        progressAAfter.GetProperty("data").GetProperty("tracks").EnumerateArray()
            .Sum(t => t.GetProperty("completedLessons").GetInt32())
            .ShouldBe(progressBeforeB.GetProperty("data").GetProperty("tracks").EnumerateArray()
                .Sum(t => t.GetProperty("completedLessons").GetInt32()));
    }
}
