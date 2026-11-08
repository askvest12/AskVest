namespace AskVest.Modules.Practice.Services;

/// <summary>
/// The single rounding authority for practice math. Quantities round toward zero
/// so a buy never spends more than requested; money rounds half away from zero.
/// </summary>
internal static class PracticeRounding
{
    public const int UsdScale = 6;

    public static decimal RoundQuantity(decimal value, int scale)
        => decimal.Round(value, scale, MidpointRounding.ToZero);

    public static decimal RoundMoney(decimal value)
        => decimal.Round(value, UsdScale, MidpointRounding.AwayFromZero);

    public static decimal RoundDisplay(decimal value)
        => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
