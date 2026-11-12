using System.Globalization;
using AskVest.Modules.Trading.Endpoints;
using FluentValidation;

namespace AskVest.Modules.Trading.Validation;

internal sealed class QuoteRequestValidator : AbstractValidator<QuoteRequest>
{
    public QuoteRequestValidator()
    {
        RuleFor(request => request.TokenId).NotNull();
        RuleFor(request => request.Side)
            .Must(side => side is "buy" or "sell")
            .WithMessage("side must be buy or sell");
        RuleFor(request => request.Amount)
            .Must(BeDecimal)
            .WithMessage("amount must be a decimal string");
        RuleFor(request => request.Quantity)
            .Must(BeDecimal)
            .WithMessage("quantity must be a decimal string");
    }

    private static bool BeDecimal(string? value)
        => value is null
            || decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _);
}
