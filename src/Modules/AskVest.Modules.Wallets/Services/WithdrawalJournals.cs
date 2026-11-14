using AskVest.Modules.Ledger.Contracts;

namespace AskVest.Modules.Wallets.Services;

/// <summary>
/// Journal construction for withdrawals. The hold reserves the gross
/// quantity; settlement consumes the hold and credits the fee; the release
/// mirrors the hold exactly. Every journal posts inside the caller's
/// transaction scope through the Ledger contract.
/// </summary>
internal static class WithdrawalJournals
{
    private const string ReferenceType = "wallet_withdrawal";

    public static PostJournalRequest Hold(
        Guid withdrawalId,
        Guid userId,
        string assetCode,
        decimal grossQuantity)
    {
        return new PostJournalRequest(
            $"wallets:withdrawal:{withdrawalId:N}:hold",
            Book.Real,
            LedgerReasonCodes.Hold,
            ReferenceType,
            withdrawalId,
            [
                new LedgerLine(LedgerAccounts.UserAvailable(Book.Real, userId), assetCode, -grossQuantity),
                new LedgerLine(LedgerAccounts.UserHeld(Book.Real, userId), assetCode, grossQuantity),
            ]);
    }

    /// <summary>
    /// Settlement: the held gross leaves user property. The clearing account
    /// carries the net that custody pays out and the fee account carries the
    /// configured fee, so ledger totals still equal custody holdings.
    /// </summary>
    public static PostJournalRequest Settle(
        Guid withdrawalId,
        Guid userId,
        string assetCode,
        decimal grossQuantity,
        decimal fee)
    {
        var net = grossQuantity - fee;
        var lines = new List<LedgerLine>
        {
            new(LedgerAccounts.UserHeld(Book.Real, userId), assetCode, -grossQuantity),
            new(LedgerAccounts.SystemClearing(Book.Real), assetCode, net),
        };
        if (fee != 0m)
        {
            lines.Add(new LedgerLine(LedgerAccounts.SystemFees(Book.Real), assetCode, fee));
        }

        return new PostJournalRequest(
            $"wallets:withdrawal:{withdrawalId:N}:settle",
            Book.Real,
            LedgerReasonCodes.Withdrawal,
            ReferenceType,
            withdrawalId,
            lines);
    }

    public static string ReleaseKey(Guid withdrawalId)
        => $"wallets:withdrawal:{withdrawalId:N}:release";
}
