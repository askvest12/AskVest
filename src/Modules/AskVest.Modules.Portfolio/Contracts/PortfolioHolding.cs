namespace AskVest.Modules.Portfolio.Contracts;

public sealed record PortfolioHolding(
    string InstrumentId,
    string Symbol,
    string Name,
    decimal Quantity,
    decimal CostBasis,
    decimal? AverageCostPerUnit,
    decimal? LatestPrice,
    string? PriceCurrency,
    string? PriceLabel,
    string? Session,
    bool UsableForExecution,
    decimal? MarketValue,
    decimal? UnrealizedProfit,
    decimal? UnrealizedProfitPercent,
    decimal RealizedProfit,
    decimal Income,
    IReadOnlyList<string> Flags,
    IReadOnlyList<PortfolioTokenBreakdown> Tokens);
