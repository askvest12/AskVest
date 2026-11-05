using AskVest.SharedKernel;
using Microsoft.AspNetCore.Diagnostics;

namespace AskVest.Api;

internal sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        var correlationId = CorrelationIdMiddleware.Get(httpContext);
        logger.LogError(exception, "Unhandled exception {CorrelationId}", correlationId);

        if (exception is BadHttpRequestException badRequest)
        {
            var (status, code, message) = badRequest.StatusCode switch
            {
                StatusCodes.Status413PayloadTooLarge => (413, ErrorCodes.RequestTooLarge, "The request body is too large."),
                StatusCodes.Status415UnsupportedMediaType => (415, ErrorCodes.RequestUnsupportedMediaType, "The content type is not supported."),
                _ => (400, ErrorCodes.RequestMalformed, "The request could not be parsed."),
            };
            await ApiResults.WriteAsync(httpContext, status, message, null, new ApiError(code));
            return true;
        }

        await ApiResults.WriteAsync(
            httpContext,
            StatusCodes.Status500InternalServerError,
            "An unexpected error occurred.",
            null,
            new ApiError(ErrorCodes.ServerUnexpected));
        return true;
    }
}
