# ADR 0015: Practice and Real separation

- Status: Accepted

## Context

Users practice with virtual credits before touching real money. Confusion between the two is a severe product and regulatory risk (T22).

## Decision

Separate route groups `/api/v1/practice/...` and `/api/v1/real/...` with separate authorization. Separate ledger books in Phase 3 that can never post to each other. Tests explicitly attempt crossover and must fail.

## Consequences

A client cannot accidentally move real money through a practice path. The boundary is structural (routes, books, auth) not just a flag.

## Alternatives rejected

- A `mode` field on shared endpoints: one mistaken branch moves real money.
- Shared ledger with account types: weaker guarantee, harder audits.
