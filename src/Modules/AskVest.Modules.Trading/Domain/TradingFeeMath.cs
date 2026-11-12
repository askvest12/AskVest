namespace AskVest.Modules.Trading.Domain;

/// <summary>
/// AskVest fee math. The fee is a configurable basis-point charge on the gross
/// amount, rounded half away from zero to the settlement asset scale (6, the
/// ledger USD scale). The gateway fee is taken verbatim from the quote.
/// </summary>
public static class TradingFeeMath
{
    public const int MoneyScale = 6;

    public static decimal RoundMoney(decimal value)
        => decimal.Round(value, MoneyScale, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Buy: the user pays gross plus our fee plus the gateway fee. The quote
    /// total is the full cash out.
    /// </summary>
    public static decimal BuyTotal(decimal grossAmount, decimal ourFee, decimal gatewayFee)
        => RoundMoney(grossAmount + ourFee + gatewayFee);

    /// <summary>
    /// Sell: the user receives gross minus our fee minus the gateway fee. The
    /// quote total is the net proceeds.
    /// </summary>
    public static decimal SellTotal(decimal grossAmount, decimal ourFee, decimal gatewayFee)
        => RoundMoney(grossAmount - ourFee - gatewayFee);

    public static decimal OurFee(decimal grossAmount, int feeBasisPoints)
        => RoundMoney(grossAmount * feeBasisPoints / 10000m);
}
