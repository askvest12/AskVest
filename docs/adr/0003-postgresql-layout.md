# ADR 0003: PostgreSQL layout

- Status: Accepted

## Context

Many modules share one database. We need isolation without distributed databases.

## Decision

One database, one schema per module plus `platform`. snake_case naming, `uuid` keys (UUIDv7), `timestamptz`, `numeric(28,18)` for money, `xmin` optimistic concurrency, created/updated timestamps on mutable tables. See `docs/design/02-data-standards.md`.

## Consequences

Schema-level separation gives ownership boundaries while keeping joins and transactions simple inside a module. Cross-module references are by id only.

## Alternatives rejected

- Database per module: loses transactional integrity across modules, multiplies operations.
- Single shared schema: no ownership boundary, harder migrations per module.
