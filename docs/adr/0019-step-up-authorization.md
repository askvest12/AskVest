# ADR 0019: Step-up authorization

Status: accepted (Phase 1)

## Decision

Sensitive actions require a single-use step-up grant: PIN verification produces a random grant token, stored hashed, bound to user, session, purpose and optional binding hash, valid 120 seconds. Consumption is atomic and exposed to other modules through a contract. Purposes live in a fixed registry that later phases extend.

## Consequences

Money-moving and account-destructive actions get fresh proof of presence without re-authentication. The binding hash lets Phase 10 tie grants to exact approved quotes.
