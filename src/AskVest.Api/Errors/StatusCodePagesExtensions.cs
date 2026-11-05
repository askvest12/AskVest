using AskVest.SharedKernel;

namespace AskVest.Api;

internal static class StatusCodePagesExtensions
{
    public static IApplicationBuilder UseApiStatusCodePages(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            await next(context);
            if (context.Response.StatusCode is < 400 or > 599
                || context.Response.ContentLength is > 0
                || context.Response.HasStarted)
            {
                return;
            }

            var (code, message) = context.Response.StatusCode switch
            {
                401 => (ErrorCodes.AuthUnauthenticated, "Authentication is required."),
                403 => (ErrorCodes.AuthForbidden, "Access is denied."),
                404 => (ErrorCodes.RouteNotFound, "The route was not found."),
                405 => (ErrorCodes.RouteMethodNotAllowed, "The method is not allowed."),
                408 => (ErrorCodes.RequestTimeout, "The request timed out."),
                413 => (ErrorCodes.RequestTooLarge, "The request body is too large."),
                415 => (ErrorCodes.RequestUnsupportedMediaType, "The content type is not supported."),
                429 => (ErrorCodes.RateLimitExceeded, "Too many requests."),
                504 => (ErrorCodes.RequestTimeout, "The request timed out."),
                _ => (ErrorCodes.ServerUnexpected, "An unexpected error occurred."),
            };
            if (code == ErrorCodes.ServerUnexpected && context.Response.StatusCode < 500)
            {
                return;
            }

            await ApiResults.WriteAsync(context, context.Response.StatusCode, message, null, new ApiError(code));
        });
    }
}
