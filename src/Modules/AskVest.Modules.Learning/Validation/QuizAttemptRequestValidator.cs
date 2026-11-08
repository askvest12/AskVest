using AskVest.Modules.Learning.Endpoints;
using FluentValidation;

namespace AskVest.Modules.Learning.Validation;

internal sealed class QuizAttemptRequestValidator : AbstractValidator<QuizAttemptRequest>
{
    public QuizAttemptRequestValidator()
    {
        RuleFor(request => request.Answers).NotNull();
        RuleForEach(request => request.Answers!)
            .ChildRules(answer =>
            {
                answer.RuleFor(item => item.QuestionId).NotEmpty().MaximumLength(64);
                answer.RuleFor(item => item.OptionId).NotEmpty().MaximumLength(64);
            });
    }
}
