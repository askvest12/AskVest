using AskVest.Modules.Trading.Domain;

namespace AskVest.Modules.Trading.Gateway;

/// <summary>
/// The token reference handed to the gateway: catalog identity only. The
/// gateway maps it to whatever venue it serves; the module never parses an
/// address or names a chain family.
/// </summary>
public sealed record GatewayTokenReference(
    Guid TokenId,
    string InstrumentId,
    string ChainId,
    string CanonicalAddress,
    string Caip19,
    string Symbol,
    int Decimals,
    string LedgerAssetCode);

/// <summary>
/// A firm quote request. A buy carries the settlement amount; a sell carries
/// the token quantity (sell-all is resolved to a quantity before the call).
/// </summary>
public sealed record GatewayQuoteRequest(
    GatewayTokenReference Token,
    OrderSide Side,
    decimal? Amount,
    decimal? Quantity,
    string SettlementAsset);

/// <summary>
/// The gateway's firm quote. The gateway quote id is internal only and is
/// never exposed through the API.
/// </summary>
public sealed record GatewayQuote(
    string GatewayQuoteId,
    decimal Price,
    decimal Quantity,
    decimal GrossAmount,
    decimal GatewayFee,
    string SettlementAsset,
    DateTimeOffset ExpiresAt);

/// <summary>
/// An order submission. <paramref name="OurReference"/> is our order id and
/// doubles as the gateway idempotency key.
/// </summary>
public sealed record GatewaySubmitRequest(
    string OurReference,
    string GatewayQuoteId,
    GatewayTokenReference Token,
    OrderSide Side,
    decimal Quantity,
    decimal Price,
    decimal GrossAmount,
    decimal GatewayFee,
    decimal Total,
    string SettlementAsset);

public enum GatewaySubmitKind
{
    Acknowledged,
    Rejected,
    Unknown,
}

public sealed record GatewaySubmitResult(
    GatewaySubmitKind Kind,
    string? GatewayReference,
    string? ReasonCode);

public enum GatewayStatusKind
{
    Pending,
    Filled,
    Rejected,
    NotFound,
    Unavailable,
}

public sealed record GatewayOrderStatus(
    GatewayStatusKind Kind,
    GatewayFillTerms? Fill,
    string? ReasonCode);
