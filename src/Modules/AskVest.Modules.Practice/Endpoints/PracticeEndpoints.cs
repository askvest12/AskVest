using System.Globalization;
using AskVest.Modules.Compliance.Contracts;
using AskVest.Modules.Practice.Persistence.Entities;
using AskVest.Modules.Practice.Services;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Practice.Endpoints;

public static class PracticeEndpoints
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static void Map(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/practice").RequireAuthorization(CompliancePolicyNames.PracticeEligible);

        group.MapPost("/account", StartAsync)
            .WithName("StartPracticeAccount")
            .WithSummary("Start the practice account and grant virtual credits once.")
            .WithTags("Practice")
            .WithMetadata(new RequiresIdempotencyAttribute());

        group.MapGet("/account", GetAccountAsync)
            .WithName("GetPracticeAccount")
            .WithSummary("Practice account status, cash and reset policy state.")
            .WithTags("Practice");

        group.MapPost("/account/reset", ResetAsync)
            .WithName("ResetPracticeAccount")
            .WithSummary("Reset the practice account under the reset policy.")
            .WithTags("Practice")
            .WithMetadata(new RequiresIdempotencyAttribute());

        group.MapGet("/positions", GetPositionsAsync)
            .WithName("GetPracticePositions")
            .WithSummary("Practice positions with quantities, held amounts and display values.")
            .WithTags("Practice");

        group.MapPost("/orders", PlaceOrderAsync)
            .WithName("PlacePracticeOrder")
            .WithSummary("Place a practice market or stop-loss order.")
            .WithTags("Practice")
            .WithMetadata(new RequiresIdempotencyAttribute());

        group.MapGet("/orders", ListOrdersAsync)
            .WithName("ListPracticeOrders")
            .WithSummary("The caller's practice orders, newest first.")
            .WithTags("Practice");

        group.MapGet("/orders/{orderId:guid}", GetOrderAsync)
            .WithName("GetPracticeOrder")
            .WithSummary("One of the caller's practice orders.")
            .WithTags("Practice");

        group.MapPost("/orders/{orderId:guid}/cancel", CancelOrderAsync)
            .WithName("CancelPracticeOrder")
            .WithSummary("Cancel an open practice stop-loss order.")
            .WithTags("Practice")
            .WithMetadata(new RequiresIdempotencyAttribute());
    }

    private static async Task<IResult> StartAsync(
        HttpContext httpContext,
        PracticeAccountService accounts,
        CancellationToken cancellationToken)
    {
        var userId = CurrentUserId(httpContext);
        var started = await accounts.StartAsync(userId, PracticeResults.CorrelationId(httpContext), cancellationToken);
        if (started.IsFailure)
        {
            return PracticeResults.Error(started.Error!, httpContext);
        }

        var status = await accounts.GetStatusAsync(userId, cancellationToken);
        return AccountResult(status, httpContext, "Practice account ready.");
    }

    private static async Task<IResult> GetAccountAsync(
        HttpContext httpContext,
        PracticeAccountService accounts,
        CancellationToken cancellationToken)
    {
        var status = await accounts.GetStatusAsync(CurrentUserId(httpContext), cancellationToken);
        return AccountResult(status, httpContext, "Practice account loaded.");
    }

    private static async Task<IResult> ResetAsync(
        HttpContext httpContext,
        PracticeAccountService accounts,
        CancellationToken cancellationToken)
    {
        var status = await accounts.ResetAsync(
            CurrentUserId(httpContext), PracticeResults.CorrelationId(httpContext), cancellationToken);
        return AccountResult(status, httpContext, "Practice account reset.");
    }

    private static IResult AccountResult(
        Result<PracticeAccountStatus> status,
        HttpContext httpContext,
        string message)
    {
        if (status.IsFailure)
        {
            return PracticeResults.Error(status.Error!, httpContext);
        }

        var value = status.Value;
        return PracticeResults.Ok(
            new
            {
                started = value.Started,
                cash = Money(value.Cash),
                resetCount = value.ResetCount,
                resetsLeftInWindow = value.ResetsLeft,
                nextResetAt = value.NextResetAt,
            },
            message,
            httpContext);
    }

    private static async Task<IResult> GetPositionsAsync(
        HttpContext httpContext,
        PracticePositionService positions,
        CancellationToken cancellationToken)
    {
        var result = await positions.GetAsync(CurrentUserId(httpContext), cancellationToken);
        if (result.IsFailure)
        {
            return PracticeResults.Error(result.Error!, httpContext);
        }

        var data = new
        {
            cash = Money(result.Value.Cash),
            items = result.Value.Items.Select(item => new
            {
                instrumentId = item.InstrumentId,
                symbol = item.Symbol,
                name = item.Name,
                availableQuantity = Quantity(item.AvailableQuantity),
                heldQuantity = Quantity(item.HeldQuantity),
                price = item.Price is { } price ? Money(price) : null,
                priceLabel = item.PriceLabel,
                marketValue = item.MarketValue is { } value ? Money2(value) : null,
            }),
        };
        return PracticeResults.Ok(data, "Practice positions loaded.", httpContext);
    }

    private static async Task<IResult> PlaceOrderAsync(
        HttpContext httpContext,
        PracticeOrderRequest request,
        PracticeOrderService orders,
        CancellationToken cancellationToken)
    {
        var result = await orders.PlaceAsync(
            CurrentUserId(httpContext),
            request,
            httpContext.Request.Headers["Idempotency-Key"].FirstOrDefault() ?? string.Empty,
            PracticeResults.CorrelationId(httpContext),
            cancellationToken);
        return OrderResult(result, httpContext, "Practice order placed.");
    }

    private static async Task<IResult> ListOrdersAsync(
        HttpContext httpContext,
        PracticeOrderService orders,
        string? state,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken)
    {
        var page = await orders.ListAsync(
            CurrentUserId(httpContext),
            state,
            Math.Clamp(limit ?? PageRequest.DefaultLimit, 1, PageRequest.MaxLimit),
            cursor,
            cancellationToken);
        if (page.IsFailure)
        {
            return PracticeResults.Error(page.Error!, httpContext);
        }

        return PracticeResults.Ok(
            new
            {
                items = page.Value.Items.Select(OrderData),
                nextCursor = page.Value.NextCursor,
                hasMore = page.Value.HasMore,
                limit = page.Value.Limit,
            },
            "Practice orders loaded.",
            httpContext);
    }

    private static async Task<IResult> GetOrderAsync(
        HttpContext httpContext,
        PracticeOrderService orders,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var result = await orders.GetAsync(CurrentUserId(httpContext), orderId, cancellationToken);
        return OrderResult(result, httpContext, "Practice order loaded.");
    }

    private static async Task<IResult> CancelOrderAsync(
        HttpContext httpContext,
        PracticeOrderService orders,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var result = await orders.CancelAsync(
            CurrentUserId(httpContext), orderId, PracticeResults.CorrelationId(httpContext), cancellationToken);
        return OrderResult(result, httpContext, "Practice order cancelled.");
    }

    private static IResult OrderResult(
        Result<PracticeOrderItem> result,
        HttpContext httpContext,
        string message)
    {
        if (result.IsFailure)
        {
            return PracticeResults.Error(result.Error!, httpContext);
        }

        return PracticeResults.Ok(OrderData(result.Value), message, httpContext);
    }

    private static IResult OrderResult(
        Result<PracticeOrder> result,
        HttpContext httpContext,
        string message)
    {
        if (result.IsFailure)
        {
            return PracticeResults.Error(result.Error!, httpContext);
        }

        return PracticeResults.Ok(
            OrderData(PracticeOrderService.ToItem(result.Value)), message, httpContext);
    }

    private static object OrderData(PracticeOrderItem item)
        => new
        {
            id = item.Id,
            instrumentId = item.InstrumentId,
            side = item.Side,
            type = item.Type,
            state = item.State,
            requestedAmount = item.RequestedAmount is { } amount ? Money(amount) : null,
            requestedQuantity = item.RequestedQuantity is { } quantity ? Quantity(quantity) : null,
            sellAll = item.SellAll,
            stopPrice = item.StopPrice is { } stop ? Money(stop) : null,
            filledQuantity = item.FilledQuantity is { } filled ? Quantity(filled) : null,
            fillPrice = item.FillPrice is { } fill ? Money(fill) : null,
            cashAmount = item.CashAmount is { } cash ? Money(cash) : null,
            fee = item.Fee is { } fee ? Money(fee) : null,
            priceAsOf = item.PriceAsOf,
            priceLabel = item.PriceLabel,
            priceSession = item.PriceSession,
            cancelReason = item.CancelReason,
            createdAt = item.CreatedAt,
            filledAt = item.FilledAt,
            cancelledAt = item.CancelledAt,
        };

    internal static string Money(decimal value)
        => value.ToString("0.############################", Invariant);

    internal static string Quantity(decimal value)
        => value.ToString("0.############################", Invariant);

    internal static string Money2(decimal value)
        => value.ToString("0.00", Invariant);

    private static Guid CurrentUserId(HttpContext httpContext)
        => Guid.Parse(httpContext.User.FindFirst("sub")!.Value);
}
