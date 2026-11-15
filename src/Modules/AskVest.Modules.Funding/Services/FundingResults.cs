using AskVest.SharedKernel;
using Microsoft.AspNetCore.Http;

namespace AskVest.Modules.Funding.Services;

internal static class FundingResults
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

    public static Error NotAvailable(string reason)
        => new(
            ErrorCodes.FundingNotAvailable,
            $"Funding is not available: {reason}.",
            ErrorType.BusinessRule);

    public static Error LimitExceeded(string reason)
        => new(
            ErrorCodes.FundingLimitExceeded,
            $"The funding limit was exceeded: {reason}.",
            ErrorType.BusinessRule);

    public static Error BeneficiaryInvalid(string reason)
        => new(
            ErrorCodes.FundingBeneficiaryInvalid,
            $"The beneficiary is not valid: {reason}.",
            ErrorType.BusinessRule);

    public static Error BeneficiaryCoolingOff(DateTimeOffset activeFrom)
        => new(
            ErrorCodes.FundingBeneficiaryCoolingOff,
            $"The beneficiary is still cooling off until {activeFrom:O}.",
            ErrorType.BusinessRule);

    public static Error QuoteInvalid(string reason)
        => new(
            ErrorCodes.FundingQuoteInvalid,
            $"The funding quote cannot be used: {reason}.",
            ErrorType.Conflict);

    public static Error QuoteUnavailable(string reason)
        => new(
            ErrorCodes.FundingQuoteUnavailable,
            $"Funding quotes are unavailable: {reason}.",
            ErrorType.Unavailable);

    public static Error Unavailable(string reason)
        => new(
            ErrorCodes.FundingUnavailable,
            $"Funding is temporarily unavailable: {reason}.",
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
        ErrorType.BusinessRule => StatusCodes.Status422UnprocessableEntity,
        ErrorType.RateLimited => StatusCodes.Status429TooManyRequests,
        ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest,
    };
}
