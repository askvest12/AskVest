using System.Globalization;

namespace AskVest.Modules.Trading.Endpoints;

public sealed record QuoteRequest(
    Guid? TokenId,
    string? Side,
    string? Amount,
    string? Quantity,
    bool? SellAll);
