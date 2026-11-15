namespace AskVest.Modules.Funding.Endpoints;

public sealed record CreateFundingAccountRequest(string? Currency);

public sealed record FundingScenarioRequest(string? Target, string? Scenario);
