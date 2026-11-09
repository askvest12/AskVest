using System.Text.Json;
using AskVest.Modules.Assistant.Contracts;
using AskVest.Modules.Compliance.Contracts;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Portfolio.Contracts;

namespace AskVest.Modules.Assistant.Tools;

/// <summary>
/// The caller's own portfolio for one book. The real book answers
/// "not available" as data when the caller is not real-eligible, and the
/// ledger is never queried for them.
/// </summary>
internal sealed class GetPortfolioTool(IPortfolio portfolio, IComplianceEligibility eligibility) : AssistantTool
{
    public override string Name => "get_portfolio";

    public override string Description
        => "Get the caller's own portfolio summary for the practice or real book: cash, holdings, cost basis and totals. The real book is only available to real-eligible users.";

    public override string ParametersSchemaJson => """
        {"type":"object","properties":{"book":{"type":"string","enum":["practice","real"]}},"required":["book"],"additionalProperties":false}
        """;

    public override async Task<ToolResult> ExecuteAsync(
        JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var bookError = ToolArguments.OptionalEnum(arguments, "book", "practice", "real");
        var hasBook = arguments.TryGetProperty("book", out var bookElement)
            && bookElement.ValueKind == JsonValueKind.String;
        if (bookError is not null || !hasBook)
        {
            return ToolResult.Fail("invalid_arguments", "'book' is required and must be 'practice' or 'real'.");
        }

        var book = bookElement.GetString()! == "real" ? Book.Real : Book.Practice;
        var asOf = context.TimeProvider.GetUtcNow();

        if (book == Book.Real)
        {
            var eligibilityResult = await eligibility.GetEligibilityAsync(context.UserId, cancellationToken);
            if (!eligibilityResult.RealAllowed)
            {
                var unavailable = new
                {
                    book = "real",
                    available = false,
                    reason = "not_eligible",
                };
                return ToolResult.Ok(
                    ToolJson.Serialize(unavailable),
                    [new ToolSource("portfolio", "real", "real portfolio", asOf, "not_available")]);
            }
        }

        var result = await portfolio.GetPortfolioAsync(context.UserId, book, cancellationToken);
        if (result.IsFailure)
        {
            return ToolResult.Fail(result.Error!.Code, result.Error.Message);
        }

        var data = result.Value!;
        var payload = new
        {
            book = book == Book.Real ? "real" : "practice",
            asOf = ToolJson.Ts(data.AsOf),
            valuationCurrency = data.ValuationCurrency,
            partial = data.Partial,
            cash = data.Cash.Select(line => new
            {
                asset = line.Asset,
                kind = line.Kind,
                available = ToolJson.Dec(line.Available),
                held = ToolJson.Dec(line.Held),
                includedInTotal = line.IncludedInTotal,
            }),
            holdings = data.Holdings.Select(holding => new
            {
                instrumentId = holding.InstrumentId,
                symbol = holding.Symbol,
                name = holding.Name,
                quantity = ToolJson.Dec(holding.Quantity),
                costBasis = ToolJson.Dec(holding.CostBasis),
                averageCostPerUnit = ToolJson.Dec(holding.AverageCostPerUnit),
                latestPrice = ToolJson.Dec(holding.LatestPrice),
                priceCurrency = holding.PriceCurrency,
                priceLabel = holding.PriceLabel,
                marketValue = ToolJson.Dec(holding.MarketValue),
                unrealizedProfit = ToolJson.Dec(holding.UnrealizedProfit),
                unrealizedProfitPercent = ToolJson.Dec(holding.UnrealizedProfitPercent),
                realizedProfit = ToolJson.Dec(holding.RealizedProfit),
                income = ToolJson.Dec(holding.Income),
                flags = holding.Flags,
            }),
            totals = new
            {
                cash = ToolJson.Dec(data.Totals.Cash),
                holdingsValue = ToolJson.Dec(data.Totals.HoldingsValue),
                costBasis = ToolJson.Dec(data.Totals.CostBasis),
                unrealized = ToolJson.Dec(data.Totals.Unrealized),
                realized = ToolJson.Dec(data.Totals.Realized),
                income = ToolJson.Dec(data.Totals.Income),
                totalValue = ToolJson.Dec(data.Totals.TotalValue),
            },
        };

        return ToolResult.Ok(
            ToolJson.Serialize(payload),
            [new ToolSource(
                "portfolio",
                book == Book.Real ? "real" : "practice",
                $"{(book == Book.Real ? "real" : "practice")} portfolio",
                data.AsOf)]);
    }
}

/// <summary>The caller's own watchlist.</summary>
internal sealed class GetWatchlistTool(IPortfolio portfolio) : AssistantTool
{
    public override string Name => "get_watchlist";

    public override string Description
        => "Get the caller's own watchlist with each instrument's latest price label.";

    public override string ParametersSchemaJson => """
        {"type":"object","properties":{},"additionalProperties":false}
        """;

    public override async Task<ToolResult> ExecuteAsync(
        JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var entries = await portfolio.GetWatchlistAsync(context.UserId, cancellationToken);
        var asOf = context.TimeProvider.GetUtcNow();
        var payload = new
        {
            asOf = ToolJson.Ts(asOf),
            count = entries.Count,
            items = entries.Select(entry => new
            {
                instrumentId = entry.InstrumentId,
                symbol = entry.Symbol,
                name = entry.Name,
                status = entry.Status,
                price = ToolJson.Dec(entry.Price),
                currency = entry.Currency,
                label = entry.Label,
                session = entry.Session,
                usableForExecution = entry.UsableForExecution,
                addedAt = ToolJson.Ts(entry.AddedAt),
            }),
        };

        return ToolResult.Ok(
            ToolJson.Serialize(payload),
            entries.Select(entry => new ToolSource(
                "watchlist_item",
                entry.InstrumentId,
                entry.Symbol,
                entry.AddedAt,
                entry.Label)).ToList());
    }
}
