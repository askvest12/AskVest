using AskVest.Modules.MarketData.Contracts;
using AskVest.Modules.Trading.Configuration;
using AskVest.Modules.Trading.Domain;
using AskVest.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Trading.Gateway;

/// <summary>
/// The deterministic in-memory gateway used in development and tests. Quotes
/// derive from the Market Data reference price plus a configured spread;
/// submissions are keyed by our order id so repeated deliveries resolve to
/// the same stored order. The scenario is mutable so the diagnostics probe
/// and tests can arm failure modes. Refused in Production at startup.
/// </summary>
internal sealed class FakeIssuerGateway(
    IServiceScopeFactory scopeFactory,
    IOptions<TradingOptions> options,
    TimeProvider timeProvider) : IIssuerGateway
{
    internal const string ProviderName = "fake";

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, StoredOrder> _orders = new(StringComparer.Ordinal);

    public async Task<Result<GatewayQuote>> GetFirmQuoteAsync(
        GatewayQuoteRequest request,
        CancellationToken cancellationToken = default)
    {
        var scenario = options.Value.Fake.Scenario;
        if (string.Equals(scenario, "quote_error", StringComparison.Ordinal))
        {
            return Result.Failure<GatewayQuote>(new Error(
                ErrorCodes.TradingQuoteUnavailable,
                "The gateway could not produce a quote.",
                ErrorType.Unavailable));
        }

        // Market Data is scoped; the singleton gateway resolves it per call.
        await using var scope = scopeFactory.CreateAsyncScope();
        var marketData = scope.ServiceProvider.GetRequiredService<IMarketData>();
        var price = await marketData.GetLatestPriceAsync(request.Token.InstrumentId, cancellationToken);
        if (price?.Price is not { } reference || reference <= 0m)
        {
            return Result.Failure<GatewayQuote>(new Error(
                ErrorCodes.TradingQuoteUnavailable,
                "The gateway has no reference price for the token.",
                ErrorType.Unavailable));
        }

        var spread = options.Value.Fake.SpreadPercent / 100m;
        var quotePrice = request.Side == OrderSide.Buy
            ? reference * (1m + spread)
            : reference * (1m - spread);
        quotePrice = RoundToDecimals(quotePrice, request.Token.Decimals);
        if (quotePrice <= 0m)
        {
            return Result.Failure<GatewayQuote>(new Error(
                ErrorCodes.TradingQuoteUnavailable,
                "The gateway quote price is not positive.",
                ErrorType.Unavailable));
        }

        decimal quantity;
        decimal gross;
        if (request.Side == OrderSide.Buy)
        {
            var amount = request.Amount ?? 0m;
            quantity = FloorToDecimals(amount / quotePrice, request.Token.Decimals);
            gross = TradingFeeMath.RoundMoney(quantity * quotePrice);
        }
        else
        {
            quantity = request.Quantity ?? 0m;
            gross = TradingFeeMath.RoundMoney(quantity * quotePrice);
        }

        if (quantity <= 0m || gross <= 0m)
        {
            return Result.Failure<GatewayQuote>(new Error(
                ErrorCodes.TradingQuoteUnavailable,
                "The gateway quote has no positive quantity or gross amount.",
                ErrorType.Unavailable));
        }

        var now = timeProvider.GetUtcNow();
        var expiresAt = string.Equals(scenario, "short_quote_expiry", StringComparison.Ordinal)
            ? now + TimeSpan.FromSeconds(2)
            : now + TimeSpan.FromSeconds(options.Value.QuoteLifetimeSeconds);
        var quoteId = QuoteIdFor(request, quotePrice, quantity);
        return Result.Success(new GatewayQuote(
            quoteId,
            quotePrice,
            quantity,
            gross,
            options.Value.Fake.GatewayFeeAmount,
            request.SettlementAsset,
            expiresAt));
    }

    public async Task<GatewaySubmitResult> SubmitOrderAsync(
        GatewaySubmitRequest request,
        CancellationToken cancellationToken = default)
    {
        var scenario = options.Value.Fake.Scenario;
        if (string.Equals(scenario, "rejected", StringComparison.Ordinal))
        {
            return new GatewaySubmitResult(GatewaySubmitKind.Rejected, null, "gateway_rejected");
        }

        if (string.Equals(scenario, "slow_submit", StringComparison.Ordinal))
        {
            await Task.Delay(TimeSpan.FromSeconds(options.Value.Fake.SubmitDelaySeconds), timeProvider, cancellationToken);
        }

        if (string.Equals(scenario, "submit_timeout_lost", StringComparison.Ordinal))
        {
            // The call timed out and the order never reached the gateway.
            return new GatewaySubmitResult(GatewaySubmitKind.Unknown, null, null);
        }

        var stored = new StoredOrder(
            request,
            timeProvider.GetUtcNow(),
            scenario);
        _orders[request.OurReference] = stored;

        return string.Equals(scenario, "submit_timeout_filled", StringComparison.Ordinal)
            ? new GatewaySubmitResult(GatewaySubmitKind.Unknown, null, null)
            : new GatewaySubmitResult(
                GatewaySubmitKind.Acknowledged,
                $"fake-order-{request.OurReference}",
                null);
    }

    public Task<GatewayOrderStatus> GetOrderStatusAsync(
        string ourReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(options.Value.Fake.Scenario, "status_unavailable", StringComparison.Ordinal))
        {
            return Task.FromResult(new GatewayOrderStatus(GatewayStatusKind.Unavailable, null, null));
        }

        if (!_orders.TryGetValue(ourReference, out var order))
        {
            return Task.FromResult(new GatewayOrderStatus(GatewayStatusKind.NotFound, null, null));
        }

        if (string.Equals(order.Scenario, "rejected", StringComparison.Ordinal))
        {
            return Task.FromResult(new GatewayOrderStatus(GatewayStatusKind.Rejected, null, "gateway_rejected"));
        }

        var delaySeconds = order.Scenario == "delayed_fill"
            ? options.Value.Fake.FillDelaySeconds * 4
            : options.Value.Fake.FillDelaySeconds;
        if (timeProvider.GetUtcNow() < order.SubmittedAt + TimeSpan.FromSeconds(delaySeconds))
        {
            return Task.FromResult(new GatewayOrderStatus(GatewayStatusKind.Pending, null, null));
        }

        var request = order.Request;
        var fill = order.Scenario == "fill_mismatch"
            ? MismatchedFill(request)
            : ExactFill(request);
        return Task.FromResult(new GatewayOrderStatus(GatewayStatusKind.Filled, fill, null));
    }

    /// <summary>
    /// Test and probe hook: overrides the configured scenario at runtime. The
    /// fake is refused in Production, so this can never arm a live venue.
    /// </summary>
    internal void SetScenario(string scenario)
        => options.Value.Fake.Scenario = scenario;

    internal int StoredOrderCount => _orders.Count;

    internal int SubmitCount(string ourReference)
        => _orders.TryGetValue(ourReference, out _) ? 1 : 0;

    private static GatewayFillTerms ExactFill(GatewaySubmitRequest request)
        => new(
            request.Quantity,
            request.Price,
            request.GrossAmount,
            request.GatewayFee,
            GatewayTotal(request),
            "fake-chain",
            $"fake-settle-{request.OurReference}");

    private static GatewayFillTerms MismatchedFill(GatewaySubmitRequest request)
        => new(
            request.Quantity,
            request.Price + 0.01m,
            request.GrossAmount,
            request.GatewayFee,
            GatewayTotal(request),
            "fake-chain",
            $"fake-settle-{request.OurReference}");

    private static decimal GatewayTotal(GatewaySubmitRequest request)
        => request.Side == OrderSide.Buy
            ? TradingFeeMath.RoundMoney(request.GrossAmount + request.GatewayFee)
            : TradingFeeMath.RoundMoney(request.GrossAmount - request.GatewayFee);

    private static decimal RoundToDecimals(decimal value, int decimals)
        => decimal.Round(value, decimals, MidpointRounding.ToZero);

    private static decimal FloorToDecimals(decimal value, int decimals)
    {
        var factor = (decimal)Math.Pow(10, decimals);
        return decimal.Floor(value * factor) / factor;
    }

    private static string QuoteIdFor(GatewayQuoteRequest request, decimal price, decimal quantity)
    {
        var canonical = string.Join(
            '|',
            request.Token.TokenId.ToString("D"),
            request.Side == OrderSide.Buy ? "buy" : "sell",
            request.Amount?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.Quantity?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            price.ToString(System.Globalization.CultureInfo.InvariantCulture),
            quantity.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(canonical)))[..16].ToLowerInvariant();
        return $"fakeq-{hash}";
    }

    private sealed record StoredOrder(
        GatewaySubmitRequest Request,
        DateTimeOffset SubmittedAt,
        string Scenario);
}
