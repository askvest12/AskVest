using AskVest.Modules.Trading.Domain;

namespace AskVest.Modules.Trading.Persistence.Entities;

public sealed class TradingOrder
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid QuoteId { get; set; }

    public Guid TokenId { get; set; }

    public string InstrumentId { get; set; } = "";

    public OrderSide Side { get; set; }

    public OrderState State { get; set; }

    public string SettlementAsset { get; set; } = "";

    public decimal Quantity { get; set; }

    public decimal Price { get; set; }

    public decimal GrossAmount { get; set; }

    public decimal OurFee { get; set; }

    public decimal GatewayFee { get; set; }

    public decimal Total { get; set; }

    public string IdempotencyKey { get; set; } = "";

    public string GatewayKey { get; set; } = "";

    public string? GatewayOrderReference { get; set; }

    public Guid? HoldJournalId { get; set; }

    public Guid? FillJournalId { get; set; }

    public Guid? ReleaseJournalId { get; set; }

    public string? SettlementChainId { get; set; }

    public string? SettlementReference { get; set; }

    public string? FailureCode { get; set; }

    public string? DetailCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? SubmittedAt { get; set; }

    public DateTimeOffset? ExecutedAt { get; set; }

    public DateTimeOffset? FailedAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public DateTimeOffset? LastStatusCheckAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Xmin { get; set; }
}
