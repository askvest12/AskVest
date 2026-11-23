# Deployment guide

How to run the AskVest backend and connect the frontend. The API is a single
ASP.NET Core process; PostgreSQL is a separate service; the migrator is a
one-shot container that must run to completion before the API starts.

## 1. What runs where

| Component | Image / project | Lifetime | Notes |
|---|---|---|---|
| PostgreSQL 17 | `postgres:17` (compose) or managed DB | always on | holds all state; needs persistent storage |
| Migrator | `docker/migrator/Dockerfile` | one-shot | applies EF migrations as the owner role; exits |
| API | `docker/api/Dockerfile` | always on | serves HTTP on the configured port; also runs background pollers (deposits, withdrawals, outbox, market data, reserves) |

`docker compose up -d --build --wait` runs all three locally. The compose file
is for development and smoke testing only; it is not a production deployment
manifest.

## 2. Local development

```bash
cp .env.example .env   # fill in every required value
docker compose up -d --build --wait
```

- API: `http://localhost:${ASKVEST_API_PORT:-5080}`
- Health: `GET /health/live` and `GET /health/ready`
- API docs (when `Docs__Enabled=true`): `/openapi/v1.json` and Scalar UI at `/scalar/v1`
- Smoke probes and Mailpit are only in `docker-compose.smoke.yml`; do not use
  that override outside local testing.

## 3. Production architecture (recommended)

Vercel is a good fit for the **frontend**. The backend has two requirements
that do not fit Vercel's request-driven container model cleanly:

1. **Background work.** The API process hosts pollers that detect deposits,
   progress withdrawals, dispatch the outbox, poll market data and run
   reconciliation. These stop when the process is not running.
2. **PostgreSQL.** Vercel Functions have no persistent filesystem and no
   managed Postgres; the database must live elsewhere.

Recommended split:

- **Frontend** on Vercel (Next.js or any static/SSR app).
- **PostgreSQL** on a managed provider (Neon, Supabase, RDS, Cloud SQL, or a
  VPS). Require TLS and a private connection where possible.
- **API** on a container host that keeps the process warm: Fly.io, Railway,
  Render, Google Cloud Run (min instances >= 1), or a VPS running the image
  from `docker/api/Dockerfile` behind a reverse proxy.
- **Migrator** run as a one-off job from CI or an operator machine against the
  production database before each API rollout that contains new migrations.

If the API must run on Vercel anyway (see section 5), plan for the
scale-to-zero consequences explicitly.

## 4. API deployment on a container host

### Image

Build from the repo root:

```bash
docker build -f docker/api/Dockerfile -t askvest-api .
```

The image listens on the port in `ASPNETCORE_URLS` (compose uses
`http://+:8080`). Expose that port and point your platform's health check at
`/health/ready`.

### Database roles

The migrator connects as the owner role (`askvest_owner`) and runs DDL. The
API connects as `askvest_app`, a least-privilege role that cannot run DDL.
Create both roles and the database on the managed Postgres instance, then set:

```
ConnectionStrings__Platform=Host=<db-host>;Port=5432;Database=askvest;Username=askvest_app;Password=<secret>;SSL Mode=Require
```

Run the migrator image once with the owner connection string before starting
the API:

```bash
docker build -f docker/migrator/Dockerfile -t askvest-migrator .
docker run --rm -e ConnectionStrings__Platform="Host=<db-host>;...;Username=askvest_owner;Password=<secret>" askvest-migrator
```

### Required environment variables

Secrets come from the platform's secret manager, never from files in the repo.
The full list is in `docker-compose.yml`; the production-relevant set:

| Variable | Purpose |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `ASPNETCORE_URLS` | e.g. `http://+:8080` |
| `ConnectionStrings__Platform` | API database connection (app role) |
| `Identity__Secrets__AccessTokenSigningKey` | JWT signing key |
| `Identity__Secrets__AccessTokenSigningKeyPrevious` | optional rotation overlap |
| `Identity__Secrets__PasswordPepper` | password hashing pepper |
| `Identity__Secrets__OneTimeCodeHashSecret` | OTP hash secret |
| `Messaging__Smtp__Host/Port/UserName/Password/FromAddress/FromName/EnableStartTls` | transactional email |
| `Compliance__Kyc__WebhookSecret` | KYC provider webhook signature |
| `Funding__Providers__0__WebhookSecret` | funding provider webhook secret |
| `Cors__AllowedOrigins__0`, `__1`, ... | exact frontend origins, no wildcards in production |
| `Docs__Enabled` | `false` in production unless you want public OpenAPI |
| `Compliance__RealMoneyEnabled`, `Compliance__RealMoneyCountries__*` | real-money gate |
| provider config (`Compliance__*`, `Wallets__Custody__*`, `Funding__*`, `MarketData__*`, `Trading__Gateway__*`) | real provider settings replace the `fake` adapters |

Generate every secret with a CSPRNG (e.g. `openssl rand -base64 48`) and store
it only in the secret manager.

## 5. Deploying the API image on Vercel

Vercel can run OCI images as request-driven Functions (Container runtime,
beta). This works for the HTTP surface but has hard limits for AskVest:

- **Port.** Vercel routes to port `80` by default. Either set
  `ASPNETCORE_URLS=http://+:80`, or set the `PORT` project env var to `8080`
  and keep `ASPNETCORE_URLS=http://+:8080`.
- **Scale to zero.** Instances with no traffic for ~5 minutes shut down
  (`SIGTERM`, 30 s grace). While shut down, no deposits are detected, no
  withdrawals progress, no outbox messages dispatch and no market data is
  polled. A scheduled ping to `/health/ready` can keep an instance warm, but
  that defeats scale-to-zero pricing and is a workaround, not a design.
- **Statelessness.** No persistent disk; the database must be external.
- **Function limits.** Request duration and concurrency limits apply; long
  operations must not be assumed.

If you proceed:

1. Put `docker/api/Dockerfile` (or a thin `Dockerfile.vercel` that `FROM`s the
   published image) at the root of a dedicated Vercel project.
2. Set `"framework": "container"` in `vercel.json` if framework detection
   picks something else.
3. Configure all environment variables from section 4 in the Vercel project.
4. Deploy the frontend separately and add its production origin to
   `Cors__AllowedOrigins__*`.
5. Run the migrator out-of-band (section 4) before each release.
6. Treat deposit/withdrawal latency as best-effort while scaled to zero, or
   add a keep-warm cron and accept the cost.

Honest assessment: Vercel for the API is viable for demos and early
integration, but a money-moving product needs the background pollers running
continuously. Prefer a warm container host for the API and keep Vercel for the
frontend.

## 6. Frontend integration quick start

Base URL: the deployed API origin, e.g. `https://api.askvest.example.com`.

- **Auth.** `POST /api/v1/auth/sign-up`, verify email via the code sent by
  email, then `POST /api/v1/auth/login`. Responses return `accessToken`,
  `refreshToken` and `accessTokenExpiresAt`. Send
  `Authorization: Bearer <accessToken>` on every protected call; refresh via
  `POST /api/v1/auth/refresh` before expiry. `GET /api/v1/account/me` confirms
  the session.
- **Envelope.** Every response, including errors, is
  `{ success, message, data, error, correlationId }`. Branch on
  `error.code` (e.g. `auth.unauthorized`, `wallet.unavailable`,
  `resource.not_found`). Money and quantities are JSON strings, not numbers.
- **Idempotency.** Every state-changing endpoint requires an
  `Idempotency-Key` header (UUID recommended). Replays return the stored
  response. Money-moving calls also persist the key server-side.
- **Correlation.** Send or capture `X-Correlation-Id` for support debugging.
- **Eligibility.** Routes are split `/api/v1/practice/*` and
  `/api/v1/real/*`; real endpoints require completed KYC and return
  `eligibility.*` errors otherwise.
- **Docs.** With `Docs__Enabled=true`, import `/openapi/v1.json` into the
  frontend's API client generator, or browse `/scalar/v1`.

### CORS

The API only allows origins listed in `Cors:AllowedOrigins`. Add the exact
frontend origins (including preview deployments if needed) as
`Cors__AllowedOrigins__0`, `__1`, etc. Requests with credentials are not used;
the API is bearer-token only, so `Access-Control-Allow-Origin` reflects the
configured list.

## 7. Verification checklist before handoff

- [ ] `GET /health/ready` returns 200 against the production database.
- [ ] Migrator ran successfully and exited 0 before API start.
- [ ] Sign-up -> email code -> login -> `GET /api/v1/account/me` works from the
      frontend origin (CORS verified in the browser, not just curl).
- [ ] One practice order and one catalog read exercised end to end.
- [ ] `Docs__Enabled=false` in production unless public docs are intended.
- [ ] No `fake` providers remain in routes that move real money.
- [ ] Secrets exist only in the platform secret manager.
