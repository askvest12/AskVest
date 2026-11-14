using FluentValidation;
using AskVest.Modules.Wallets.Endpoints;

namespace AskVest.Modules.Wallets.Validation;

internal sealed class PreviewWithdrawalRequestValidator : AbstractValidator<PreviewWithdrawalRequest>
{
    public PreviewWithdrawalRequestValidator()
    {
        RuleFor(request => request.TokenId).NotEmpty();
        RuleFor(request => request.AddressId).NotEmpty();
        RuleFor(request => request.GrossQuantity)
            .Must(value => decimal.TryParse(value, out var parsed) && parsed > 0m)
            .WithMessage("GrossQuantity must be a positive decimal.");
    }
}

internal sealed class PlaceWithdrawalRequestValidator : AbstractValidator<PlaceWithdrawalRequest>
{
    public PlaceWithdrawalRequestValidator()
    {
        RuleFor(request => request.PreviewId).NotEmpty();
        RuleFor(request => request.StepUpToken).NotEmpty();
    }
}

internal sealed class AddAddressRequestValidator : AbstractValidator<AddAddressRequest>
{
    public AddAddressRequestValidator()
    {
        RuleFor(request => request.ChainId).NotEmpty();
        RuleFor(request => request.Address).NotEmpty();
        RuleFor(request => request.StepUpToken).NotEmpty();
    }
}
