namespace AskVest.Modules.Practice.Services;

/// <summary>
/// Order quantity, cost and fee math. Quantity always rounds toward zero so a
/// buy never spends more than requested; money rounds half away from zero to
/// the USD ledger scale.
/// </summary>
internal static class PracticeOrderMath
{
    public static PracticeBuyComputation ComputeBuy(
        decimal amount,
        decimal price,
        int quantityScale,
        int feeBasisPoints)
    {
        var quantity = PracticeRounding.RoundQuantity(amount / price, quantityScale);
        var cost = PracticeRounding.RoundMoney(quantity * price);
        var fee = PracticeRounding.RoundMoney(cost * feeBasisPoints / 10000m);
        return new PracticeBuyComputation(quantity, cost, fee);
    }

    public static PracticeSellComputation ComputeSell(
        decimal quantity,
        decimal price,
        int feeBasisPoints)
    {
        var proceeds = PracticeRounding.RoundMoney(quantity * price);
        var fee = PracticeRounding.RoundMoney(proceeds * feeBasisPoints / 10000m);
        return new PracticeSellComputation(proceeds, fee);
    }

    internal readonly record struct PracticeBuyComputation(
        decimal Quantity,
        decimal Cost,
        decimal Fee);

    internal readonly record struct PracticeSellComputation(
        decimal Proceeds,
        decimal Fee);
}
