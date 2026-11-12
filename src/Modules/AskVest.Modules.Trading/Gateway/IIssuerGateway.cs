using AskVest.Modules.Trading.Domain;
using AskVest.SharedKernel;

namespace AskVest.Modules.Trading.Gateway;

/// <summary>
/// The only provider seam in the module. One issuer gateway, selected by the
/// catalog token's gateway key through configured routes. No chain-specific
/// types, no address parsing, no vendor SDK types: tokens travel as catalog
/// references and settlement references stay opaque.
/// </summary>
public interface IIssuerGateway
{
    /// <summary>
    /// Requests a firm quote. The gateway returns a price, quantity, fee
    /// breakdown, settlement asset, expiry and its own quote id. Any failure
    /// means no quote exists and nothing is stored.
    /// </summary>
    Task<Result<GatewayQuote>> GetFirmQuoteAsync(
        GatewayQuoteRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits an order for execution. <paramref name="request"/> carries our
    /// order id as the idempotency key so a repeated delivery cannot create a
    /// second order at the gateway. An unknown outcome is reported as
    /// <see cref="GatewaySubmitKind.Unknown"/>, never guessed.
    /// </summary>
    Task<GatewaySubmitResult> SubmitOrderAsync(
        GatewaySubmitRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Polls the status of a previously submitted order by our key. "Not
    /// found" is not definitive: the order may still arrive, so callers keep
    /// funds held.
    /// </summary>
    Task<GatewayOrderStatus> GetOrderStatusAsync(
        string ourReference,
        CancellationToken cancellationToken = default);
}
