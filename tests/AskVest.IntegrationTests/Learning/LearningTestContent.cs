namespace AskVest.IntegrationTests.Learning;

internal static class LearningTestContent
{
    public const string QuizLessonId = "quiz-lesson";
    public const string PlainLessonId = "plain-lesson";
    public const string RequiredLessonId = "required-lesson";

    public static string WriteValidContent(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "content.json");
        File.WriteAllText(path, ValidJson);
        return path;
    }

    public static string WriteEmptyContent()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"learning-empty-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "content.json"), """{"tracks":[]}""");
        return directory;
    }

    public static void WriteContent(string directory, string json)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "content.json"), json);
    }

    public static string ValidJson { get; } = """
        {
          "tracks": [
            {
              "id": "test-track",
              "title": "Test track",
              "summary": "Track summary",
              "lessons": [
                {
                  "id": "quiz-lesson",
                  "title": "Quiz lesson",
                  "summary": "Has a quiz",
                  "estimatedMinutes": 3,
                  "version": 1,
                  "requiredForReal": false,
                  "blocks": [ { "type": "paragraph", "text": "Body" } ],
                  "quiz": {
                    "passMarkPercent": 67,
                    "questions": [
                      {
                        "id": "q1",
                        "prompt": "Pick one",
                        "options": [
                          { "id": "a", "text": "First" },
                          { "id": "b", "text": "Second" },
                          { "id": "c", "text": "Third" }
                        ],
                        "correctOptionId": "b",
                        "explanation": "Second is right."
                      },
                      {
                        "id": "q2",
                        "prompt": "Pick two",
                        "options": [
                          { "id": "a", "text": "Alpha" },
                          { "id": "b", "text": "Beta" }
                        ],
                        "correctOptionId": "a",
                        "explanation": "Alpha is right."
                      },
                      {
                        "id": "q3",
                        "prompt": "Pick three",
                        "options": [
                          { "id": "x", "text": "X" },
                          { "id": "y", "text": "Y" }
                        ],
                        "correctOptionId": "y",
                        "explanation": "Y is right."
                      }
                    ]
                  }
                },
                {
                  "id": "plain-lesson",
                  "title": "Plain lesson",
                  "summary": "No quiz",
                  "estimatedMinutes": 2,
                  "version": 1,
                  "requiredForReal": false,
                  "blocks": [ { "type": "bullets", "items": ["one", "two"] } ],
                  "quiz": null
                },
                {
                  "id": "required-lesson",
                  "title": "Required lesson",
                  "summary": "Required for real",
                  "estimatedMinutes": 3,
                  "version": 1,
                  "requiredForReal": true,
                  "blocks": [ { "type": "callout", "kind": "warning", "text": "Read carefully" } ],
                  "quiz": {
                    "questions": [
                      {
                        "id": "q1",
                        "prompt": "Only question",
                        "options": [
                          { "id": "a", "text": "Yes" },
                          { "id": "b", "text": "No" }
                        ],
                        "correctOptionId": "a",
                        "explanation": "Yes is right."
                      }
                    ]
                  }
                }
              ]
            }
          ]
        }
        """;
}
