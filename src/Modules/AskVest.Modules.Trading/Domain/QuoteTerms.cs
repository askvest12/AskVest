namespace AskVest.Modules.Trading.Domain;

/// <summary>
/// The quote terms a fill must equal. Mirrors the stored quote columns so the
/// comparison works for both quotes and orders. The gateway never sees our
/// fee, so the comparison covers the gateway-side terms only.
/// </summary>
public sealed record QuoteTerms(
    OrderSide Side,
    decimal Quantity,
    decimal Price,
    decimal GrossAmount,
    decimal OurFee,
    decimal GatewayFee,
    decimal Total);
