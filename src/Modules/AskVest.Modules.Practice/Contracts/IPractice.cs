namespace AskVest.Modules.Practice.Contracts;

/// <summary>
/// Read-only practice account status for other modules. Phase 8 consumes this
/// for portfolio views; it never exposes ledger internals.
/// </summary>
public interface IPractice
{
    Task<PracticeAccountInfo> GetAccountInfoAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PracticeOrderSummary>> GetOrdersSinceAsync(
        Guid userId,
        DateTimeOffset since,
        CancellationToken cancellationToken = default);
}
