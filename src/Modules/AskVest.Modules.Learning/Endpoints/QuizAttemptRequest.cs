namespace AskVest.Modules.Learning.Endpoints;

/// <summary>
/// Quiz attempt body: one chosen option per question. Grading rules live in
/// the domain; this shape only carries the submission.
/// </summary>
public sealed record QuizAttemptRequest(IReadOnlyList<QuizAnswerRequest>? Answers);
