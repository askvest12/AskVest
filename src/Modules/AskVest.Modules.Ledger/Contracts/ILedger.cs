using AskVest.SharedKernel;

namespace AskVest.Modules.Ledger.Contracts;

public interface ILedger
{
    Task<Result<LedgerJournal>> PostJournalAsync(PostJournalRequest request, CancellationToken cancellationToken = default);

    Task<Result<LedgerJournal>> ReverseJournalAsync(string idempotencyKey, Guid journalId, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<LedgerBalance>>> GetBalancesAsync(Guid userId, Book book, CancellationToken cancellationToken = default);

    Task<PageResult<LedgerMovement>> GetMovementsAsync(
        Guid userId,
        Book book,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default);

    Task<AssetKind?> GetAssetKindAsync(string assetCode, CancellationToken cancellationToken = default);

    /// <summary>Returns the registered kind and scale, or null when unregistered.</summary>
    Task<LedgerAssetInfo?> GetAssetInfoAsync(string assetCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Per-asset totals across user available, user held and system fees
    /// accounts in the given book. Used by the wallets reserves check to
    /// compare ledger ownership with custody holdings.
    /// </summary>
    Task<Result<IReadOnlyList<LedgerAssetTotal>>> GetAssetTotalsAsync(
        Book book,
        CancellationToken cancellationToken = default);

    Task<Result> RegisterAssetAsync(string code, AssetKind kind, int scale, CancellationToken cancellationToken = default);
}
