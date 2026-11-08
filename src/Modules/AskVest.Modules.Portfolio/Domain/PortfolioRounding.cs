namespace AskVest.Modules.Portfolio.Domain;

/// <summary>
/// Portfolio math uses exact decimal internally; display figures round half up
/// to the configured decimals. Quantities and prices are never rounded here.
/// </summary>
internal static class PortfolioRounding
{
    public const int CostScale = 6;

    public static decimal RoundCost(decimal value)
        => decimal.Round(value, CostScale, MidpointRounding.AwayFromZero);

    public static decimal RoundDisplay(decimal value, int decimals)
        => decimal.Round(value, decimals, MidpointRounding.AwayFromZero);
}
