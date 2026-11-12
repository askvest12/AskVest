using AskVest.Modules.Trading.Endpoints;
using FluentValidation;

namespace AskVest.Modules.Trading.Validation;

public sealed class OrderRequestValidator : AbstractValidator<OrderRequest>
{
    public OrderRequestValidator()
    {
        RuleFor(request => request.QuoteId).NotNull();
        RuleFor(request => request.StepUpToken).NotEmpty();
    }
}
