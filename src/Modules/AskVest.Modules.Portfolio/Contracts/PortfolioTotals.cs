namespace AskVest.Modules.Portfolio.Contracts;

public sealed record PortfolioTotals(
    decimal Cash,
    decimal HoldingsValue,
    decimal CostBasis,
    decimal Unrealized,
    decimal Realized,
    decimal Income,
    decimal TotalValue);
