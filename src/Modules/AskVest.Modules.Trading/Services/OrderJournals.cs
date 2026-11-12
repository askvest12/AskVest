using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Trading.Domain;

namespace AskVest.Modules.Trading.Services;

/// <summary>
/// Journal construction for real orders, per the money and ledger principles.
/// Holds reserve funds or shares; fills settle the hold exactly; releases
/// mirror the matching hold. Every journal posts inside the caller's
/// transaction scope through the Ledger contract.
/// </summary>
internal static class OrderJournals
{
    private const string ReferenceType = "real_order";

    public static PostJournalRequest Hold(
        Guid orderId,
        Guid userId,
        OrderSide side,
        string settlementAsset,
        string tokenAssetCode,
        decimal total,
        decimal quantity)
    {
        var userAvailable = LedgerAccounts.UserAvailable(Book.Real, userId);
        var userHeld = LedgerAccounts.UserHeld(Book.Real, userId);
        var assetCode = side == OrderSide.Buy ? settlementAsset : tokenAssetCode;
        var amount = side == OrderSide.Buy ? total : quantity;
        return new PostJournalRequest(
            $"real:order:{orderId:N}:hold",
            Book.Real,
            LedgerReasonCodes.Hold,
            ReferenceType,
            orderId,
            [
                new LedgerLine(userAvailable, assetCode, -amount),
                new LedgerLine(userHeld, assetCode, amount),
            ]);
    }

    public static PostJournalRequest BuyFill(
        Guid orderId,
        Guid userId,
        string settlementAsset,
        string tokenAssetCode,
        decimal quantity,
        decimal grossAmount,
        decimal ourFee,
        decimal gatewayFee,
        decimal total)
    {
        var userHeld = LedgerAccounts.UserHeld(Book.Real, userId);
        var userAvailable = LedgerAccounts.UserAvailable(Book.Real, userId);
        var clearing = LedgerAccounts.SystemClearing(Book.Real);
        var fees = LedgerAccounts.SystemFees(Book.Real);
        var feeTotal = ourFee + gatewayFee;
        var lines = new List<LedgerLine>
        {
            new(userHeld, settlementAsset, -total),
            new(clearing, settlementAsset, grossAmount),
        };
        if (feeTotal != 0m)
        {
            lines.Add(new LedgerLine(fees, settlementAsset, feeTotal));
        }

        lines.Add(new LedgerLine(clearing, tokenAssetCode, -quantity));
        lines.Add(new LedgerLine(userAvailable, tokenAssetCode, quantity));
        return new PostJournalRequest(
            $"real:order:{orderId:N}:fill",
            Book.Real,
            LedgerReasonCodes.TradeBuy,
            ReferenceType,
            orderId,
            lines);
    }

    public static PostJournalRequest SellFill(
        Guid orderId,
        Guid userId,
        string settlementAsset,
        string tokenAssetCode,
        decimal quantity,
        decimal grossAmount,
        decimal ourFee,
        decimal gatewayFee)
    {
        var userHeld = LedgerAccounts.UserHeld(Book.Real, userId);
        var userAvailable = LedgerAccounts.UserAvailable(Book.Real, userId);
        var clearing = LedgerAccounts.SystemClearing(Book.Real);
        var fees = LedgerAccounts.SystemFees(Book.Real);
        var feeTotal = ourFee + gatewayFee;
        var net = grossAmount - feeTotal;
        var lines = new List<LedgerLine>
        {
            new(userHeld, tokenAssetCode, -quantity),
            new(clearing, tokenAssetCode, quantity),
            new(clearing, settlementAsset, -grossAmount),
            new(userAvailable, settlementAsset, net),
        };
        if (feeTotal != 0m)
        {
            lines.Add(new LedgerLine(fees, settlementAsset, feeTotal));
        }

        return new PostJournalRequest(
            $"real:order:{orderId:N}:fill",
            Book.Real,
            LedgerReasonCodes.TradeSell,
            ReferenceType,
            orderId,
            lines);
    }

    public static string ReleaseKey(Guid orderId) => $"real:order:{orderId:N}:release";

}
