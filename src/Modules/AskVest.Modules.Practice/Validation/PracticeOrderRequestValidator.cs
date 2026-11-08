using AskVest.Modules.Practice.Endpoints;
using FluentValidation;

namespace AskVest.Modules.Practice.Validation;

public sealed class PracticeOrderRequestValidator : AbstractValidator<PracticeOrderRequest>
{
    public PracticeOrderRequestValidator()
    {
        RuleFor(request => request.InstrumentId).NotEmpty().MaximumLength(64);
        RuleFor(request => request.Side).NotEmpty().MaximumLength(8);
        RuleFor(request => request.Type).NotEmpty().MaximumLength(12);
        RuleFor(request => request.Amount).MaximumLength(32);
        RuleFor(request => request.Quantity).MaximumLength(32);
        RuleFor(request => request.StopPrice).MaximumLength(32);
    }
}
