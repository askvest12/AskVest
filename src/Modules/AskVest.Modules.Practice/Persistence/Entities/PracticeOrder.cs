using AskVest.Modules.Practice.Domain;

namespace AskVest.Modules.Practice.Persistence.Entities;

public sealed class PracticeOrder
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string InstrumentId { get; set; } = "";

    public PracticeOrderSide Side { get; set; }

    public PracticeOrderType Type { get; set; }

    public PracticeOrderState State { get; set; }

    public decimal? RequestedAmount { get; set; }

    public decimal? RequestedQuantity { get; set; }

    public bool SellAll { get; set; }

    public decimal? StopPrice { get; set; }

    public decimal? FilledQuantity { get; set; }

    public decimal? FillPrice { get; set; }

    public decimal? CashAmount { get; set; }

    public decimal? Fee { get; set; }

    public DateTimeOffset? PriceAsOf { get; set; }

    public string? PriceLabel { get; set; }

    public string? PriceSession { get; set; }

    public string IdempotencyKey { get; set; } = "";

    public Guid? HoldJournalId { get; set; }

    public Guid? FillJournalId { get; set; }

    public string? CancelReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? FilledAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public uint Xmin { get; set; }
}
