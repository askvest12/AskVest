# ADR 0005: Idempotency

- Status: Accepted

## Context

Mobile networks retry. Money-moving endpoints must never execute twice. A stored-response replay alone has a crash window: if the process dies after the handler commits but before the response is stored, a retry after the lock expires would run the handler again.

## Decision

Middleware claims `(scope, idempotency_key)` rows in `platform.idempotency_keys` with fingerprint checking, in-progress locking with takeover, and stored-response replay. Only 2xx/404/409/422 responses are stored; other statuses release the key. Handlers additionally persist the key under a unique constraint in the same transaction as their effects (Phases 3, 10, 12), which makes a post-commit crash harmless. Replays return the original correlation id inside the stored body while the response header carries the current request's correlation id.

## Consequences

At-least-once clients are safe everywhere. The store adds one write per mutating call. Fail-closed behavior means a database outage rejects mutating calls rather than risking duplicates.

## Alternatives rejected

- Client-side sequence numbers only: does not survive process crashes or multi-device use.
- Exactly-once messaging semantics: not achievable over HTTP; idempotent commands are the practical equivalent.
