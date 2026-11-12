namespace AskVest.Modules.Trading.Endpoints;

public sealed record OrderRequest(Guid? QuoteId, string? StepUpToken);
