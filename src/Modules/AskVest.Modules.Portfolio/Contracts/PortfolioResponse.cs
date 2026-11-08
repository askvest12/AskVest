using AskVest.Modules.Ledger.Contracts;

namespace AskVest.Modules.Portfolio.Contracts;

public sealed record PortfolioResponse(
    Book Book,
    DateTimeOffset AsOf,
    string ValuationCurrency,
    bool Partial,
    IReadOnlyList<PortfolioCashLine> Cash,
    IReadOnlyList<PortfolioHolding> Holdings,
    PortfolioTotals Totals);
