namespace AskVest.Modules.Wallets.Domain;

/// <summary>
/// The pure reserves comparison: custody holdings must equal the ledger
/// totals custody is responsible for. Mismatches are counted per chain and
/// token; reaching the configured threshold pauses withdrawals.
/// </summary>
public static class ReservesComparison
{
    public static bool Matches(decimal custodyQuantity, decimal ledgerQuantity)
        => custodyQuantity == ledgerQuantity;

    /// <summary>
    /// A matching run resets the streak; a mismatch extends it by one.
    /// </summary>
    public static int NextMismatchCount(int previousCount, bool matches)
        => matches ? 0 : previousCount + 1;

    public static bool ShouldPause(int mismatchCount, int threshold)
        => mismatchCount >= threshold;
}
