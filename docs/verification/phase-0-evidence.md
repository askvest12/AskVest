# Phase 0 Verification Evidence

All commands run on Linux x86_64, .NET SDK 10.0.114, Docker 29.8.1, Compose v5.5.1.

## Environment (Step 1)

- `dotnet --version` -> 10.0.114
- `docker version` -> server 29.8.1, API 1.56
- `docker compose version` -> v5.5.1
- `git --version` -> 2.53.0
- NuGet restore connectivity verified via `dotnet restore --locked-mode`.

## Build and tests

- `dotnet build AskVest.slnx` -> 0 warnings, 0 errors (warnings-as-errors, max analyzers).
- `dotnet test --project tests/AskVest.UnitTests` -> 54 passed, 0 failed.
- `dotnet test --project tests/AskVest.IntegrationTests` -> 44 passed, 0 failed (Testcontainers PostgreSQL 17).
- `dotnet test --project tests/AskVest.ArchitectureTests` -> 1 passed.
- `ASKVEST_SMOKE_BASE_URL=http://localhost:5081 dotnet test --project tests/AskVest.SmokeTests` -> 8 passed, 0 failed, 0 skipped against `docker compose -f docker-compose.yml -f docker-compose.smoke.yml up -d --build --wait`.
- Without `ASKVEST_SMOKE_BASE_URL`: 8 skipped with explicit reason, exit code 8 (not reported as passed).

## Rule enforcement proofs (Step 3)

- Scratch file with `DateTime.UtcNow`, `Console.WriteLine`, nullable dereference, unused variable, `FromSqlRaw`, `ExecuteSqlRawAsync` -> build failed with RS0030/CA/IDE errors as designed; scratch removed.

## Database and least privilege (Steps 6, 9)

- `docker exec askvest-postgres-1 psql -U askvest_admin -d askvest -c "SELECT rolname, rolsuper, rolcreatedb, rolcreaterole FROM pg_roles WHERE rolname LIKE 'askvest%'"`:
  - askvest_admin: superuser t (container admin only)
  - askvest_owner: all false
  - askvest_app: all false
- `information_schema.role_table_grants` for grantee askvest_app on schema platform: SELECT, INSERT, UPDATE, DELETE only.
- Migration applied by askvest_owner via Migrator before API start (`Applied migration 20261105021950_InitialPlatform`).

## Container hardening (Step 9)

- Multi-stage Dockerfiles (`docker/api`, `docker/migrator`), non-root `askvest` user.
- Compose: `read_only: true`, `cap_drop: ALL` (postgres adds back CHOWN/DAC_OVERRIDE/FOWNER/SETGID/SETUID required by its entrypoint), `no-new-privileges:true`, tmpfs for /tmp.
- `docker compose ps`: postgres healthy, api healthy, migrator exited 0.
- `curl http://localhost:5080/health/live` -> `{"success":true,...,"data":{"status":"Healthy"}}`.

## Self-audit greps

- `grep -rn "TODO\|FIXME\|HACK"` over tracked sources -> only the rule text in CLAUDE.md.
- `grep -rn "\bfloat\b\|\bdouble\b" src/` -> no matches.
- `grep -rln "—"` tracked files -> no matches.
- `git ls-files | grep env/secret` -> only `.env.example` template; `.env` is gitignored.
- `dotnet list AskVest.slnx package --vulnerable --include-transitive` -> no vulnerable packages in any project.
- `git status --short` -> clean.
- Fresh clone: `git clone --depth 1` to /tmp + `dotnet restore AskVest.slnx --locked-mode` -> all 8 projects restored.

## Test counts

- Unit: 54, Integration: 44, Architecture: 1, Smoke: 8 (env-gated). Total executed in final runs: 107.

## Known limitations

- Remote CI workflow exists (`.github/workflows/ci.yml`) but its first run status is observed on GitHub after push; not claimed here.
- Program.cs is ~125 lines; endpoint concerns live in `Endpoints/` and `Middleware/` extension classes.
