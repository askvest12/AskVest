using AskVest.Modules.Compliance.Contracts;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Portfolio.Contracts;
using AskVest.Modules.Portfolio.Services;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Portfolio.Endpoints;

public static class PortfolioEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        MapBookPortfolio(api.MapGroup("/practice").RequireAuthorization(CompliancePolicyNames.PracticeEligible), Book.Practice);
        MapBookPortfolio(api.MapGroup("/real").RequireAuthorization(CompliancePolicyNames.RealEligible), Book.Real);

        var watchlist = api.MapGroup("/watchlist").WithTags("Portfolio");
        watchlist.MapGet("/", (HttpContext httpContext, IPortfolio portfolio, CancellationToken cancellationToken) =>
                ListWatchlistAsync(httpContext, portfolio, cancellationToken))
            .WithName("ListWatchlist")
            .WithSummary("The caller's watchlist in added order.");
        watchlist.MapPut("/{instrumentId}", (HttpContext httpContext, string instrumentId, IPortfolio portfolio, CancellationToken cancellationToken) =>
                AddWatchlistAsync(httpContext, instrumentId, portfolio, cancellationToken))
            .WithName("AddWatchlistItem")
            .WithSummary("Add an instrument to the watchlist. Idempotent set operation: adding again is a harmless success, so no Idempotency-Key is required.")
            .WithMetadata(new IdempotencyExemptAttribute());
        watchlist.MapDelete("/{instrumentId}", (HttpContext httpContext, string instrumentId, IPortfolio portfolio, CancellationToken cancellationToken) =>
                RemoveWatchlistAsync(httpContext, instrumentId, portfolio, cancellationToken))
            .WithName("RemoveWatchlistItem")
            .WithSummary("Remove an instrument from the watchlist. Idempotent set operation: removing a missing item is a harmless success, so no Idempotency-Key is required.")
            .WithMetadata(new IdempotencyExemptAttribute());
    }

    private static void MapBookPortfolio(RouteGroupBuilder group, Book book)
    {
        group.MapGet("/portfolio", (HttpContext httpContext, IPortfolio portfolio, CancellationToken cancellationToken) =>
                GetPortfolioAsync(httpContext, portfolio, book, cancellationToken))
            .WithName($"Get{book}Portfolio")
            .WithSummary("The caller's portfolio for this book, replayed from the ledger.")
            .WithTags("Portfolio");
    }

    private static async Task<IResult> GetPortfolioAsync(
        HttpContext httpContext,
        IPortfolio portfolio,
        Book book,
        CancellationToken cancellationToken)
    {
        var result = await portfolio.GetPortfolioAsync(CurrentUserId(httpContext), book, cancellationToken);
        if (result.IsFailure)
        {
            return PortfolioResults.Error(result.Error!, httpContext);
        }

        return PortfolioResults.Ok(result.Value, "Portfolio loaded.", httpContext);
    }

    private static async Task<IResult> ListWatchlistAsync(
        HttpContext httpContext,
        IPortfolio portfolio,
        CancellationToken cancellationToken)
    {
        var items = await portfolio.GetWatchlistAsync(CurrentUserId(httpContext), cancellationToken);
        return PortfolioResults.Ok(new { items }, "Watchlist loaded.", httpContext);
    }

    private static async Task<IResult> AddWatchlistAsync(
        HttpContext httpContext,
        string instrumentId,
        IPortfolio portfolio,
        CancellationToken cancellationToken)
    {
        var result = await portfolio.AddWatchlistItemAsync(CurrentUserId(httpContext), instrumentId, cancellationToken);
        return result.IsFailure
            ? PortfolioResults.Error(result.Error!, httpContext)
            : PortfolioResults.Ok(new { instrumentId }, "Watchlist item added.", httpContext);
    }

    private static async Task<IResult> RemoveWatchlistAsync(
        HttpContext httpContext,
        string instrumentId,
        IPortfolio portfolio,
        CancellationToken cancellationToken)
    {
        var result = await portfolio.RemoveWatchlistItemAsync(CurrentUserId(httpContext), instrumentId, cancellationToken);
        return result.IsFailure
            ? PortfolioResults.Error(result.Error!, httpContext)
            : PortfolioResults.Ok(new { instrumentId }, "Watchlist item removed.", httpContext);
    }

    private static Guid CurrentUserId(HttpContext httpContext)
        => Guid.Parse(httpContext.User.FindFirst("sub")!.Value);
}
