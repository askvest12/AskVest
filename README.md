# AskVest Backend

Phase 0 foundation: hardened .NET 10 backend with PostgreSQL, EF Core migrations, response envelope, idempotency, OpenAPI/Scalar, Serilog, strict JSON rules, and containerized deployment.

## Prerequisites

- .NET SDK 10.0.x (see `global.json`)
- Docker with Compose v2
- Git

## Setup

```bash
git clone git@github.com:askvest12/AskVest.git
cd AskVest
dotnet restore AskVest.slnx --locked-mode
```

## Run locally (containers)

```bash
cp .env.example .env   # fill in passwords
docker compose up -d --build --wait
curl http://localhost:5080/health/live
curl http://localhost:5080/api/v1/system/info
```

The migrator runs automatically before the API starts. Scalar docs are served at `/scalar/v1` when `Docs__Enabled=true`.

## Test

```bash
./scripts/verify.sh                 # restore, build, all tests, vulnerability audit
dotnet test --project tests/AskVest.UnitTests
dotnet test --project tests/AskVest.IntegrationTests   # requires Docker (Testcontainers)
```

Smoke tests against the running stack:

```bash
ASKVEST_SMOKE_BASE_URL=http://localhost:5080 dotnet test --project tests/AskVest.SmokeTests
```

## Layout

- `src/AskVest.SharedKernel` - Result/Error primitives
- `src/AskVest.Platform` - EF Core context, migrations, readiness, idempotency storage
- `src/AskVest.Api` - HTTP host: envelope, middleware, health, OpenAPI
- `src/AskVest.Migrator` - forward-only migration runner
- `tests/` - unit, architecture, integration (Testcontainers), smoke
- `docs/` - design docs and ADRs
- `docker/` - images and postgres init

## Troubleshooting

- Port conflicts: set `ASKVEST_API_PORT` / `ASKVEST_POSTGRES_PORT` in `.env`.
- `ConnectionStrings:Platform is required`: set the connection string env var or appsettings.
- Containers unhealthy: `docker compose logs api postgres migrator`.
