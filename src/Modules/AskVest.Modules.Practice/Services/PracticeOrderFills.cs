using AskVest.Modules.Ledger.Contracts;
using AskVest.SharedKernel;

namespace AskVest.Modules.Practice.Services;

/// <summary>
/// Journal construction for practice orders. Every operation posts through the
/// Ledger contract inside the caller's transaction scope.
/// </summary>
internal static class PracticeOrderFills
{
    private const string CashAsset = "USD";
    private const string ReferenceType = "practice_order";

    public static PostJournalRequest BuyFill(
        Guid orderId,
        Guid userId,
        string assetCode,
        decimal quantity,
        decimal cost,
        decimal fee)
    {
        var userAvailable = LedgerAccounts.UserAvailable(Book.Practice, userId);
        var clearing = LedgerAccounts.SystemClearing(Book.Practice);
        var lines = new List<LedgerLine>
        {
            new(userAvailable, CashAsset, -(cost + fee)),
            new(clearing, CashAsset, cost),
        };
        if (fee != 0m)
        {
            lines.Add(new LedgerLine(LedgerAccounts.SystemFees(Book.Practice), CashAsset, fee));
        }

        lines.Add(new LedgerLine(clearing, assetCode, -quantity));
        lines.Add(new LedgerLine(userAvailable, assetCode, quantity));
        return Journal(orderId, LedgerReasonCodes.TradeBuy, lines);
    }

    public static PostJournalRequest SellFill(
        Guid orderId,
        Guid userId,
        string assetCode,
        decimal quantity,
        decimal proceeds,
        decimal fee,
        bool fromHeld)
    {
        var source = fromHeld
            ? LedgerAccounts.UserHeld(Book.Practice, userId)
            : LedgerAccounts.UserAvailable(Book.Practice, userId);
        var userAvailable = LedgerAccounts.UserAvailable(Book.Practice, userId);
        var clearing = LedgerAccounts.SystemClearing(Book.Practice);
        var lines = new List<LedgerLine>
        {
            new(source, assetCode, -quantity),
            new(clearing, assetCode, quantity),
            new(clearing, CashAsset, -proceeds),
            new(userAvailable, CashAsset, proceeds - fee),
        };
        if (fee != 0m)
        {
            lines.Add(new LedgerLine(LedgerAccounts.SystemFees(Book.Practice), CashAsset, fee));
        }

        return Journal(orderId, LedgerReasonCodes.TradeSell, lines);
    }

    public static PostJournalRequest Hold(
        Guid orderId,
        Guid userId,
        string assetCode,
        decimal quantity)
        => new(
            $"practice:order:{orderId:N}:hold",
            Book.Practice,
            LedgerReasonCodes.Hold,
            ReferenceType,
            orderId,
            [
                new LedgerLine(LedgerAccounts.UserAvailable(Book.Practice, userId), assetCode, -quantity),
                new LedgerLine(LedgerAccounts.UserHeld(Book.Practice, userId), assetCode, quantity),
            ]);

    public static string FillKey(Guid orderId) => $"practice:order:{orderId:N}:fill";

    public static string ReleaseKey(Guid orderId) => $"practice:order:{orderId:N}:release";

    private static PostJournalRequest Journal(Guid orderId, string reasonCode, IReadOnlyList<LedgerLine> lines)
        => new(FillKey(orderId), Book.Practice, reasonCode, ReferenceType, orderId, lines);
}
