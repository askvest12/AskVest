namespace AskVest.Modules.Catalog.Contracts;

/// <summary>
/// Whether the caller can buy and sell a token right now, with the stable
/// blocker codes explaining each denied action.
/// </summary>
public sealed record TokenAvailability(
    bool Buyable,
    bool Sellable,
    IReadOnlyList<string> BuyBlockers,
    IReadOnlyList<string> SellBlockers);
