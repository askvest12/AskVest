using AskVest.SharedKernel;

namespace AskVest.Api;

internal static class ErrorCatalogue
{
    private static readonly IReadOnlyDictionary<string, (int Status, ErrorType Type, string Message)> _entries =
        new Dictionary<string, (int, ErrorType, string)>(StringComparer.Ordinal)
        {
            [ErrorCodes.ValidationFailed] = (400, ErrorType.Validation, "The request is not valid."),
            [ErrorCodes.RequestMalformed] = (400, ErrorType.Validation, "The request body is not valid."),
            [ErrorCodes.RequestTooLarge] = (413, ErrorType.Validation, "The request body is too large."),
            [ErrorCodes.RequestUnsupportedMediaType] = (415, ErrorType.Validation, "The content type is not supported."),
            [ErrorCodes.RequestTimeout] = (408, ErrorType.Failure, "The request timed out."),
            [ErrorCodes.RouteNotFound] = (404, ErrorType.NotFound, "The route was not found."),
            [ErrorCodes.RouteMethodNotAllowed] = (405, ErrorType.NotFound, "The method is not allowed."),
            [ErrorCodes.ResourceNotFound] = (404, ErrorType.NotFound, "The resource was not found."),
            [ErrorCodes.ResourceConflict] = (409, ErrorType.Conflict, "The request conflicts with current state."),
            [ErrorCodes.BusinessRuleViolated] = (422, ErrorType.BusinessRule, "A business rule rejected the request."),
            [ErrorCodes.AuthUnauthenticated] = (401, ErrorType.Unauthenticated, "Authentication is required."),
            [ErrorCodes.AuthForbidden] = (403, ErrorType.Forbidden, "Access is denied."),
            [ErrorCodes.AuthInvalidCredentials] = (401, ErrorType.Unauthenticated, "The email or password is incorrect."),
            [ErrorCodes.AuthEmailNotVerified] = (403, ErrorType.Forbidden, "The email address is not verified."),
            [ErrorCodes.AuthAccountDisabled] = (403, ErrorType.Forbidden, "The account is disabled."),
            [ErrorCodes.AuthAccountPendingDeletion] = (403, ErrorType.Forbidden, "The account is pending deletion."),
            [ErrorCodes.AuthInvalidCode] = (400, ErrorType.Validation, "The code is invalid, expired or already used."),
            [ErrorCodes.AuthTokenExpired] = (401, ErrorType.Unauthenticated, "The access token has expired."),
            [ErrorCodes.AuthSessionRevoked] = (401, ErrorType.Unauthenticated, "The session has been revoked."),
            [ErrorCodes.AuthRefreshInvalid] = (401, ErrorType.Unauthenticated, "The refresh token is invalid."),
            [ErrorCodes.AuthRefreshConflict] = (409, ErrorType.Conflict, "The refresh token was already rotated."),
            [ErrorCodes.AuthPinNotSet] = (422, ErrorType.BusinessRule, "No PIN is configured for this account."),
            [ErrorCodes.AuthPinInvalid] = (422, ErrorType.BusinessRule, "The PIN is incorrect."),
            [ErrorCodes.AuthPinLocked] = (429, ErrorType.RateLimited, "The PIN is temporarily locked."),
            [ErrorCodes.AuthStepUpInvalid] = (403, ErrorType.Forbidden, "The step-up authorization is invalid."),
            [ErrorCodes.AccountDeletionBlocked] = (422, ErrorType.BusinessRule, "Account deletion is blocked."),
            [ErrorCodes.IdempotencyKeyMissing] = (400, ErrorType.Validation, "The Idempotency-Key header is required."),
            [ErrorCodes.IdempotencyKeyInvalid] = (400, ErrorType.Validation, "The Idempotency-Key header is invalid."),
            [ErrorCodes.IdempotencyKeyReused] = (422, ErrorType.Conflict, "The Idempotency-Key was already used with a different request."),
            [ErrorCodes.IdempotencyRequestInProgress] = (409, ErrorType.Conflict, "A request with this Idempotency-Key is still in progress."),
            [ErrorCodes.IdempotencyReplayUnavailable] = (409, ErrorType.Conflict, "The stored response for this Idempotency-Key is unavailable."),
            [ErrorCodes.RateLimitExceeded] = (429, ErrorType.RateLimited, "Too many requests."),
            [ErrorCodes.ServiceUnavailable] = (503, ErrorType.Unavailable, "The service is temporarily unavailable."),
            [ErrorCodes.ServerUnexpected] = (500, ErrorType.Failure, "An unexpected error occurred."),
        };

    public static bool TryGet(string code, out int status, out ErrorType type, out string message)
    {
        if (_entries.TryGetValue(code, out var entry))
        {
            (status, type, message) = entry;
            return true;
        }

        status = 500;
        type = ErrorType.Failure;
        message = "An unexpected error occurred.";
        return false;
    }

    public static IEnumerable<string> AllCodes => _entries.Keys;
}
