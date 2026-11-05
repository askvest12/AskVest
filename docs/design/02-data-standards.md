# 02. Data Standards

## Layout

One PostgreSQL database, one schema per module plus `platform`. Schema names are `[a-z_]+` only (enforced by `SchemaAccess`). The `platform` schema holds cross-cutting infrastructure (idempotency keys, migration history).

## Naming and types

- Tables, columns, indexes: snake_case.
- Primary keys: `uuid`, generated client-side with `Guid.CreateVersion7()` (time ordered, index friendly).
- Timestamps: `timestamptz`, UTC only.
- Money and quantities: `numeric(28,18)`, which fits C# `decimal` exactly.
- Mutable aggregates carry an optimistic concurrency token (`xmin`).
- Every mutable table has `created_at` and `updated_at`.
- No soft delete for financial records. Personal data is anonymized on erasure; ledger rows are retained (threat T19).

## Migrations

- Forward-only. Never edit an applied migration.
- Breaking changes use expand and contract (add new, migrate, drop old in a later migration).
- Each module's first migration calls `SchemaAccess.ProvisionSchema` **before any `CreateTable`**: default privileges only apply to objects created afterward.

## Roles

| Role | Used by | Privileges |
|---|---|---|
| `askvest_admin` | container superuser only | never used by applications |
| `askvest_owner` | `AskVest.Migrator` | owns schemas, runs DDL and migrations |
| `askvest_app` | `AskVest.Api` | DML only inside granted schemas, no DDL |

`askvest_app` session limits: `statement_timeout = 15s`, `lock_timeout = 5s`, `idle_in_transaction_session_timeout = 30s`. The database itself enforces `idle_in_transaction_session_timeout = 30s`.

## Provisioning pattern

The first migration of each module emits, via `SchemaAccess.ProvisionSchema`:

1. `GRANT USAGE ON SCHEMA <schema> TO askvest_app`
2. `ALTER DEFAULT PRIVILEGES FOR ROLE askvest_owner IN SCHEMA <schema> GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO askvest_app`
3. `ALTER DEFAULT PRIVILEGES FOR ROLE askvest_owner IN SCHEMA <schema> GRANT USAGE, SELECT ON SEQUENCES TO askvest_app`
4. `GRANT SELECT ON <history table>` so the API can check pending migrations

## Retry policy

No automatic EF retry on writes. Clients retry with idempotency keys, which is why every state-changing endpoint is idempotent.
