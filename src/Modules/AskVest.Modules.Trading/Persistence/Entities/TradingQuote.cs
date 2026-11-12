using AskVest.Modules.Trading.Domain;

namespace AskVest.Modules.Trading.Persistence.Entities;

public sealed class TradingQuote
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid TokenId { get; set; }

    public OrderSide Side { get; set; }

    public decimal Quantity { get; set; }

    public decimal Price { get; set; }

    public decimal GrossAmount { get; set; }

    public decimal OurFee { get; set; }

    public decimal GatewayFee { get; set; }

    public decimal Total { get; set; }

    public string SettlementAsset { get; set; } = "";

    public string GatewayKey { get; set; } = "";

    public string GatewayQuoteId { get; set; } = "";

    /// <summary>Pipe-separated warning codes; empty means no warnings.</summary>
    public string Warnings { get; set; } = "";

    public string TermsHash { get; set; } = "";

    public QuoteState State { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
