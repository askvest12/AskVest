namespace AskVest.Modules.Practice.Services;

internal sealed record PracticeOrderItem(
    Guid Id,
    string InstrumentId,
    string Side,
    string Type,
    string State,
    decimal? RequestedAmount,
    decimal? RequestedQuantity,
    bool SellAll,
    decimal? StopPrice,
    decimal? FilledQuantity,
    decimal? FillPrice,
    decimal? CashAmount,
    decimal? Fee,
    DateTimeOffset? PriceAsOf,
    string? PriceLabel,
    string? PriceSession,
    string? CancelReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FilledAt,
    DateTimeOffset? CancelledAt);
