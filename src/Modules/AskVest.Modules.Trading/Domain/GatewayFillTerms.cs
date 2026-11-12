namespace AskVest.Modules.Trading.Domain;

/// <summary>
/// The gateway-reported fill terms. Settlement references stay opaque: the
/// gateway decides their format.
/// </summary>
public sealed record GatewayFillTerms(
    decimal Quantity,
    decimal Price,
    decimal GrossAmount,
    decimal GatewayFee,
    decimal Total,
    string? SettlementChainId,
    string? SettlementReference);
