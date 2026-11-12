namespace AskVest.Modules.Trading.Domain;

/// <summary>
/// Exact fill-versus-quote comparison. A fill settles only when every
/// gateway-visible term equals the quote exactly; any difference is a mismatch
/// for reconciliation, never a partial settlement. The gateway fill total is
/// the settlement total it moved: gross plus gateway fee on a buy, gross minus
/// gateway fee on a sell.
/// </summary>
public static class FillComparison
{
    public static bool Matches(GatewayFillTerms fill, QuoteTerms terms)
        => fill.Quantity == terms.Quantity
            && fill.Price == terms.Price
            && fill.GrossAmount == terms.GrossAmount
            && fill.GatewayFee == terms.GatewayFee
            && fill.Total == GatewayTotal(terms);

    public static string DescribeMismatch(GatewayFillTerms fill, QuoteTerms terms)
    {
        var fields = new List<string>();
        if (fill.Quantity != terms.Quantity)
        {
            fields.Add("quantity");
        }

        if (fill.Price != terms.Price)
        {
            fields.Add("price");
        }

        if (fill.GrossAmount != terms.GrossAmount)
        {
            fields.Add("gross_amount");
        }

        if (fill.GatewayFee != terms.GatewayFee)
        {
            fields.Add("gateway_fee");
        }

        if (fill.Total != GatewayTotal(terms))
        {
            fields.Add("total");
        }

        return string.Join(',', fields);
    }

    private static decimal GatewayTotal(QuoteTerms terms)
        => terms.Side == OrderSide.Buy
            ? TradingFeeMath.RoundMoney(terms.GrossAmount + terms.GatewayFee)
            : TradingFeeMath.RoundMoney(terms.GrossAmount - terms.GatewayFee);
}
