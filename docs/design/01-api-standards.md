# 01. API Standards

## Response envelope

Every response, including framework errors, uses this shape. `success`, `message`, `data`, `error` are the contract fields; `correlationId` is added for support tracing.

```json
{
  "success": true,
  "message": "Service information retrieved.",
  "data": { },
  "error": null,
  "correlationId": "0194f0a8c3b07c4e9a1d5e2b7f6c3a10"
}
```

```json
{
  "success": false,
  "message": "The request is not valid.",
  "data": null,
  "error": {
    "code": "validation.failed",
    "details": [ { "field": "amount", "message": "Amount must be greater than zero." } ]
  },
  "correlationId": "0194f0a8c3b07c4e9a1d5e2b7f6c3a11"
}
```

`message` is human readable and safe to show. Clients branch on `error.code`, never on `message`. No 204 responses: success with nothing to return is 200 with `data: null`.

## Error code catalogue

Pattern `^[a-z]+(_[a-z]+)*(\.[a-z]+(_[a-z]+)*)+$`.

| Code | HTTP | Meaning |
|---|---|---|
| `validation.failed` | 400 | Request failed validation; `details` lists fields |
| `request.malformed` | 400 | Body is not valid JSON or not readable |
| `request.too_large` | 413 | Body exceeds the size limit |
| `request.unsupported_media_type` | 415 | Content type is not supported |
| `request.timeout` | 504 | Request exceeded the time limit |
| `route.not_found` | 404 | No route matched |
| `route.method_not_allowed` | 405 | Route exists, method does not |
| `resource.not_found` | 404 | Resource does not exist or is not visible |
| `resource.conflict` | 409 | State conflict (for example unique violation) |
| `business.rule_violated` | 422 | Domain rule rejected the command |
| `auth.unauthenticated` | 401 | No valid credentials (Phase 1) |
| `auth.forbidden` | 403 | Authenticated but not allowed (Phase 1) |
| `auth.invalid_credentials` | 401 | Wrong email or password, or unknown email (identical) |
| `auth.email_not_verified` | 403 | Password correct, email not yet verified |
| `auth.account_disabled` | 403 | Account disabled |
| `auth.account_pending_deletion` | 403 | Deletion cooling-off in progress |
| `auth.invalid_code` | 400 | One-time code wrong, expired, consumed or exhausted |
| `auth.token_expired` | 401 | Access token past its lifetime |
| `auth.session_revoked` | 401 | Session revoked or expired |
| `auth.refresh_invalid` | 401 | Refresh token unknown, expired, rotated (theft) or session dead |
| `auth.refresh_conflict` | 409 | Rotated refresh token replayed inside grace window |
| `auth.pin_not_set` | 422 | No PIN configured for this account |
| `auth.pin_invalid` | 422 | PIN wrong |
| `auth.pin_locked` | 429 | PIN locked, `Retry-After` set |
| `auth.step_up_invalid` | 403 | Step-up grant missing, expired, consumed, wrong purpose, session or binding |
| `account.deletion_blocked` | 422 | A registered blocker prevents deletion, reasons in details |
| `compliance.not_eligible` | 403 | Practice or Real gate failed; one detail per blocker reason code |
| `compliance.consent_version_mismatch` | 409 | Consent request does not match current document versions |
| `compliance.kyc_attempts_exhausted` | 409 | KYC attempt limit reached |
| `webhook.invalid_signature` | 401 | Webhook signature verification failed |
| `webhook.stale_timestamp` | 400 | Webhook timestamp missing or outside tolerance |
| `idempotency.key_missing` | 400 | State-changing call without `Idempotency-Key` |
| `idempotency.key_invalid` | 400 | Key fails the format rule |
| `idempotency.key_reused` | 422 | Same key, different request fingerprint |
| `idempotency.request_in_progress` | 409 | Same key is being processed; retry later |
| `idempotency.replay_unavailable` | 409 | Stored response had no body; read the resource |
| `rate_limit.exceeded` | 429 | Too many requests; see `Retry-After` |
| `service.unavailable` | 503 | Dependency unavailable, fail closed |
| `server.unexpected` | 500 | Unhandled error; generic message only |

## Versioning

Version in the path (`/api/v1`). No version library. Breaking changes require `/api/v2`.

## JSON rules

- camelCase property names.
- Enums serialize as strings.
- `decimal` serializes as strings; a JSON number for a decimal field is rejected.
- Timestamps are ISO 8601 UTC with `Z`.
- Unknown request properties are rejected (`UnmappedMemberHandling.Disallow`).
- Duplicate JSON properties are rejected.
- Request body limit: 1 MB.

## Authentication

Bearer JWT (ES256) in the `Authorization` header. Missing or invalid credentials return the standard 401 envelope with `WWW-Authenticate: Bearer`. Tokens in query strings are never accepted. Authenticated endpoints resolve the session by primary key on every request; revocation is immediate.

## Pagination

Cursor based. `limit` defaults to 20, maximum 100. Page shape:

```json
{ "items": [], "page": { "nextCursor": null, "hasMore": false, "limit": 20 } }
```

Cursors are opaque to clients (base64url payloads, see `CursorCodec` in SharedKernel).

## Idempotency contract

Header `Idempotency-Key` (8 to 128 chars of `[A-Za-z0-9_.:-]`). Opt in with `.RequireIdempotency()`, opt out with `.ExemptFromIdempotency("reason")`. Full algorithm in Step 8 of the phase plan and ADR 0005. Replays return `Idempotency-Replayed: true`.

## Rate limit contract

429 with `error.code = rate_limit.exceeded` and a `Retry-After` header in seconds.

## Headers

- `X-Correlation-Id`: accepted inbound only if it matches `^[A-Za-z0-9._-]{8,64}$`, echoed outbound always.
- `Idempotency-Key`: inbound on state-changing endpoints.
- `Idempotency-Replayed: true`: outbound on replays.

## OpenAPI and Scalar

Every endpoint documents success type `ApiResponse<T>` and the standard error statuses. Idempotent endpoints document the `Idempotency-Key` header via `.WithStandardErrors()`. Docs are configuration-gated and off in Production.
