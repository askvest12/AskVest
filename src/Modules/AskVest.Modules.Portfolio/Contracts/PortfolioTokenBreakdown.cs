namespace AskVest.Modules.Portfolio.Contracts;

public sealed record PortfolioTokenBreakdown(
    string AssetCode,
    string? ChainId,
    string? Symbol,
    decimal QuantityAvailable,
    decimal QuantityHeld,
    decimal CostBasis);
