namespace AskVest.Modules.Practice.Endpoints;

/// <summary>
/// Order request body. Money and quantity arrive as strings per the API rules;
/// unknown properties are rejected by the JSON options.
/// </summary>
public sealed record PracticeOrderRequest(
    string? InstrumentId,
    string? Side,
    string? Type,
    string? Amount,
    string? Quantity,
    bool? SellAll,
    string? StopPrice);
