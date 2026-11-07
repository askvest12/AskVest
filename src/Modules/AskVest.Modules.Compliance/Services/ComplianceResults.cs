using AskVest.SharedKernel;
using Microsoft.AspNetCore.Http;

namespace AskVest.Modules.Compliance.Services;

/// <summary>
/// Writes the standard response envelope for Compliance endpoints. Mirrors the
/// host envelope; modules cannot reference the host project.
/// </summary>
internal static class ComplianceResults
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
}
