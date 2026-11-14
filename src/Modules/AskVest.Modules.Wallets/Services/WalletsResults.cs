using AskVest.SharedKernel;
using Microsoft.AspNetCore.Http;

namespace AskVest.Modules.Wallets.Services;

internal static class WalletsResults
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

    public static Error WalletNotAvailable(string reason)
        => new(
            ErrorCodes.WalletNotAvailable,
            $"Wallets are not available: {reason}.",
            ErrorType.BusinessRule);

    public static Error AddressInvalid(string reason)
        => new(
            ErrorCodes.WalletAddressInvalid,
            $"The address is not valid: {reason}.",
            ErrorType.Validation);

    public static Error CoolingOff()
        => new(
            ErrorCodes.WalletAddressCoolingOff,
            "The address is still in its cooling-off period.",
            ErrorType.Conflict);

    public static Error LimitExceeded(string reason)
        => new(
            ErrorCodes.WalletLimitExceeded,
            $"The withdrawal limit was exceeded: {reason}.",
            ErrorType.BusinessRule);

    public static Error PreviewInvalid(string reason)
        => new(
            ErrorCodes.WalletPreviewInvalid,
            $"The withdrawal preview cannot be used: {reason}.",
            ErrorType.Conflict);

    public static Error Unavailable()
        => new(
            ErrorCodes.WalletUnavailable,
            "Wallets are temporarily unavailable.",
            ErrorType.Unavailable);

    public static Error NotFound(string what)
        => new(
            ErrorCodes.ResourceNotFound,
            $"The {what} was not found.",
            ErrorType.NotFound);

    public static string? CorrelationId(HttpContext context)
        => context.Items.TryGetValue(CorrelationIdItemKey, out var value) && value is string id
            ? id
            : null;

    private static int StatusFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthenticated => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest,
    };
}
