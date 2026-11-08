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

    private static Guid CurrentUserId(HttpContext httpContext)
        => Guid.Parse(httpContext.User.FindFirst("sub")!.Value);
}
