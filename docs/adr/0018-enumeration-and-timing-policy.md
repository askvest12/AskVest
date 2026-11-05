# ADR 0018: Enumeration and timing policy

Status: accepted (Phase 1)

## Decision

Sign-up, resend, forgot-password and login return uniform responses and perform comparable work whether or not the account exists. Unknown emails cost one dummy password verification. Throttling applies to unknown emails exactly as to real ones.

## Consequences

Attackers cannot enumerate accounts through response shape, timing or throttle behavior.
