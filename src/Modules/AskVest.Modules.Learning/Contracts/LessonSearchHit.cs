namespace AskVest.Modules.Learning.Contracts;

/// <summary>
/// One lesson search result for cross-module consumers such as the
/// assistant. Carries no quiz answers or explanations.
/// </summary>
public sealed record LessonSearchHit(
    string LessonId,
    string Title,
    string Summary,
    string Excerpt);
