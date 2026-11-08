using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace AskVest.SmokeTests;

/// <summary>
/// Completes every requiredForReal lesson so Real eligibility can open. The
/// API never exposes correct options, so answers come from the reviewed
/// sample content file in the repository, which the smoke stack serves.
/// </summary>
internal static class SmokeLearning
{
    public static async Task CompleteRequiredLessonsAsync(
        HttpClient client, string token, CancellationToken cancellationToken)
    {
        foreach (var (lessonId, answers) in RequiredLessons())
        {
            var progress = await LessonProgressAsync(client, token, lessonId, cancellationToken);
            if (progress == "completed")
            {
                continue;
            }

            await SubmitAttemptAsync(
                client, token, lessonId,
                new { answers },
                cancellationToken);
        }
    }

    public static async Task<string> LessonProgressAsync(
        HttpClient client, string token, string lessonId, CancellationToken cancellationToken)
    {
        using var detailRequest = Request(HttpMethod.Get, $"/api/v1/learning/lessons/{lessonId}", token);
        using var detailResponse = await client.SendAsync(detailRequest, cancellationToken);
        detailResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return detail.GetProperty("data").GetProperty("lesson").GetProperty("progress")
            .GetProperty("status").GetString()!;
    }

    public static async Task<JsonElement> SubmitAttemptAsync(
        HttpClient client,
        string token,
        string lessonId,
        object body,
        CancellationToken cancellationToken,
        string? idempotencyKey = null)
    {
        using var attemptRequest = Request(
            HttpMethod.Post,
            $"/api/v1/learning/lessons/{lessonId}/quiz-attempts",
            token,
            body,
            idempotencyKey);
        using var attemptResponse = await client.SendAsync(attemptRequest, cancellationToken);
        attemptResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var attempt = await attemptResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return attempt;
    }

    public static IReadOnlyList<(string QuestionId, string OptionId)> CorrectAnswers(string lessonId)
        => AnswersByLesson()[lessonId];

    public static IReadOnlyList<(string QuestionId, string OptionId)> WrongAnswers(string lessonId)
    {
        var options = OptionsByLesson()[lessonId];
        return CorrectAnswers(lessonId)
            .Select(answer =>
            {
                var wrong = options[answer.QuestionId].First(option => option != answer.OptionId);
                return (answer.QuestionId, wrong);
            })
            .ToList();
    }

    private static IEnumerable<(string LessonId, object[] Answers)> RequiredLessons()
    {
        using var document = SampleContent();
        foreach (var track in document.RootElement.GetProperty("tracks").EnumerateArray())
        {
            foreach (var lesson in track.GetProperty("lessons").EnumerateArray())
            {
                if (!lesson.GetProperty("requiredForReal").GetBoolean())
                {
                    continue;
                }

                var quiz = lesson.GetProperty("quiz");
                if (quiz.ValueKind == JsonValueKind.Null)
                {
                    yield return (lesson.GetProperty("id").GetString()!, []);
                    continue;
                }

                var answers = quiz.GetProperty("questions").EnumerateArray()
                    .Select(question => (object)new
                    {
                        questionId = question.GetProperty("id").GetString(),
                        optionId = question.GetProperty("correctOptionId").GetString(),
                    })
                    .ToArray();
                yield return (lesson.GetProperty("id").GetString()!, answers);
            }
        }
    }

    private static Dictionary<string, (string QuestionId, string OptionId)[]> _answersByLesson = new();
    private static Dictionary<string, Dictionary<string, List<string>>> _optionsByLesson = new();

    private static Dictionary<string, (string QuestionId, string OptionId)[]> AnswersByLesson()
    {
        if (_answersByLesson.Count == 0)
        {
            LoadAnswers();
        }

        return _answersByLesson;
    }

    private static Dictionary<string, Dictionary<string, List<string>>> OptionsByLesson()
    {
        if (_optionsByLesson.Count == 0)
        {
            LoadAnswers();
        }

        return _optionsByLesson;
    }

    private static void LoadAnswers()
    {
        using var document = SampleContent();
        foreach (var track in document.RootElement.GetProperty("tracks").EnumerateArray())
        {
            foreach (var lesson in track.GetProperty("lessons").EnumerateArray())
            {
                var quiz = lesson.GetProperty("quiz");
                if (quiz.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                var id = lesson.GetProperty("id").GetString()!;
                _answersByLesson[id] = quiz.GetProperty("questions").EnumerateArray()
                    .Select(question => (
                        QuestionId: question.GetProperty("id").GetString()!,
                        OptionId: question.GetProperty("correctOptionId").GetString()!))
                    .ToArray();
                _optionsByLesson[id] = quiz.GetProperty("questions").EnumerateArray()
                    .ToDictionary(
                        question => question.GetProperty("id").GetString()!,
                        question => question.GetProperty("options").EnumerateArray()
                            .Select(option => option.GetProperty("id").GetString()!)
                            .ToList());
            }
        }
    }

    private static JsonDocument SampleContent()
        => JsonDocument.Parse(File.ReadAllText(SampleContentPath()));

    private static string SampleContentPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "data", "learning-sample", "content.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("data/learning-sample/content.json not found above the test binary.");
    }

    private static HttpRequestMessage Request(
        HttpMethod method, string path, string? token = null, object? body = null, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey ?? $"smoke-learn-{Guid.CreateVersion7():N}");
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }
}
