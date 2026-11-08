using System.Text.RegularExpressions;
using AskVest.Modules.Learning.Configuration;

namespace AskVest.Modules.Learning.Content;

internal static partial class LearningContentValidator
{
    private const int MaxIdLength = 64;
    private const int MaxTitleLength = 120;
    private const int MaxSummaryLength = 280;
    private const int MaxTextLength = 2000;
    private const int MaxItemLength = 500;
    private const int MaxPromptLength = 500;
    private const int MaxOptionTextLength = 200;
    private const int MaxExplanationLength = 500;
    private const int MaxBlocksPerLesson = 50;
    private const int MaxBulletItems = 20;
    private const int MaxQuestionsPerQuiz = 20;
    private const int MinOptionsPerQuestion = 2;
    private const int MaxOptionsPerQuestion = 5;
    private const int MinEstimatedMinutes = 1;
    private const int MaxEstimatedMinutes = 60;
    private const int MinPassMark = 50;
    private const int MaxPassMark = 100;
    private const int DefaultPassMark = 80;

    private static readonly string[] BlockTypes = ["heading", "paragraph", "bullets", "callout"];
    private static readonly string[] CalloutKinds = ["info", "warning"];

    public static int EffectivePassMark(QuizContent quiz) => quiz.PassMarkPercent ?? DefaultPassMark;

    public static List<string> Validate(ContentFile? content, IReadOnlyList<string> bannedPhrases)
    {
        var problems = new List<string>();
        if (content is null)
        {
            problems.Add("learning content is empty");
            return problems;
        }

        var tracks = content.Tracks ?? [];
        var trackIds = new HashSet<string>(StringComparer.Ordinal);
        var lessonIds = new HashSet<string>(StringComparer.Ordinal);
        for (var trackIndex = 0; trackIndex < tracks.Count; trackIndex++)
        {
            var track = tracks[trackIndex];
            var trackLabel = $"tracks[{trackIndex}]";
            if (track is null)
            {
                problems.Add($"{trackLabel} is null");
                continue;
            }

            trackLabel = $"track '{track.Id ?? "?"}'";
            ValidateId(track.Id, $"{trackLabel} id", problems);
            if (!trackIds.Add(track.Id ?? string.Empty))
            {
                problems.Add($"{trackLabel}: duplicate track id '{track.Id}'");
            }

            ValidateText(track.Title, MaxTitleLength, $"{trackLabel} title", problems);
            ValidateText(track.Summary, MaxSummaryLength, $"{trackLabel} summary", problems);
            Lint(track.Title, $"{trackLabel} title", bannedPhrases, problems);
            Lint(track.Summary, $"{trackLabel} summary", bannedPhrases, problems);

            var lessons = track.Lessons ?? [];
            if (lessons.Count == 0)
            {
                problems.Add($"{trackLabel}: a track must contain at least one lesson");
            }

            for (var lessonIndex = 0; lessonIndex < lessons.Count; lessonIndex++)
            {
                ValidateLesson(lessons[lessonIndex], lessonIndex, trackLabel, lessonIds, bannedPhrases, problems);
            }
        }

        return problems;
    }

    private static void ValidateLesson(
        LessonContent? lesson,
        int lessonIndex,
        string trackLabel,
        HashSet<string> lessonIds,
        IReadOnlyList<string> bannedPhrases,
        List<string> problems)
    {
        var label = $"{trackLabel} lessons[{lessonIndex}]";
        if (lesson is null)
        {
            problems.Add($"{label} is null");
            return;
        }

        label = $"lesson '{lesson.Id ?? "?"}'";
        ValidateId(lesson.Id, $"{label} id", problems);
        if (!lessonIds.Add(lesson.Id ?? string.Empty))
        {
            problems.Add($"{label}: duplicate lesson id '{lesson.Id}'");
        }

        ValidateText(lesson.Title, MaxTitleLength, $"{label} title", problems);
        ValidateText(lesson.Summary, MaxSummaryLength, $"{label} summary", problems);
        Lint(lesson.Title, $"{label} title", bannedPhrases, problems);
        Lint(lesson.Summary, $"{label} summary", bannedPhrases, problems);
        if (lesson.EstimatedMinutes is < MinEstimatedMinutes or > MaxEstimatedMinutes)
        {
            problems.Add($"{label}: estimatedMinutes must be between {MinEstimatedMinutes} and {MaxEstimatedMinutes}");
        }

        if (lesson.Version < 1)
        {
            problems.Add($"{label}: version must be at least 1");
        }

        var blocks = lesson.Blocks ?? [];
        if (blocks.Count == 0)
        {
            problems.Add($"{label}: a lesson needs at least one body block");
        }
        else if (blocks.Count > MaxBlocksPerLesson)
        {
            problems.Add($"{label}: at most {MaxBlocksPerLesson} body blocks are allowed");
        }

        for (var blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
        {
            ValidateBlock(blocks[blockIndex], $"{label} blocks[{blockIndex}]", bannedPhrases, problems);
        }

        if (lesson.Quiz is null)
        {
            if (lesson.RequiredForReal)
            {
                problems.Add($"{label}: a requiredForReal lesson must have a quiz");
            }

            return;
        }

        ValidateQuiz(lesson.Quiz, label, bannedPhrases, problems);
    }

    private static void ValidateBlock(
        BlockContent? block,
        string label,
        IReadOnlyList<string> bannedPhrases,
        List<string> problems)
    {
        if (block is null)
        {
            problems.Add($"{label} is null");
            return;
        }

        if (!BlockTypes.Contains(block.Type, StringComparer.Ordinal))
        {
            problems.Add($"{label}: unknown block type '{block.Type}'");
            return;
        }

        switch (block.Type)
        {
            case "heading":
            case "paragraph":
                ValidateText(block.Text, MaxTextLength, $"{label} text", problems, required: true);
                RejectExtra(block.Items, $"{label} items", problems);
                RejectExtra(block.Kind, $"{label} kind", problems);
                Lint(block.Text, $"{label} text", bannedPhrases, problems);
                break;
            case "bullets":
                RejectExtra(block.Text, $"{label} text", problems);
                RejectExtra(block.Kind, $"{label} kind", problems);
                var items = block.Items ?? [];
                if (items.Count == 0)
                {
                    problems.Add($"{label}: bullets need at least one item");
                }
                else if (items.Count > MaxBulletItems)
                {
                    problems.Add($"{label}: at most {MaxBulletItems} bullet items are allowed");
                }

                for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
                {
                    ValidateText(items[itemIndex], MaxItemLength, $"{label} items[{itemIndex}]", problems, required: true);
                    Lint(items[itemIndex], $"{label} items[{itemIndex}]", bannedPhrases, problems);
                }

                break;
            case "callout":
                RejectExtra(block.Items, $"{label} items", problems);
                if (block.Kind is null || !CalloutKinds.Contains(block.Kind, StringComparer.Ordinal))
                {
                    problems.Add($"{label}: callout kind must be one of {string.Join(", ", CalloutKinds)}");
                }

                ValidateText(block.Text, MaxTextLength, $"{label} text", problems, required: true);
                Lint(block.Text, $"{label} text", bannedPhrases, problems);
                break;
        }
    }

    private static void ValidateQuiz(
        QuizContent quiz,
        string lessonLabel,
        IReadOnlyList<string> bannedPhrases,
        List<string> problems)
    {
        var label = $"{lessonLabel} quiz";
        if (quiz.PassMarkPercent is { } passMark && passMark is < MinPassMark or > MaxPassMark)
        {
            problems.Add($"{label}: passMarkPercent must be between {MinPassMark} and {MaxPassMark}");
        }

        var questions = quiz.Questions ?? [];
        if (questions.Count == 0)
        {
            problems.Add($"{label}: a quiz needs at least one question");
            return;
        }

        if (questions.Count > MaxQuestionsPerQuiz)
        {
            problems.Add($"{label}: at most {MaxQuestionsPerQuiz} questions are allowed");
        }

        var questionIds = new HashSet<string>(StringComparer.Ordinal);
        for (var questionIndex = 0; questionIndex < questions.Count; questionIndex++)
        {
            ValidateQuestion(questions[questionIndex], $"{label} questions[{questionIndex}]", questionIds, bannedPhrases, problems);
        }
    }

    private static void ValidateQuestion(
        QuestionContent? question,
        string label,
        HashSet<string> questionIds,
        IReadOnlyList<string> bannedPhrases,
        List<string> problems)
    {
        if (question is null)
        {
            problems.Add($"{label} is null");
            return;
        }

        ValidateId(question.Id, $"{label} id", problems);
        if (!questionIds.Add(question.Id ?? string.Empty))
        {
            problems.Add($"{label}: duplicate question id '{question.Id}'");
        }

        ValidateText(question.Prompt, MaxPromptLength, $"{label} prompt", problems, required: true);
        Lint(question.Prompt, $"{label} prompt", bannedPhrases, problems);
        ValidateText(question.Explanation, MaxExplanationLength, $"{label} explanation", problems, required: true);
        Lint(question.Explanation, $"{label} explanation", bannedPhrases, problems);

        var options = question.Options ?? [];
        if (options.Count is < MinOptionsPerQuestion or > MaxOptionsPerQuestion)
        {
            problems.Add($"{label}: a question needs between {MinOptionsPerQuestion} and {MaxOptionsPerQuestion} options");
        }

        var optionIds = new HashSet<string>(StringComparer.Ordinal);
        var hasCorrect = false;
        for (var optionIndex = 0; optionIndex < options.Count; optionIndex++)
        {
            var option = options[optionIndex];
            var optionLabel = $"{label} options[{optionIndex}]";
            if (option is null)
            {
                problems.Add($"{optionLabel} is null");
                continue;
            }

            ValidateId(option.Id, $"{optionLabel} id", problems);
            if (!optionIds.Add(option.Id ?? string.Empty))
            {
                problems.Add($"{optionLabel}: duplicate option id '{option.Id}'");
            }

            ValidateText(option.Text, MaxOptionTextLength, $"{optionLabel} text", problems, required: true);
            Lint(option.Text, $"{optionLabel} text", bannedPhrases, problems);
            if (string.Equals(option.Id, question.CorrectOptionId, StringComparison.Ordinal))
            {
                hasCorrect = true;
            }
        }

        if (!hasCorrect)
        {
            problems.Add($"{label}: correctOptionId '{question.CorrectOptionId}' does not name an option");
        }
    }

    private static void ValidateId(string? id, string label, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            problems.Add($"{label}: id is required");
            return;
        }

        if (id.Length > MaxIdLength || !IdPattern().IsMatch(id))
        {
            problems.Add($"{label}: id must be lowercase letters, digits and dashes, at most {MaxIdLength} characters");
        }
    }

    private static void ValidateText(string? text, int maxLength, string label, List<string> problems, bool required = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            if (required)
            {
                problems.Add($"{label}: text is required");
            }

            return;
        }

        if (text.Length > maxLength)
        {
            problems.Add($"{label}: text is longer than {maxLength} characters");
        }
    }

    private static void RejectExtra(string? value, string label, List<string> problems)
    {
        if (value is not null)
        {
            problems.Add($"{label}: not allowed for this block shape");
        }
    }

    private static void RejectExtra(IReadOnlyList<string>? value, string label, List<string> problems)
    {
        if (value is not null)
        {
            problems.Add($"{label}: not allowed for this block shape");
        }
    }

    private static void Lint(string? text, string label, IReadOnlyList<string> bannedPhrases, List<string> problems)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (var phrase in bannedPhrases)
        {
            if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{label}: contains banned phrase '{phrase}'");
            }
        }
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdPattern();
}
