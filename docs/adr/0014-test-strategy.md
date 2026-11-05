# ADR 0014: Test strategy

- Status: Accepted

## Context

Financial correctness needs proof at every layer, including the real deployment shape.

## Decision

Four layers. Unit tests for logic (SharedKernel, converters, options). Architecture tests (NetArchTest) for module and endpoint rules, including the idempotency-declaration guard. Integration tests run the real API in-process (`WebApplicationFactory`) against real PostgreSQL 17 in Testcontainers with role scripts, migrations and Respawn resets. Smoke tests run as a black box over real HTTP against the Docker Compose stack. xUnit v3 (3.2.2) on Microsoft.Testing.Platform via `global.json`; smoke tests skip with a reason when `ASKVEST_SMOKE_BASE_URL` is unset.

## Consequences

Nothing is "done" without automated proof plus, for HTTP surface, a real call against real containers. Database is never mocked.

## Alternatives rejected

- Mocking the database: hides SQL, constraint and privilege bugs.
- xUnit v2: older runner; v3's native MTP support is cleaner on .NET 10.
- In-memory TestServer only: misses container, proxy and config issues.
