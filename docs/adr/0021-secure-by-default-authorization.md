# ADR 0021: Secure-by-default authorization and secrets in idempotency

Status: accepted (Phase 1)

## Decision

A fallback authorization policy requires an authenticated active account for every endpoint. Anonymous routes exist only on an explicit allow-list guarded by an architecture test. Endpoints returning secrets carry a returns-secrets tag, are exempt from idempotency replay storage, and may never also require idempotency.

## Consequences

New endpoints are protected by default; forgetting authorization fails closed. Response bodies containing tokens, codes or grants are never persisted by the idempotency store.
