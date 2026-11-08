using AskVest.Modules.Practice.Contracts;
using AskVest.Modules.Practice.Domain;
using AskVest.Modules.Practice.Persistence;
using AskVest.Modules.Practice.Services;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Practice.Services;

/// <summary>
/// The only public surface of the Practice module for other modules.
/// </summary>
internal sealed class PracticeContractService(PracticeDbContext db) : IPractice
{
    public async Task<PracticeAccountInfo> GetAccountInfoAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var account = await db.PracticeAccounts.AsNoTracking()
            .FirstOrDefaultAsync(row => row.UserId == userId, cancellationToken);
        return account is null
            ? new PracticeAccountInfo(false, null, 0, null)
            : new PracticeAccountInfo(true, account.StartedAt, account.ResetCount, account.LastResetAt);
    }

    public async Task<IReadOnlyList<PracticeOrderSummary>> GetOrdersSinceAsync(
        Guid userId,
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        var orders = await db.PracticeOrders.AsNoTracking()
            .Where(order => order.UserId == userId && order.CreatedAt >= since)
            .OrderBy(order => order.CreatedAt)
            .ToListAsync(cancellationToken);
        if (orders.Count == 0)
        {
            return [];
        }

        var assetCodes = orders.Select(order => order.InstrumentId).Distinct().ToList();
        var mappings = await db.PracticeInstruments.AsNoTracking()
            .Where(row => assetCodes.Contains(row.InstrumentId))
            .ToDictionaryAsync(row => row.InstrumentId, row => row.AssetCode, StringComparer.Ordinal, cancellationToken);

        return orders.Select(order => new PracticeOrderSummary(
            order.Id,
            order.InstrumentId,
            mappings.GetValueOrDefault(order.InstrumentId),
            PracticeText.ToDb(order.Side),
            PracticeText.ToDb(order.Type),
            PracticeText.ToDb(order.State),
            order.RequestedAmount,
            order.RequestedQuantity,
            order.SellAll,
            order.StopPrice,
            order.FilledQuantity,
            order.FillPrice,
            order.CashAmount,
            order.Fee,
            order.CreatedAt,
            order.FilledAt,
            order.CancelledAt)).ToList();
    }
}
