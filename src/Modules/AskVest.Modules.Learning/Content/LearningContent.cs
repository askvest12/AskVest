namespace AskVest.Modules.Learning.Content;

internal sealed record ContentFile(IReadOnlyList<TrackContent> Tracks);

internal sealed record TrackContent(
    string Id,
    string Title,
    string Summary,
    IReadOnlyList<LessonContent> Lessons);

internal sealed record LessonContent(
    string Id,
    string Title,
    string Summary,
    int EstimatedMinutes,
    int Version,
    bool RequiredForReal,
    IReadOnlyList<BlockContent> Blocks,
    QuizContent? Quiz);

internal sealed record BlockContent(
    string Type,
    string? Text,
    IReadOnlyList<string>? Items,
    string? Kind);

internal sealed record QuizContent(
    int? PassMarkPercent,
    IReadOnlyList<QuestionContent> Questions);

internal sealed record QuestionContent(
    string Id,
    string Prompt,
    IReadOnlyList<OptionContent> Options,
    string CorrectOptionId,
    string Explanation);

internal sealed record OptionContent(string Id, string Text);
