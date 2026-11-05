# Phase 0 Final Report

## Status

Phase 0 complete. All 13 steps executed in order with verification evidence in `docs/verification/phase-0-evidence.md`.

## Environment

Linux x86_64, .NET SDK 10.0.114, Docker 29.8.1, Compose v5.5.1, Git 2.53.0.

## Repository tree

- `src/AskVest.SharedKernel` - Result/Error/pagination primitives
- `src/AskVest.Platform` - EF Core + Npgsql, platform schema, roles, migrator support, idempotency storage
- `src/AskVest.Api` - minimal API host: envelope, error catalogue, middleware, health, OpenAPI/Scalar, rate limiting, idempotency
- `src/AskVest.Migrator` - forward-only migration runner (askvest_owner)
- `tests/` - UnitTests (54), ArchitectureTests (1), IntegrationTests (44, Testcontainers), SmokeTests (8, env-gated)
- `docs/` - design docs, 15 ADRs, verification evidence
- `docker/` - hardened images, postgres init, compose + smoke override
- `.github/` - CI workflow (green on main, run 41966353451), Dependabot

## Test commands and counts

- `dotnet build AskVest.slnx` - 0 warnings, 0 errors
- `dotnet test --project tests/AskVest.UnitTests` - 54 passed
- `dotnet test --project tests/AskVest.IntegrationTests` - 44 passed
- `dotnet test --project tests/AskVest.ArchitectureTests` - 1 passed
- `ASKVEST_SMOKE_BASE_URL=http://localhost:5081 dotnet test --project tests/AskVest.SmokeTests` - 8 passed against real containers
- Remote CI: success on main (GitHub Actions run 41966353451)

## Package choices and deviations

- xUnit v3 + Microsoft.Testing.Platform (global.json `test.runner`), locked via central package management + lock files.
- Scalar.AspNetCore for API docs; Serilog.AspNetCore console JSON; FluentValidation.AspNetCore; Respawn + Testcontainers.PostgreSql for tests.
- Deviation: `Error` type name kept via analyzer configuration rather than rename, per spec naming.
- Deviation: diagnostics probe endpoint (`POST /diagnostics/probe`) added as the only mutating endpoint, gated by `Diagnostics:EnableProbes`, to exercise idempotency end to end.

## Unverifiable / noted items

- Smoke suite requires `ASKVEST_SMOKE_BASE_URL`; skips with explicit reason otherwise (verified).
- No business modules, auth, KYC, ledger, providers, or AI - deferred to later phases per spec.

## Startup

```bash
cp .env.example .env
docker compose up -d --build --wait
curl http://localhost:5080/health/live
curl http://localhost:5080/api/v1/system/info
```

## Phase 1 notes

- `IIdempotencyScopeResolver` exists (`DefaultIdempotencyScopeResolver` returns "api"); Phase 1 must scope keys per user/tenant.
- Strict rate-limit policy named `strict` is registered and ready for auth endpoints.
- `auth.*` error codes are reserved in `ErrorCodes` (`auth.unauthenticated`, `auth.forbidden`).
- `PlatformDbContext` + `SchemaAccess.ProvisionSchema` must run before any module calls `CreateTable`.
- Envelope, correlation ID, validation filter, and idempotency middleware are already in the pipeline.
