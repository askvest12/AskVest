using AskVest.Modules.Ledger.Contracts;

namespace AskVest.Modules.Funding.Services;

/// <summary>
/// Journal construction for cash withdrawals. The hold reserves the total
/// debited; settlement consumes the hold, sends the receive amount to
/// clearing for the provider payout and books the fee; the release mirrors
/// the hold exactly. Every journal posts inside the caller's transaction
/// scope through the Ledger contract.
/// </summary>
internal static class FundingWithdrawalJournals
{
    private const string ReferenceType = "funding_withdrawal";

    public static PostJournalRequest Hold(
        Guid withdrawalId,
        Guid userId,
        string currency,
        decimal totalDebited)
        => new(
            $"funding:withdrawal:{withdrawalId:N}:hold",
            Book.Real,
            LedgerReasonCodes.Hold,
            ReferenceType,
            withdrawalId,
            [
                new LedgerLine(LedgerAccounts.UserAvailable(Book.Real, userId), currency, -totalDebited),
                new LedgerLine(LedgerAccounts.UserHeld(Book.Real, userId), currency, totalDebited),
            ]);

    public static PostJournalRequest Settle(
        Guid withdrawalId,
        Guid userId,
        string currency,
        decimal receiveAmount,
        decimal feeAmount,
        decimal totalDebited)
    {
        var lines = new List<LedgerLine>
        {
            new(LedgerAccounts.UserHeld(Book.Real, userId), currency, -totalDebited),
            new(LedgerAccounts.SystemClearing(Book.Real), currency, receiveAmount),
        };
        if (feeAmount != 0m)
        {
            lines.Add(new LedgerLine(LedgerAccounts.SystemFees(Book.Real), currency, feeAmount));
        }

        return new(
            $"funding:withdrawal:{withdrawalId:N}:settle",
            Book.Real,
            LedgerReasonCodes.Withdrawal,
            ReferenceType,
            withdrawalId,
            lines);
    }

    public static PostJournalRequest Release(
        Guid withdrawalId,
        Guid userId,
        string currency,
        decimal totalDebited)
        => new(
            $"funding:withdrawal:{withdrawalId:N}:release",
            Book.Real,
            LedgerReasonCodes.Release,
            ReferenceType,
            withdrawalId,
            [
                new LedgerLine(LedgerAccounts.UserHeld(Book.Real, userId), currency, -totalDebited),
                new LedgerLine(LedgerAccounts.UserAvailable(Book.Real, userId), currency, totalDebited),
            ]);
}
