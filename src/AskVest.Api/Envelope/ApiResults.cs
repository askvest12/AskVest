using System.Text.Json;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace AskVest.Api;

internal static class ApiResults
{
    public static int StatusFor(ErrorType type) => type switch
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

    public static string MessageFor(ErrorType type) => type switch
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

    public static ApiError ToApiError(Error error) => new(
        error.Code,
        error.Details?.Select(detail => new ApiErrorDetail(detail.Field, detail.Message)).ToList());

    public static Task WriteAsync(
        HttpContext httpContext,
        int statusCode,
        string message,
        object? data,
        ApiError? error)
    {
        var correlationId = CorrelationIdMiddleware.Get(httpContext);
        var options = httpContext.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value;
        var payload = new ApiResponse<object?>(statusCode < 400, message, data, error, correlationId);
        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/json";
        return httpContext.Response.WriteAsync(JsonSerializer.Serialize(payload, options.SerializerOptions));
    }

    public static Task WriteErrorAsync(HttpContext httpContext, Error error, int? statusCode = null)
    {
        var status = statusCode ?? StatusFor(error.Type);
        var apiError = ToApiError(error);
        return WriteAsync(httpContext, status, MessageFor(error.Type), null, apiError);
    }

    public static IResult Ok<TData>(TData data, string message, HttpContext httpContext)
        => Results.Json(new ApiResponse<TData>(true, message, data, null, CorrelationIdMiddleware.Get(httpContext)));

    public static IResult Created<TData>(TData data, string message, string? location, HttpContext httpContext)
    {
        var result = Results.Json(
            new ApiResponse<TData>(true, message, data, null, CorrelationIdMiddleware.Get(httpContext)),
            statusCode: StatusCodes.Status201Created);
        if (location is not null)
        {
            httpContext.Response.Headers.Location = location;
        }

        return result;
    }

    public static IResult Accepted<TData>(TData data, string message, HttpContext httpContext)
        => Results.Json(
            new ApiResponse<TData>(true, message, data, null, CorrelationIdMiddleware.Get(httpContext)),
            statusCode: StatusCodes.Status202Accepted);

    public static IResult FromResult(Result result, HttpContext httpContext)
    {
        if (result.IsSuccess)
        {
            return Ok<object?>(null, "Request completed.", httpContext);
        }

        return Results.Json(
            new ApiResponse<object?>(false, MessageFor(result.Error!.Type), null, ToApiError(result.Error), CorrelationIdMiddleware.Get(httpContext)),
            statusCode: StatusFor(result.Error.Type));
    }

    public static IResult FromResult<TData>(Result<TData> result, HttpContext httpContext)
    {
        if (result.IsSuccess)
        {
            return Ok(result.Value, "Request completed.", httpContext);
        }

        return Results.Json(
            new ApiResponse<object?>(false, MessageFor(result.Error!.Type), null, ToApiError(result.Error), CorrelationIdMiddleware.Get(httpContext)),
            statusCode: StatusFor(result.Error.Type));
    }
}
