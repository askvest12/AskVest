using AskVest.Modules.Learning.Content;
using AskVest.SharedKernel;

namespace AskVest.Modules.Learning.Domain;

internal sealed record QuestionGrade(
    string QuestionId,
    bool Correct,
    string? SelectedOptionId,
    string CorrectOptionId,
    string Explanation);

internal sealed record QuizGrade(
    int Score,
    bool Passed,
    IReadOnlyList<QuestionGrade> Questions);

/// <summary>
/// Pure grading over the immutable snapshot. Answers arrive as a question id
/// to option id map; every question must be answered exactly once with a
/// valid option before any score is computed.
/// </summary>
internal static class QuizGrader
{
    public static Result<QuizGrade> Grade(
        QuizContent quiz,
        IReadOnlyDictionary<string, string> answers)
    {
        var questions = quiz.Questions;
        if (answers.Count != questions.Count)
        {
            return Invalid("Every question must be answered exactly once.");
        }

        var grades = new List<QuestionGrade>(questions.Count);
        var correct = 0;
        foreach (var question in questions)
        {
            if (!answers.TryGetValue(question.Id, out var optionId))
            {
                return Invalid($"Question '{question.Id}' is missing an answer.");
            }

            var option = question.Options.FirstOrDefault(o => o.Id == optionId);
            if (option is null)
            {
                return Invalid($"Question '{question.Id}' has an unknown option.");
            }

            var isCorrect = option.Id == question.CorrectOptionId;
            if (isCorrect)
            {
                correct++;
            }

            grades.Add(new QuestionGrade(
                question.Id, isCorrect, option.Id, question.CorrectOptionId, question.Explanation));
        }

        foreach (var key in answers.Keys)
        {
            if (questions.All(question => question.Id != key))
            {
                return Invalid($"Answer for unknown question '{key}'.");
            }
        }

        var score = (int)Math.Floor(100.0 * correct / questions.Count);
        return Result<QuizGrade>.Success(new QuizGrade(
            score, score >= LearningContentValidator.EffectivePassMark(quiz), grades));
    }

    private static Result<QuizGrade> Invalid(string message)
        => Result.Failure<QuizGrade>(new Error(
            ErrorCodes.ValidationFailed, message, ErrorType.Validation));
}
