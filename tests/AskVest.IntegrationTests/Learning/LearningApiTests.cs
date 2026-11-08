using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AskVest.IntegrationTests.Compliance;
using AskVest.IntegrationTests.Identity;
using AskVest.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Learning;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class LearningApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private AuthApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        Environment.SetEnvironmentVariable(
            "Learning__ContentPath", fixture.LearningContentDirectory);
        LearningTestContent.WriteValidContent(fixture.LearningContentDirectory);
        (await File.ReadAllTextAsync(
                Path.Combine(fixture.LearningContentDirectory, "content.json"),
                TestContext.Current.CancellationToken))
            .ShouldContain("quiz-lesson");
        _factory = new AuthApiFactory(
            fixture.AppConnectionString, ComplianceTestUsers.Settings());
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        LearningTestContent.WriteContent(fixture.LearningContentDirectory, """{"tracks":[]}""");
    }

    private HttpClient Client() => _factory.CreateClient();

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
        => await ComplianceTestUsers.DataAsync(response);

    [Fact]
    public async Task Tracks_Require_Authentication()
    {
        using var response = await Client().SendAsync(
            ComplianceTestUsers.Get("/api/v1/learning/tracks"),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tracks_Return_Lessons_In_Order_With_Caller_Status()
    {
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        using var response = await Client().SendAsync(
            ComplianceTestUsers.Get("/api/v1/learning/tracks").Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var data = await DataAsync(response);
        var track = data.GetProperty("tracks")[0];
        track.GetProperty("id").GetString().ShouldBe("test-track");
        var lessons = track.GetProperty("lessons").EnumerateArray().ToList();
        lessons.Select(lesson => lesson.GetProperty("id").GetString())
            .ShouldBe(["quiz-lesson", "plain-lesson", "required-lesson"]);
        lessons.ShouldAllBe(lesson =>
            lesson.GetProperty("status").GetString() == "not_started");
        lessons[2].GetProperty("requiredForReal").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Lesson_Returns_Blocks_And_Questions_Without_Answers_Or_Explanations()
    {
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        var raw = await (await Client().SendAsync(
                ComplianceTestUsers.Get("/api/v1/learning/lessons/quiz-lesson").Auth(token),
                TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(raw);
        var lesson = document.RootElement.GetProperty("data").GetProperty("lesson");
        lesson.GetProperty("blocks")[0].GetProperty("text").GetString().ShouldBe("Body");
        var quiz = lesson.GetProperty("quiz");
        quiz.GetProperty("passMarkPercent").GetInt32().ShouldBe(67);
        var question = quiz.GetProperty("questions")[0];
        question.GetProperty("options").EnumerateArray()
            .Select(option => option.GetProperty("id").GetString())
            .ShouldBe(["a", "b", "c"]);
        lesson.GetProperty("progress").GetProperty("status").GetString().ShouldBe("not_started");

        raw.ShouldNotContain("correctOptionId");
        raw.ShouldNotContain("explanation");
        raw.ShouldNotContain("Second is right");
    }

    [Fact]
    public async Task Lesson_Unknown_Id_Returns_NotFound()
    {
        var token = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        using var response = await Client().SendAsync(
            ComplianceTestUsers.Get("/api/v1/learning/lessons/no-such-lesson").Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        envelope.GetProperty("error").GetProperty("code").GetString().ShouldBe("resource.not_found");
    }

    [Fact]
    public async Task Progress_Is_Filtered_Per_User()
    {
        var tokenA = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        var tokenB = await ComplianceTestUsers.CreateVerifiedUserAsync(_factory);
        var userA = await UserIdAsync(tokenA);
        await UserIdAsync(tokenB);

        await Database.ExecuteAsync(
            fixture.OwnerConnectionString,
            $"INSERT INTO learning.lesson_progress (user_id, lesson_id, status, attempts_count, updated_at) VALUES ('{userA}', 'quiz-lesson', 'attempted', 2, now())",
            TestContext.Current.CancellationToken);

        var statusA = await LessonStatusAsync(tokenA);
        statusA.ShouldBe("attempted");
        var statusB = await LessonStatusAsync(tokenB);
        statusB.ShouldBe("not_started");
    }

    private async Task<Guid> UserIdAsync(string token)
    {
        using var response = await Client().SendAsync(
            ComplianceTestUsers.Get("/api/v1/account/me").Auth(token),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var data = await DataAsync(response);
        return data.GetProperty("id").GetGuid();
    }

    private async Task<string> LessonStatusAsync(string token)
    {
        using var response = await Client().SendAsync(
            ComplianceTestUsers.Get("/api/v1/learning/tracks").Auth(token),
            TestContext.Current.CancellationToken);
        var data = await DataAsync(response);
        return data.GetProperty("tracks")[0].GetProperty("lessons")[0]
            .GetProperty("status").GetString()!;
    }
}
