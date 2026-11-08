namespace AskVest.Modules.Portfolio.Contracts;

public sealed record PortfolioCashLine(
    string Asset,
    string Kind,
    decimal Available,
    decimal Held,
    bool IncludedInTotal);
