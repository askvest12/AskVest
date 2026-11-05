# ADR 0020: Account deletion and erasure

Status: accepted (Phase 1)

## Decision

Deletion requires password plus a `delete_account` step-up grant, is blocked by registered module blockers, and starts a 30-day cooling-off. An idempotent job then anonymizes the account: non-reversible email placeholder, removed phone, password, PIN, devices and sessions, stripped event metadata. The user id survives so ledger records stay valid.

## Consequences

Erasure rights are honored while financial retention obligations keep ledger integrity.
