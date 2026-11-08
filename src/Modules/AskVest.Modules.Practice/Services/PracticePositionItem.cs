namespace AskVest.Modules.Practice.Services;

internal sealed record PracticePositionItem(
    string InstrumentId,
    string Symbol,
    string Name,
    decimal AvailableQuantity,
    decimal HeldQuantity,
    decimal? Price,
    string? PriceLabel,
    decimal? MarketValue);

internal sealed record PracticePositions(
    decimal Cash,
    IReadOnlyList<PracticePositionItem> Items);
