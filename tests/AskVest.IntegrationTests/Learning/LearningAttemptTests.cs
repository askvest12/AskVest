using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AskVest.IntegrationTests.Compliance;
using AskVest.IntegrationTests.Identity;
using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Learning.Endpoints;
using AskVest.Modules.Learning.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Learning;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class LearningAttemptTests(PostgresFixture fixture) : IAsyncLifetime
{
    private AuthApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        Environment.SetEnvironmentVariable(
            "Learning__ContentPath", fixture.LearningContentDirectory);
        LearningTestContent.WriteValidContent(fixture.LearningContentDirectory);
        _factory = new AuthApiFactory(
            fixture.AppConnectionString, ComplianceTestUsers.Settings());
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient Client() => _factory.CreateClient();

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
        => await ComplianceTestUsers.DataAsync(response);

    private static object PassingAnswers(string lesson = "quiz-lesson")
        => new
        {
            answers = new[]
            {
                new { questionId = "q1", optionId = "b" },
                new { questionId = "q2", optionId = "a" },
                new { questionId = "q3", optionId = "y" },
            },
        };

    private static object FailingAnswers()
        => new
        {
            answers = new[]
            {
                new { questionId = "q1", optionId = "a" },
                new { questionId = "q2", optionId = "b" },
                new { questionId = "q3", optionId = "x" },
            },
        };

    private static object RequiredLessonAnswers()
        => new
        {
            answers = new[] { new { questionId = "q1", optionId = "a" } },
        };

    private async Task<string> CreateUserAsync()
    {
        var email = $"learning-{Guid.CreateVersion7():N}@example.com";
        var client = Client();
        using var signUp = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/auth/sign-up", new
            {
                email,
                password = ComplianceTestUsers.GoodPassword,
            }),
            TestContext.Current.CancellationToken);
        signUp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var code = await ComplianceTestUsers.WaitForCodeAsync(_factory, email);
        using var verify = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/auth/verify-email", new { email, code }),
            TestContext.Current.CancellationToken);
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await LoginAsync(email);
    }

    private async Task<string> LoginAsync(string email, string? installationId = null)
    {
        var client = Client();
        using var login = await client.SendAsync(
            ComplianceTestUsers.Post("/api/v1/auth/login", new
            {
                email,
                password = ComplianceTestUsers.GoodPassword,
                device = new
                {
                    installationId = installationId ?? $"dev-{Guid.CreateVersion7():N}",
                    platform = "android",
                    deviceName = "Test",
                    appVersion = "1.0.0",
                },
            }),
            TestContext.Current.CancellationToken);
        var raw = await login.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        login.StatusCode.ShouldBe(HttpStatusCode.OK, raw);
        var json = JsonDocument.Parse(raw).RootElement;
        return json.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    private async Task<JsonElement> AttemptAsync(
        string token, string lessonId, object body, string? key = null)
    {
        using var response = await Client().SendAsync(
            ComplianceTestUsers.Post($"/api/v1/learning/lessons/{lessonId}/quiz-attempts", body, key)
                .Auth(token),
            TestContext.Current.CancellationToken);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, raw);
        return JsonDocument.Parse(raw).RootElement.GetProperty("data");
    }

    private async Task<int> CountAttemptsAsync(Guid userId, string lessonId)
    {
        var rows = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            $"SELECT COUNT(*) AS c FROM learning.quiz_attempts WHERE user_id = '{userId}' AND lesson_id = '{lessonId}'",
            TestContext.Current.CancellationToken);
        return Convert.ToInt32(rows[0]["c"]);
    }

    [Fact]
    public async Task Fail_Then_Pass_Sets_Best_Score_And_Completes_Once()
    {
        var token = await CreateUserAsync();
        var userId = await UserIdAsync(token);

        var failed = await AttemptAsync(token, "quiz-lesson", FailingAnswers(), "attempt-key-fail-1");
        failed.GetProperty("score").GetInt32().ShouldBe(0);
        failed.GetProperty("passed").GetBoolean().ShouldBeFalse();
        failed.GetProperty("lessonComplete").GetBoolean().ShouldBeFalse();
        failed.GetProperty("questions")[0].GetProperty("explanation").GetString().ShouldNotBeNullOrEmpty();

        var passed = await AttemptAsync(token, "quiz-lesson", PassingAnswers(), "attempt-key-pass-1");
        passed.GetProperty("score").GetInt32().ShouldBe(100);
        passed.GetProperty("passed").GetBoolean().ShouldBeTrue();
        passed.GetProperty("lessonComplete").GetBoolean().ShouldBeTrue();
        passed.GetProperty("attemptsCount").GetInt32().ShouldBe(2);

        await AttemptAsync(token, "quiz-lesson", PassingAnswers(), "attempt-key-pass-2");

        var progress = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            $"SELECT status, attempts_count, best_score, completed_version FROM learning.lesson_progress WHERE user_id = '{userId}' AND lesson_id = 'quiz-lesson'",
            TestContext.Current.CancellationToken);
        progress.Count.ShouldBe(1);
        progress[0]["status"].ShouldBe("completed");
        Convert.ToInt32(progress[0]["attempts_count"]).ShouldBe(3);
        Convert.ToInt32(progress[0]["best_score"]).ShouldBe(100);
        Convert.ToInt32(progress[0]["completed_version"]).ShouldBe(1);
        (await CountAttemptsAsync(userId, "quiz-lesson")).ShouldBe(3);
    }

    [Fact]
    public async Task Replay_Returns_Identical_Response_And_Stores_Nothing()
    {
        var token = await CreateUserAsync();
        var userId = await UserIdAsync(token);
        var body = PassingAnswers();

        var first = await AttemptAsync(token, "quiz-lesson", body, "attempt-key-replay");
        var replay = await AttemptAsync(token, "quiz-lesson", body, "attempt-key-replay");
        replay.GetRawText().ShouldBe(first.GetRawText());
        (await CountAttemptsAsync(userId, "quiz-lesson")).ShouldBe(1);

        using var conflict = await Client().SendAsync(
            ComplianceTestUsers.Post(
                    "/api/v1/learning/lessons/quiz-lesson/quiz-attempts", FailingAnswers(), "attempt-key-replay")
                .Auth(token),
            TestContext.Current.CancellationToken);
        conflict.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var envelope = await conflict.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        envelope.GetProperty("error").GetProperty("code").GetString().ShouldBe("idempotency.key_reused");
    }

    [Fact]
    public async Task Invalid_Answers_Return_Validation_Failed()
    {
        var token = await CreateUserAsync();

        var missing = new { answers = new[] { new { questionId = "q1", optionId = "b" } } };
        await AssertValidationAsync(token, "quiz-lesson", missing, "attempt-key-miss");

        var duplicate = new
        {
            answers = new[]
            {
                new { questionId = "q1", optionId = "b" },
                new { questionId = "q1", optionId = "a" },
                new { questionId = "q2", optionId = "b" },
                new { questionId = "q3", optionId = "y" },
            },
        };
        await AssertValidationAsync(token, "quiz-lesson", duplicate, "attempt-key-dup");

        var unknownQuestion = new
        {
            answers = new[]
            {
                new { questionId = "q1", optionId = "b" },
                new { questionId = "q2", optionId = "b" },
                new { questionId = "q9", optionId = "b" },
            },
        };
        await AssertValidationAsync(token, "quiz-lesson", unknownQuestion, "attempt-key-unknown-q");

        var unknownOption = new
        {
            answers = new[]
            {
                new { questionId = "q1", optionId = "z" },
                new { questionId = "q2", optionId = "a" },
                new { questionId = "q3", optionId = "y" },
            },
        };
        await AssertValidationAsync(token, "quiz-lesson", unknownOption, "attempt-key-unknown-o");

        var noQuiz = new { answers = new[] { new { questionId = "q1", optionId = "a" } } };
        await AssertValidationAsync(token, "plain-lesson", noQuiz, "attempt-key-noquiz");
    }

    private async Task AssertValidationAsync(string token, string lessonId, object body, string key)
    {
        using var response = await Client().SendAsync(
            ComplianceTestUsers.Post($"/api/v1/learning/lessons/{lessonId}/quiz-attempts", body, key)
                .Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        envelope.GetProperty("error").GetProperty("code").GetString().ShouldBe("validation.failed");
    }

    [Fact]
    public async Task Attempt_Requires_Idempotency_Key()
    {
        var token = await CreateUserAsync();
        using var response = await Client().SendAsync(
            new HttpRequestMessage(
                HttpMethod.Post,
                new Uri("/api/v1/learning/lessons/quiz-lesson/quiz-attempts", UriKind.Relative))
            {
                Content = JsonContent.Create(PassingAnswers()),
                Headers = { Authorization = new("Bearer", token) },
            },
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Complete_No_Quiz_Works_Twice_And_Quiz_Lesson_Is_Rejected()
    {
        var token = await CreateUserAsync();
        var userId = await UserIdAsync(token);

        var first = await CompleteAsync(token, "plain-lesson", "complete-key-first");
        first.GetProperty("status").GetString().ShouldBe("completed");
        first.GetProperty("completedVersion").GetInt32().ShouldBe(1);

        var second = await CompleteAsync(token, "plain-lesson", "complete-key-second");
        second.GetProperty("status").GetString().ShouldBe("completed");

        using var quizComplete = await Client().SendAsync(
            ComplianceTestUsers.Post("/api/v1/learning/lessons/quiz-lesson/complete", new { }, "complete-key-quiz")
                .Auth(token),
            TestContext.Current.CancellationToken);
        quizComplete.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var envelope = await quizComplete.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        envelope.GetProperty("error").GetProperty("code").GetString().ShouldBe("learning.quiz_required");

        var days = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            $"SELECT COUNT(*) AS c FROM learning.learning_days WHERE user_id = '{userId}'",
            TestContext.Current.CancellationToken);
        Convert.ToInt32(days[0]["c"]).ShouldBe(1);
    }

    private async Task<JsonElement> CompleteAsync(string token, string lessonId, string key)
    {
        using var response = await Client().SendAsync(
            ComplianceTestUsers.Post($"/api/v1/learning/lessons/{lessonId}/complete", new { }, key)
                .Auth(token),
            TestContext.Current.CancellationToken);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, raw);
        return JsonDocument.Parse(raw).RootElement.GetProperty("data");
    }

    [Fact]
    public async Task Twenty_Parallel_Attempts_Record_Exactly()
    {
        var token = await CreateUserAsync();
        var userId = await UserIdAsync(token);

        var tasks = Enumerable.Range(0, 20).Select(index =>
        {
            var body = index % 2 == 0 ? PassingAnswers() : FailingAnswers();
            return AttemptAsync(token, "quiz-lesson", body, $"parallel-attempt-{index:D2}");
        });
        var results = await Task.WhenAll(tasks);
        results.Select(result => result.GetProperty("attemptsCount").GetInt32())
            .OrderBy(count => count)
            .ShouldBe(Enumerable.Range(1, 20));

        var progress = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            $"SELECT attempts_count, best_score, status FROM learning.lesson_progress WHERE user_id = '{userId}' AND lesson_id = 'quiz-lesson'",
            TestContext.Current.CancellationToken);
        progress.Count.ShouldBe(1);
        Convert.ToInt32(progress[0]["attempts_count"]).ShouldBe(20);
        Convert.ToInt32(progress[0]["best_score"]).ShouldBe(100);
        progress[0]["status"].ShouldBe("completed");
        (await CountAttemptsAsync(userId, "quiz-lesson")).ShouldBe(20);

        var days = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            $"SELECT COUNT(*) AS c FROM learning.learning_days WHERE user_id = '{userId}'",
            TestContext.Current.CancellationToken);
        Convert.ToInt32(days[0]["c"]).ShouldBe(1);
    }

    [Fact]
    public async Task Twenty_Parallel_Completes_Create_One_Day_Row()
    {
        var token = await CreateUserAsync();
        var userId = await UserIdAsync(token);

        var tasks = Enumerable.Range(0, 20).Select(index =>
            CompleteAsync(token, "plain-lesson", $"parallel-complete-{index:D2}"));
        await Task.WhenAll(tasks);

        var days = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            $"SELECT COUNT(*) AS c FROM learning.learning_days WHERE user_id = '{userId}'",
            TestContext.Current.CancellationToken);
        Convert.ToInt32(days[0]["c"]).ShouldBe(1);
    }

    [Fact]
    public async Task Streak_Counts_Consecutive_Utc_Days_And_Resets_After_A_Gap()
    {
        var token = await CreateUserAsync();
        var userId = await UserIdAsync(token);
        var today = DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime);

        await AttemptAsync(token, "quiz-lesson", PassingAnswers(), "streak-attempt-key");
        await InsertLearningDaysAsync(
            userId, today.AddDays(-2), today.AddDays(-1));
        var streak = await StreakAsync(token);
        streak.Current.ShouldBe(3);
        streak.Longest.ShouldBe(3);
        streak.TodayCounted.ShouldBeTrue();

        var gappedToken = await CreateUserAsync();
        var gappedUserId = await UserIdAsync(gappedToken);
        await AttemptAsync(gappedToken, "quiz-lesson", PassingAnswers(), "streak-gap-attempt-key");
        await InsertLearningDaysAsync(gappedUserId, today.AddDays(-5), today.AddDays(-4));
        var gapped = await StreakAsync(gappedToken);
        gapped.Current.ShouldBe(1);
        gapped.Longest.ShouldBe(2);
        gapped.TodayCounted.ShouldBeTrue();
    }

    private async Task InsertLearningDaysAsync(Guid userId, params DateOnly[] days)
    {
        foreach (var day in days)
        {
            await Database.ExecuteAsync(
                fixture.OwnerConnectionString,
                $"INSERT INTO learning.learning_days (user_id, day, first_activity_at) VALUES ('{userId}', '{day:yyyy-MM-dd}', now())",
                TestContext.Current.CancellationToken);
        }
    }

    private async Task<StreakView> StreakAsync(string token)
    {
        using var response = await Client().SendAsync(
            ComplianceTestUsers.Get("/api/v1/learning/progress").Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var data = await DataAsync(response);
        var streak = data.GetProperty("streak");
        return new StreakView(
            streak.GetProperty("current").GetInt32(),
            streak.GetProperty("longest").GetInt32(),
            streak.GetProperty("todayCounted").GetBoolean());
    }

    private sealed record StreakView(int Current, int Longest, bool TodayCounted);

    [Fact]
    public async Task Progress_Shows_Tracks_Streak_And_Required_Lessons()
    {
        var token = await CreateUserAsync();
        await AttemptAsync(token, "quiz-lesson", PassingAnswers(), "progress-attempt-key");
        await CompleteAsync(token, "plain-lesson", "progress-complete-key");

        using var response = await Client().SendAsync(
            ComplianceTestUsers.Get("/api/v1/learning/progress").Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var data = await DataAsync(response);

        var track = data.GetProperty("tracks")[0];
        track.GetProperty("totalLessons").GetInt32().ShouldBe(3);
        track.GetProperty("completedLessons").GetInt32().ShouldBe(2);

        var streak = data.GetProperty("streak");
        streak.GetProperty("current").GetInt32().ShouldBe(1);
        streak.GetProperty("todayCounted").GetBoolean().ShouldBeTrue();

        var required = data.GetProperty("requiredLessons")[0];
        required.GetProperty("id").GetString().ShouldBe("required-lesson");
        required.GetProperty("status").GetString().ShouldBe("not_started");
        required.GetProperty("version").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Required_Lesson_Completed_At_Old_Version_Does_Not_Count()
    {
        // Service-level check with two content versions sharing one database.
        var directoryV1 = Path.Combine(Path.GetTempPath(), $"learning-v1-{Guid.CreateVersion7():N}");
        var directoryV2 = Path.Combine(Path.GetTempPath(), $"learning-v2-{Guid.CreateVersion7():N}");
        try
        {
            LearningTestContent.WriteValidContent(directoryV1);
            var v2 = LearningTestContent.ValidJson;
            var marker = "\"id\": \"required-lesson\"";
            var index = v2.LastIndexOf(marker, StringComparison.Ordinal);
            index.ShouldBeGreaterThanOrEqualTo(0);
            var versionIndex = v2.IndexOf("\"version\": 1", index, StringComparison.Ordinal);
            v2 = v2.Remove(versionIndex, "\"version\": 1".Length)
                .Insert(versionIndex, "\"version\": 2");
            LearningTestContent.WriteContent(directoryV2, v2);

            await using var providerV1 = LearningTestServices.CreateProvider(
                fixture, TimeProvider.System, directoryV1);
            await using var providerV2 = LearningTestServices.CreateProvider(
                fixture, TimeProvider.System, directoryV2);

            var userId = Guid.CreateVersion7();
            await using (var scope = providerV1.CreateAsyncScope())
            {
                var service = scope.ServiceProvider.GetRequiredService<LearningCommandService>();
                var result = await service.SubmitQuizAttemptAsync(
                    userId,
                    "required-lesson",
                    [new QuizAnswerRequest("q1", "a")],
                    "version-attempt-key",
                    TestContext.Current.CancellationToken);
                result.IsSuccess.ShouldBeTrue(result.Error?.Message);
            }

            await using (var scope = providerV2.CreateAsyncScope())
            {
                var progress = scope.ServiceProvider.GetRequiredService<LearningProgressService>();
                var view = await progress.GetAsync(userId, TestContext.Current.CancellationToken);
                var required = view.RequiredLessons.ShouldHaveSingleItem();
                required.Version.ShouldBe(2);
                required.CompletedVersion.ShouldBe(1);
                required.Status.ShouldBe("completed");
            }
        }
        finally
        {
            Directory.Delete(directoryV1, recursive: true);
            Directory.Delete(directoryV2, recursive: true);
        }
    }

    private async Task<Guid> UserIdAsync(string token)
    {
        using var response = await Client().SendAsync(
            ComplianceTestUsers.Get("/api/v1/account/me").Auth(token),
            TestContext.Current.CancellationToken);
        var data = await DataAsync(response);
        return data.GetProperty("id").GetGuid();
    }
}
