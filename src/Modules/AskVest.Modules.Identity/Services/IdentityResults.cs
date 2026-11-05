using System.Text.Json;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Identity.Services;

internal static class IdentityResults
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

    public static IResult FromResult(Result result, HttpContext httpContext, string successMessage, TimeSpan? retryAfter = null)
    {
        if (result.IsSuccess)
        {
            return Ok<object?>(null, successMessage, httpContext);
        }

        return Error(result.Error!, httpContext, retryAfter);
    }

    public static IResult FromResult<TData>(Result<TData> result, HttpContext httpContext, string successMessage)
    {
        if (result.IsSuccess)
        {
            return Ok(result.Value, successMessage, httpContext);
        }

        return Error(result.Error!, httpContext);
    }

    public static IResult Error(Error error, HttpContext httpContext, TimeSpan? retryAfter = null)
    {
        var status = StatusFor(error.Type);
        var payload = new
        {
            success = false,
            message = MessageFor(error.Type),
            data = (object?)null,
            error = new
            {
                code = error.Code,
                details = error.Details?.Select(d => new { field = d.Field, message = d.Message }).ToList(),
            },
            correlationId = CorrelationId(httpContext),
        };
        var result = Results.Json(payload, statusCode: status);
        if (retryAfter is { } delay)
        {
            httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(delay.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return result;
    }

    private static string CorrelationId(HttpContext context)
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

    private static string MessageFor(ErrorType type) => type switch
    {
        ErrorType.Validation => "The request is not valid.",
        ErrorType.NotFound => "The resource was not found.",
        ErrorType.Conflict => "The request conflicts with current state.",
        ErrorType.BusinessRule => "A business rule rejected the request.",
        ErrorType.RateLimited => "Too many requests.",
        ErrorType.Unauthenticated => "Authentication is required.",
        ErrorType.Forbidden => "Access is denied.",
        ErrorType.Unavailable => "The service is temporarily unavailable.",
        _ => "An unexpected error occurred.",
    };
}
