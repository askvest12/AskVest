# ADR 0008: Database roles and migrations

- Status: Accepted

## Context

The API must never run DDL and should have minimal privileges. Migrations need an owner-level credential kept away from the runtime.

## Decision

Three roles: `askvest_admin` (container superuser, unused by apps), `askvest_owner` (runs migrations via the `AskVest.Migrator` one-shot container), `askvest_app` (API, DML only, timeouts set). The API never auto-migrates; `DatabaseReadinessCheck` fails readiness while migrations are pending. No EF retry on writes.

## Consequences

A compromised API credential cannot alter schema or escalate. Deploy order is enforced by Compose (`migrator` completes before `api` starts). Readiness honestly reports schema drift.

## Alternatives rejected

- API-run migrations: couples deploys to schema risk and grants DDL to the runtime role.
- EF retry on writes: can mask idempotency bugs; clients retry explicitly with keys.
