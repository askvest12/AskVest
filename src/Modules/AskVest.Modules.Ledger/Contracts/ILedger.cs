using AskVest.SharedKernel;

namespace AskVest.Modules.Ledger.Contracts;

public interface ILedger
{
    Task<Result<LedgerJournal>> PostJournalAsync(PostJournalRequest request, CancellationToken cancellationToken = default);

    Task<Result<LedgerJournal>> ReverseJournalAsync(string idempotencyKey, Guid journalId, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<LedgerBalance>>> GetBalancesAsync(Guid userId, Book book, CancellationToken cancellationToken = default);

    Task<Result> RegisterAssetAsync(string code, AssetKind kind, int scale, CancellationToken cancellationToken = default);
}
