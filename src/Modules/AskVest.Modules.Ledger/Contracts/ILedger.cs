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

    Task<Result> RegisterAssetAsync(string code, AssetKind kind, int scale, CancellationToken cancellationToken = default);
}
