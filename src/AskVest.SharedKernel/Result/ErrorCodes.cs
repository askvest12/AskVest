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
    public const string AuthInvalidCredentials = "auth.invalid_credentials";
    public const string AuthEmailNotVerified = "auth.email_not_verified";
    public const string AuthAccountDisabled = "auth.account_disabled";
    public const string AuthAccountPendingDeletion = "auth.account_pending_deletion";
    public const string AuthInvalidCode = "auth.invalid_code";
    public const string AuthTokenExpired = "auth.token_expired";
    public const string AuthSessionRevoked = "auth.session_revoked";
    public const string AuthRefreshInvalid = "auth.refresh_invalid";
    public const string AuthRefreshConflict = "auth.refresh_conflict";
    public const string AuthPinNotSet = "auth.pin_not_set";
    public const string AuthPinInvalid = "auth.pin_invalid";
    public const string AuthPinLocked = "auth.pin_locked";
    public const string AuthStepUpInvalid = "auth.step_up_invalid";
    public const string AccountDeletionBlocked = "account.deletion_blocked";
    public const string IdempotencyKeyMissing = "idempotency.key_missing";
    public const string IdempotencyKeyInvalid = "idempotency.key_invalid";
    public const string IdempotencyKeyReused = "idempotency.key_reused";
    public const string IdempotencyRequestInProgress = "idempotency.request_in_progress";
    public const string IdempotencyReplayUnavailable = "idempotency.replay_unavailable";
    public const string RateLimitExceeded = "rate_limit.exceeded";
    public const string ServiceUnavailable = "service.unavailable";
    public const string ComplianceNotEligible = "compliance.not_eligible";
    public const string ComplianceConsentVersionMismatch = "compliance.consent_version_mismatch";
    public const string ComplianceKycAttemptsExhausted = "compliance.kyc_attempts_exhausted";
    public const string WebhookInvalidSignature = "webhook.invalid_signature";
    public const string WebhookStaleTimestamp = "webhook.stale_timestamp";
    public const string ServerUnexpected = "server.unexpected";
}
