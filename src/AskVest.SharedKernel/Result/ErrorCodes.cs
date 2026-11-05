namespace AskVest.SharedKernel;

public static class ErrorCodes
{
    public const string ValidationFailed = "validation.failed";
    public const string RequestMalformed = "request.malformed";
    public const string RequestTooLarge = "request.too_large";
    public const string RequestUnsupportedMediaType = "request.unsupported_media_type";
    public const string RequestTimeout = "request.timeout";
    public const string RouteNotFound = "route.not_found";
    public const string RouteMethodNotAllowed = "route.method_not_allowed";
    public const string ResourceNotFound = "resource.not_found";
    public const string ResourceConflict = "resource.conflict";
    public const string BusinessRuleViolated = "business.rule_violated";
    public const string AuthUnauthenticated = "auth.unauthenticated";
    public const string AuthForbidden = "auth.forbidden";
    public const string IdempotencyKeyMissing = "idempotency.key_missing";
    public const string IdempotencyKeyInvalid = "idempotency.key_invalid";
    public const string IdempotencyKeyReused = "idempotency.key_reused";
    public const string IdempotencyRequestInProgress = "idempotency.request_in_progress";
    public const string IdempotencyReplayUnavailable = "idempotency.replay_unavailable";
    public const string RateLimitExceeded = "rate_limit.exceeded";
    public const string ServiceUnavailable = "service.unavailable";
    public const string ServerUnexpected = "server.unexpected";
}
