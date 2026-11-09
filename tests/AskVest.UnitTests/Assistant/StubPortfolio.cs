using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Portfolio.Contracts;
using AskVest.SharedKernel;

namespace AskVest.UnitTests.Assistant;

internal sealed class StubPortfolio : IPortfolio
{
    public PortfolioResponse? Portfolio { get; set; }

    public List<WatchlistEntry> Watchlist { get; } = [];

    public Task<Result<PortfolioResponse>> GetPortfolioAsync(
        Guid userId, Book book, CancellationToken cancellationToken = default)
        => Task.FromResult(Portfolio is null
            ? Result.Failure<PortfolioResponse>(new Error("resource.not_found", "No portfolio.", ErrorType.NotFound))
            : Result.Success(Portfolio));

    public Task<IReadOnlyList<WatchlistEntry>> GetWatchlistAsync(Guid userId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WatchlistEntry>>(Watchlist);

    public Task<Result> AddWatchlistItemAsync(Guid userId, string instrumentId, CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Success());

    public Task<Result> RemoveWatchlistItemAsync(Guid userId, string instrumentId, CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Success());
}
