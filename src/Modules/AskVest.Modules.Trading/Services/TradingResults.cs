using AskVest.Modules.Trading.Domain;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.Http;

namespace AskVest.Modules.Trading.Services;

internal static class TradingResults
{
    private const string CorrelationIdItemKey = "AskVest.CorrelationId";

    public static IResult Ok<TData>(TData data, string message, HttpContext httpContext)
    {
        var payload = new
        {
            success = true,
            message,
            data,
            error = (object?)null,
            correlationId = CorrelationId(httpContext),
        };
        return Results.Json(payload);
    }

    public static IResult FromResult<TData>(Result<TData> result, HttpContext httpContext, string successMessage)
    {
        if (result.IsSuccess)
        {
            return Ok(result.Value, successMessage, httpContext);
        }

        return Error(result.Error!, httpContext);
    }

    public static IResult Error(Error error, HttpContext httpContext, int? statusOverride = null)
    {
        var status = statusOverride ?? StatusFor(error.Type);
        var payload = new
        {
            success = false,
            message = error.Message,
            data = (object?)null,
            error = new
            {
                code = error.Code,
                details = error.Details?.Select(d => new { field = d.Field, message = d.Message }).ToList(),
            },
            correlationId = CorrelationId(httpContext),
        };
        return Results.Json(payload, statusCode: status);
    }

    public static Error NotAvailable(string reason, params ValidationFailure[] details)
        => new(
            ErrorCodes.TradingNotAvailable,
            $"Trading is not available: {reason}.",
            ErrorType.BusinessRule,
            details);

    public static Error OrderInvalid(string reason)
        => new(
            ErrorCodes.TradingOrderInvalid,
            $"The order is not valid: {reason}.",
            ErrorType.BusinessRule,
            [new ValidationFailure("reason", reason)]);

    public static Error QuoteUnavailable()
        => new(
            ErrorCodes.TradingQuoteUnavailable,
            "A firm quote is not available right now.",
            ErrorType.Unavailable);

    public static Error QuoteInvalid(string reason)
        => new(
            ErrorCodes.TradingQuoteInvalid,
            $"The quote cannot be used: {reason}.",
            ErrorType.Conflict,
            [new ValidationFailure("reason", reason)]);

    public static string? CorrelationId(HttpContext context)
        => context.Items.TryGetValue(CorrelationIdItemKey, out var value) && value is string id
            ? id
            : context.TraceIdentifier;

    private static int StatusFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthenticated => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.BusinessRule => StatusCodes.Status422UnprocessableEntity,
        ErrorType.RateLimited => StatusCodes.Status429TooManyRequests,
        ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };
}
