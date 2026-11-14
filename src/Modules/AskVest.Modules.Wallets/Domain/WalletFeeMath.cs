namespace AskVest.Modules.Wallets.Domain;

/// <summary>
/// Withdrawal fee math. The configured flat fee is denominated in token
/// units and is rounded down to the token's decimals so the platform never
/// overcharges; the net quantity is what leaves custody.
/// </summary>
public static class WalletFeeMath
{
    public static decimal FeeForToken(IReadOnlyDictionary<string, decimal> fees, Guid tokenId)
        => fees.TryGetValue(tokenId.ToString("D"), out var fee) ? fee : 0m;

    public static decimal NetQuantity(decimal grossQuantity, decimal fee, int decimals)
        => grossQuantity - decimal.Round(fee, decimals, MidpointRounding.ToZero);
}
