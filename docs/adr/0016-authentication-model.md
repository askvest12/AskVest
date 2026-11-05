# ADR 0016: Authentication model

Status: accepted (Phase 1)

## Decision

Email plus password login. Short-lived ES256 JWT access tokens (10 minutes) bound to server-side sessions validated by primary-key lookup on every request. Opaque rotating refresh tokens (30 day idle, 90 day absolute) stored hashed. Unknown devices require an email one-time code before a session is issued.

## Consequences

Instant revocation without a cache. Two signing keys supported for rotation. Session validation is one indexed read per request; revisit with measurements in Phase 17.
