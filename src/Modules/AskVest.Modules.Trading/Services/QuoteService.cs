using System.Globalization;
using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.MarketData.Contracts;
using AskVest.Modules.Trading.Configuration;
using AskVest.Modules.Trading.Domain;
using AskVest.Modules.Trading.Endpoints;
using AskVest.Modules.Trading.Gateway;
using AskVest.Modules.Trading.Persistence;
using AskVest.Modules.Trading.Persistence.Entities;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Trading.Services;

/// <summary>
/// Firm quote creation. A quote stores every term the user saw plus the terms
/// hash the step-up grant binds to. Gateway failures and guard refusals store
/// nothing.
/// </summary>
internal sealed class QuoteService(
    TradingDbContext db,
    ICatalog catalog,
    IMarketData marketData,
    ILedger ledger,
    GatewayRegistry gateways,
    IOptions<TradingOptions> options,
    TimeProvider timeProvider,
    ILogger<QuoteService> logger)
{
    public async Task<Result<TradingQuote>> CreateAsync(
        Guid userId,
        QuoteRequest request,
        CancellationToken cancellationToken)
    {
        var parsed = Parse(request);
        if (parsed.Error is not null)
        {
            return Result.Failure<TradingQuote>(parsed.Error);
        }

        var rateLimited = await CheckRateLimitAsync(userId, cancellationToken);
        if (rateLimited)
        {
            return Result.Failure<TradingQuote>(new Error(
                ErrorCodes.RateLimitExceeded,
                "Too many quote requests.",
                ErrorType.RateLimited));
        }

        var token = await catalog.FindTokenAsync(parsed.TokenId!.Value, cancellationToken);
        var availability = token is null
            ? null
            : await catalog.GetAvailabilityAsync(userId, token.Id, cancellationToken);
        var allowed = parsed.Side == OrderSide.Buy ? availability?.Buyable : availability?.Sellable;
        if (token is null || availability is null || allowed != true)
        {
            return Result.Failure<TradingQuote>(TradingResults.NotAvailable(
                "token_not_tradable",
                [new ValidationFailure("tokenId", token is null ? "unknown" : "not_available")]));
        }

        var blockers = parsed.Side == OrderSide.Buy ? availability.BuyBlockers : availability.SellBlockers;
        if (blockers.Count > 0)
        {
            return Result.Failure<TradingQuote>(TradingResults.NotAvailable(
                "token_blocked",
                blockers.Select(code => new ValidationFailure("blocker", code)).ToArray()));
        }

        var settings = options.Value;
        var switchOff = parsed.Side == OrderSide.Buy ? !settings.BuyEnabled : !settings.SellEnabled;
        if (switchOff)
        {
            return Result.Failure<TradingQuote>(TradingResults.NotAvailable(
                parsed.Side == OrderSide.Buy ? "buy_disabled" : "sell_disabled"));
        }

        if (!gateways.TryResolve(token.GatewayKey, out var gateway))
        {
            return Result.Failure<TradingQuote>(TradingResults.NotAvailable("gateway_route_missing"));
        }

        var route = settings.Gateway.Routes[token.GatewayKey];
        var settlementAsset = route.SettlementAsset;

        var reference = await marketData.GetLatestPriceAsync(token.InstrumentId, cancellationToken);
        var session = reference?.Session ?? "unknown";
        var marketOpen = string.Equals(session, "regular", StringComparison.Ordinal);
        if (string.Equals(token.ScheduleKind, "underlying_market_hours", StringComparison.Ordinal) && !marketOpen)
        {
            return Result.Failure<TradingQuote>(TradingResults.NotAvailable("market_closed"));
        }

        var sellQuantity = parsed.Side == OrderSide.Sell
            ? await ResolveSellQuantityAsync(userId, token, parsed, cancellationToken)
            : null;
        if (parsed.Side == OrderSide.Sell && sellQuantity is null)
        {
            return Result.Failure<TradingQuote>(TradingResults.NotAvailable("no_sellable_balance"));
        }

        var gatewayRequest = new GatewayQuoteRequest(
            ToReference(token),
            parsed.Side,
            parsed.Side == OrderSide.Buy ? parsed.Amount : null,
            parsed.Side == OrderSide.Sell ? sellQuantity : null,
            settlementAsset);

        GatewayQuote gatewayQuote;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(settings.GatewayTimeoutSeconds), timeProvider))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
        {
            var quoted = await gateway.GetFirmQuoteAsync(gatewayRequest, linked.Token);
            if (quoted.IsFailure)
            {
                return Result.Failure<TradingQuote>(TradingResults.QuoteUnavailable());
            }

            gatewayQuote = quoted.Value;
        }

        var validation = ValidateGatewayQuote(gatewayQuote, token, settlementAsset);
        if (validation is not null)
        {
            return Result.Failure<TradingQuote>(validation);
        }

        if (reference is { UsableForExecution: true, Price: > 0m } usable
            && DeviationExceeded(usable.Price.Value, gatewayQuote.Price, settings.MaxQuoteDeviationPercent))
        {
            return Result.Failure<TradingQuote>(TradingResults.QuoteUnavailable());
        }

        var now = timeProvider.GetUtcNow();
        var expiresAt = gatewayQuote.ExpiresAt < now + TimeSpan.FromSeconds(settings.QuoteLifetimeSeconds)
            ? gatewayQuote.ExpiresAt
            : now + TimeSpan.FromSeconds(settings.QuoteLifetimeSeconds);
        var ourFee = TradingFeeMath.OurFee(gatewayQuote.GrossAmount, settings.FeeBasisPoints);
        var total = parsed.Side == OrderSide.Buy
            ? TradingFeeMath.BuyTotal(gatewayQuote.GrossAmount, ourFee, gatewayQuote.GatewayFee)
            : TradingFeeMath.SellTotal(gatewayQuote.GrossAmount, ourFee, gatewayQuote.GatewayFee);

        var warnings = new List<string>();
        if (string.Equals(token.ScheduleKind, "always", StringComparison.Ordinal) && !marketOpen)
        {
            warnings.Add("underlying_market_closed");
        }

        if (reference is not { UsableForExecution: true })
        {
            warnings.Add("reference_price_unusable");
        }

        var quote = new TradingQuote
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            TokenId = token.Id,
            Side = parsed.Side,
            Quantity = gatewayQuote.Quantity,
            Price = gatewayQuote.Price,
            GrossAmount = gatewayQuote.GrossAmount,
            OurFee = ourFee,
            GatewayFee = gatewayQuote.GatewayFee,
            Total = total,
            SettlementAsset = settlementAsset,
            GatewayKey = token.GatewayKey,
            GatewayQuoteId = gatewayQuote.GatewayQuoteId,
            Warnings = string.Join('|', warnings),
            State = QuoteState.Open,
            ExpiresAt = expiresAt,
            CreatedAt = now,
        };
        quote.TermsHash = TermsHash.Compute(
            quote.Id, token.Id, parsed.Side, quote.Quantity, quote.Price,
            quote.GrossAmount, quote.OurFee, quote.GatewayFee, quote.Total,
            quote.SettlementAsset, quote.ExpiresAt);

        db.Quotes.Add(quote);
        await db.SaveChangesAsync(cancellationToken);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Real quote {QuoteId} created.", quote.Id);
        }

        return Result.Success(quote);
    }

    public async Task<Result<TradingQuote>> GetAsync(
        Guid userId,
        Guid quoteId,
        CancellationToken cancellationToken)
    {
        var quote = await db.Quotes.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == quoteId && row.UserId == userId, cancellationToken);
        return quote is null
            ? Result.Failure<TradingQuote>(new Error(
                ErrorCodes.ResourceNotFound,
                "The quote was not found.",
                ErrorType.NotFound))
            : Result.Success(quote);
    }

    private async Task<bool> CheckRateLimitAsync(Guid userId, CancellationToken cancellationToken)
    {
        var windowStart = timeProvider.GetUtcNow() - TimeSpan.FromMinutes(1);
        var recent = await db.Quotes.CountAsync(
            quote => quote.UserId == userId && quote.CreatedAt > windowStart,
            cancellationToken);
        return recent >= options.Value.QuotesPerUserPerMinute;
    }

    private async Task<decimal?> ResolveSellQuantityAsync(
        Guid userId,
        CatalogToken token,
        ParsedQuote parsed,
        CancellationToken cancellationToken)
    {
        var balances = await ledger.GetBalancesAsync(userId, Book.Real, cancellationToken);
        if (balances.IsFailure)
        {
            return null;
        }

        var available = balances.Value
            .FirstOrDefault(balance => balance.AssetCode == token.LedgerAssetCode)?.Available ?? 0m;
        if (available <= 0m)
        {
            return null;
        }

        if (!parsed.SellAll)
        {
            return parsed.Quantity;
        }

        var factor = (decimal)Math.Pow(10, token.Decimals);
        var floored = decimal.Floor(available * factor) / factor;
        return floored > 0m ? floored : null;
    }

    private Error? ValidateGatewayQuote(GatewayQuote quote, CatalogToken token, string settlementAsset)
    {
        var now = timeProvider.GetUtcNow();
        if (quote.Price <= 0m || quote.Quantity <= 0m || quote.GrossAmount <= 0m)
        {
            return TradingResults.QuoteUnavailable();
        }

        if (quote.GatewayFee < 0m
            || !string.Equals(quote.SettlementAsset, settlementAsset, StringComparison.Ordinal)
            || quote.ExpiresAt <= now
            || ScaleOf(quote.Quantity) > token.Decimals
            || ScaleOf(quote.Price) > 18)
        {
            return TradingResults.QuoteUnavailable();
        }

        var expectedGross = TradingFeeMath.RoundMoney(quote.Quantity * quote.Price);
        return quote.GrossAmount == expectedGross
            ? null
            : TradingResults.QuoteUnavailable();
    }

    private static bool DeviationExceeded(decimal reference, decimal gatewayPrice, decimal maxPercent)
        => Math.Abs(gatewayPrice - reference) / reference * 100m > maxPercent;

    private static int ScaleOf(decimal value)
    {
        var bits = decimal.GetBits(value);
        return (bits[3] >> 16) & 0x7F;
    }

    private static GatewayTokenReference ToReference(CatalogToken token)
        => new(
            token.Id,
            token.InstrumentId,
            token.ChainId,
            token.CanonicalAddress,
            token.Caip19,
            token.Symbol,
            token.Decimals,
            token.LedgerAssetCode);

    private static ParsedQuote Parse(QuoteRequest request)
    {
        var side = request.Side?.Trim().ToLowerInvariant() == "sell" ? OrderSide.Sell : OrderSide.Buy;
        if (request.Side?.Trim().ToLowerInvariant() is not ("buy" or "sell") || request.TokenId is null)
        {
            return new ParsedQuote(OrderSide.Buy, null, null, false, null, TradingResults.OrderInvalid("side or tokenId is not recognized"));
        }

        var isBuy = side == OrderSide.Buy;
        decimal? amount = null;
        decimal? quantity = null;
        var sellAll = request.SellAll == true;
        if (request.Amount is not null)
        {
            if (!decimal.TryParse(request.Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedAmount))
            {
                return new ParsedQuote(side, null, null, false, null, TradingResults.OrderInvalid("amount is not a valid decimal"));
            }

            amount = parsedAmount;
        }

        if (request.Quantity is not null)
        {
            if (!decimal.TryParse(request.Quantity, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedQuantity))
            {
                return new ParsedQuote(side, null, null, false, null, TradingResults.OrderInvalid("quantity is not a valid decimal"));
            }

            quantity = parsedQuantity;
        }

        if (isBuy)
        {
            if (amount is null or <= 0m || quantity is not null || sellAll)
            {
                return new ParsedQuote(side, null, null, false, null, TradingResults.OrderInvalid("a buy quote requires a positive amount and nothing else"));
            }
        }
        else if (amount is not null || (quantity is null) == !sellAll || (quantity is <= 0m))
        {
            return new ParsedQuote(side, null, null, false, null, TradingResults.OrderInvalid("a sell quote requires exactly one of quantity or sellAll"));
        }

        return new ParsedQuote(side, request.TokenId.Value, amount, sellAll, quantity, null);
    }

    private sealed record ParsedQuote(
        OrderSide Side,
        Guid? TokenId,
        decimal? Amount,
        bool SellAll,
        decimal? Quantity,
        Error? Error);
}
