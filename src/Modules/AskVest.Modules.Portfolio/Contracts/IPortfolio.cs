using AskVest.Modules.Ledger.Contracts;
using AskVest.SharedKernel;

namespace AskVest.Modules.Portfolio.Contracts;

/// <summary>
/// The only public surface of the Portfolio module for other modules. It
/// exposes a user's portfolio for one book and the user's watchlist; it never
/// exposes ledger internals or account ids.
/// </summary>
public interface IPortfolio
{
    Task<Result<PortfolioResponse>> GetPortfolioAsync(Guid userId, Book book, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WatchlistEntry>> GetWatchlistAsync(Guid userId, CancellationToken cancellationToken = default);

}
