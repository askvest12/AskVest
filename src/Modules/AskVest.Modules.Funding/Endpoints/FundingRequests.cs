namespace AskVest.Modules.Funding.Endpoints;

public sealed record CreateFundingAccountRequest(string? Currency);

public sealed record CreateConversionQuoteRequest(string? Pair, string? SourceAmount);

public sealed record ExecuteConversionRequest(Guid? QuoteId);

public sealed record AddBeneficiaryRequest(
    string? Currency,
    IReadOnlyDictionary<string, string>? Details,
    string? StepUpToken);

public sealed record CreateWithdrawalPreviewRequest(
    string? Currency,
    string? ReceiveAmount,
    Guid? BeneficiaryId);

public sealed record PlaceWithdrawalRequest(Guid? PreviewId, string? StepUpToken);

public sealed record FundingScenarioRequest(string? Target, string? Scenario);
