namespace AskVest.Modules.Practice.Contracts;

/// <summary>
/// The cross-module view of a practice order. Amounts are decimals; consumers
/// format for display. AssetCode is the practice ledger asset ("P." prefix).
/// </summary>
public sealed record PracticeOrderSummary(
    Guid OrderId,
    string InstrumentId,
    string? AssetCode,
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
    DateTimeOffset CreatedAt,
    DateTimeOffset? FilledAt,
    DateTimeOffset? CancelledAt);
