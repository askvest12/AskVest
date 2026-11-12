using System.Globalization;
using AskVest.Modules.Compliance.Contracts;
using AskVest.Modules.Trading.Endpoints;
using AskVest.Modules.Trading.Persistence.Entities;
using AskVest.Modules.Trading.Services;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Trading.Endpoints;

public static class TradingEndpoints
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly string[] GatewayScenarios =
    [
        "normal",
        "quote_error",
        "rejected",
        "slow_submit",
        "submit_timeout_lost",
        "submit_timeout_filled",
        "delayed_fill",
        "fill_mismatch",
        "status_unavailable",
    ];

    public static void Map(RouteGroupBuilder api, bool probesEnabled = false)
    {
        if (probesEnabled)
        {
            api.MapPost("/diagnostics/trading-scenario", ScenarioProbeAsync)
                .WithName("TradingScenarioProbe")
                .WithSummary("Diagnostic: set the fake issuer gateway scenario.")
                .WithTags("Diagnostics")
                .WithMetadata(new RequiresIdempotencyAttribute());
        }

        var real = api.MapGroup("/real").RequireAuthorization(CompliancePolicyNames.RealEligible);

        real.MapPost("/quotes", CreateQuoteAsync)
            .WithName("CreateQuote")
            .WithSummary("Request a firm quote for a real order.")
            .WithTags("Real Trading")
            .WithMetadata(new RequiresIdempotencyAttribute());

        real.MapGet("/quotes/{quoteId:guid}", GetQuoteAsync)
            .WithName("GetQuote")
            .WithSummary("Read one of the caller's quotes.")
            .WithTags("Real Trading");

        real.MapPost("/orders", PlaceOrderAsync)
            .WithName("PlaceOrder")
            .WithSummary("Place a real order against a firm quote and step-up grant.")
            .WithTags("Real Trading")
            .WithMetadata(new RequiresIdempotencyAttribute());

        real.MapGet("/orders", ListOrdersAsync)
            .WithName("ListOrders")
            .WithSummary("The caller's real orders, newest first.")
            .WithTags("Real Trading");

        real.MapGet("/orders/{orderId:guid}", GetOrderAsync)
            .WithName("GetOrder")
            .WithSummary("One of the caller's real orders.")
            .WithTags("Real Trading");

        real.MapPost("/orders/{orderId:guid}/cancel", CancelOrderAsync)
            .WithName("CancelOrder")
            .WithSummary("Cancel a real order while cancellation is still legal.")
            .WithTags("Real Trading")
            .WithMetadata(new RequiresIdempotencyAttribute());
    }

    private static async Task<IResult> CreateQuoteAsync(
        QuoteRequest request,
        HttpContext httpContext,
        QuoteService quotes,
        CancellationToken cancellationToken)
    {
        var result = await quotes.CreateAsync(CurrentUserId(httpContext), request, cancellationToken);
        return result.IsFailure
            ? TradingResults.Error(result.Error!, httpContext)
            : TradingResults.Ok(ToResponse(result.Value), "Quote created.", httpContext);
    }

    private static async Task<IResult> GetQuoteAsync(
        Guid quoteId,
        HttpContext httpContext,
        QuoteService quotes,
        CancellationToken cancellationToken)
    {
        var result = await quotes.GetAsync(CurrentUserId(httpContext), quoteId, cancellationToken);
        return result.IsFailure
            ? TradingResults.Error(result.Error!, httpContext)
            : TradingResults.Ok(ToResponse(result.Value), "Quote loaded.", httpContext);
    }

    private static async Task<IResult> PlaceOrderAsync(
        OrderRequest request,
        HttpContext httpContext,
        OrderService orders,
        CancellationToken cancellationToken)
    {
        var sessionId = SessionId(httpContext);
        if (sessionId is null)
        {
            return TradingResults.Error(new Error(
                ErrorCodes.AuthUnauthenticated,
                "The session is not authenticated.",
                ErrorType.Unauthenticated), httpContext);
        }

        var idempotencyKey = httpContext.Request.Headers["Idempotency-Key"].ToString();
        var result = await orders.PlaceAsync(
            CurrentUserId(httpContext),
            sessionId.Value,
            request,
            idempotencyKey,
            TradingResults.CorrelationId(httpContext),
            cancellationToken);
        return result.IsFailure
            ? TradingResults.Error(result.Error!, httpContext)
            : TradingResults.Ok(ToResponse(result.Value), "Order placed.", httpContext);
    }

    private static async Task<IResult> ListOrdersAsync(
        HttpContext httpContext,
        OrderService orders,
        int? limit,
        CancellationToken cancellationToken)
    {
        var result = await orders.ListAsync(
            CurrentUserId(httpContext), limit ?? 50, cancellationToken);
        return result.IsFailure
            ? TradingResults.Error(result.Error!, httpContext)
            : TradingResults.Ok(
                result.Value.Select(ToResponse).ToArray(), "Orders loaded.", httpContext);
    }

    private static async Task<IResult> GetOrderAsync(
        Guid orderId,
        HttpContext httpContext,
        OrderService orders,
        CancellationToken cancellationToken)
    {
        var result = await orders.GetAsync(CurrentUserId(httpContext), orderId, cancellationToken);
        return result.IsFailure
            ? TradingResults.Error(result.Error!, httpContext)
            : TradingResults.Ok(ToResponse(result.Value), "Order loaded.", httpContext);
    }

    private static async Task<IResult> CancelOrderAsync(
        Guid orderId,
        HttpContext httpContext,
        OrderService orders,
        CancellationToken cancellationToken)
    {
        var result = await orders.CancelAsync(
            CurrentUserId(httpContext),
            orderId,
            TradingResults.CorrelationId(httpContext),
            cancellationToken);
        return result.IsFailure
            ? TradingResults.Error(result.Error!, httpContext)
            : TradingResults.Ok(ToResponse(result.Value), "Order cancelled.", httpContext);
    }

    private static object ToResponse(TradingOrder order)
        => new
        {
            id = order.Id,
            quoteId = order.QuoteId,
            tokenId = order.TokenId,
            instrumentId = order.InstrumentId,
            side = TradingText.ToDb(order.Side),
            state = TradingText.ToDb(order.State),
            settlementAsset = order.SettlementAsset,
            quantity = Money(order.Quantity),
            price = Money(order.Price),
            grossAmount = Money(order.GrossAmount),
            ourFee = Money(order.OurFee),
            gatewayFee = Money(order.GatewayFee),
            total = Money(order.Total),
            gatewayReference = order.GatewayOrderReference,
            failureCode = order.FailureCode,
            detailCode = order.DetailCode,
            createdAt = order.CreatedAt,
            submittedAt = order.SubmittedAt,
            executedAt = order.ExecutedAt,
            failedAt = order.FailedAt,
            cancelledAt = order.CancelledAt,
        };

    private static Guid? SessionId(HttpContext httpContext)
        => Guid.TryParse(httpContext.User.FindFirst("sid")?.Value, out var sessionId)
            ? sessionId
            : null;

    private static object ToResponse(TradingQuote quote)
        => new
        {
            id = quote.Id,
            tokenId = quote.TokenId,
            side = TradingText.ToDb(quote.Side),
            quantity = Money(quote.Quantity),
            price = Money(quote.Price),
            grossAmount = Money(quote.GrossAmount),
            ourFee = Money(quote.OurFee),
            gatewayFee = Money(quote.GatewayFee),
            total = Money(quote.Total),
            settlementAsset = quote.SettlementAsset,
            expiresAt = quote.ExpiresAt,
            warnings = quote.Warnings.Split('|', StringSplitOptions.RemoveEmptyEntries),
            termsHash = quote.TermsHash,
            state = TradingText.ToDb(quote.State),
            createdAt = quote.CreatedAt,
        };

    private static string Money(decimal value) => value.ToString(Invariant);

    private static async Task<IResult> ScenarioProbeAsync(
        HttpContext httpContext,
        ScenarioProbeRequest request,
        Gateway.FakeIssuerGateway gateway,
        CancellationToken cancellationToken)
    {
        var scenario = request.Scenario?.Trim() ?? string.Empty;
        if (!GatewayScenarios.Contains(scenario, StringComparer.Ordinal))
        {
            return TradingResults.Error(new Error(
                ErrorCodes.ValidationFailed,
                "The scenario is not recognized.",
                ErrorType.Validation), httpContext);
        }

        gateway.SetScenario(scenario);
        return TradingResults.Ok(new { applied = true }, "Scenario set.", httpContext);
    }

    private sealed record ScenarioProbeRequest(string? Scenario);

    private static Guid CurrentUserId(HttpContext httpContext)
        => Guid.Parse(httpContext.User.FindFirst("sub")!.Value);
}
