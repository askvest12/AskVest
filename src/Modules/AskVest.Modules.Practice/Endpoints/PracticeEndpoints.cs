using System.Globalization;
using AskVest.Modules.Compliance.Contracts;
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

    internal static string Money(decimal value)
        => value.ToString("0.############################", Invariant);

    internal static string Quantity(decimal value)
        => value.ToString("0.############################", Invariant);

    internal static string Money2(decimal value)
        => value.ToString("0.00", Invariant);

    private static Guid CurrentUserId(HttpContext httpContext)
        => Guid.Parse(httpContext.User.FindFirst("sub")!.Value);
}
