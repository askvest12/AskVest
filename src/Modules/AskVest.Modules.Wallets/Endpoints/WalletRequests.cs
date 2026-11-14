namespace AskVest.Modules.Wallets.Endpoints;

internal sealed record PreviewWithdrawalRequest(Guid TokenId, Guid AddressId, string GrossQuantity);

internal sealed record PlaceWithdrawalRequest(Guid PreviewId, string StepUpToken);

internal sealed record AddAddressRequest(string ChainId, string Address, string? Label, string StepUpToken);

internal sealed record CustodyScenarioRequest(string Scenario);

internal sealed record ScreeningScenarioRequest(string Scenario);

internal sealed record DepositInjectionRequest(
    string ChainId,
    string DestinationAddress,
    string TokenContractAddress,
    string Quantity,
    string SenderAddress,
    string Finality);
