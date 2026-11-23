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

## 3. Production on Render (recommended)

Render fits this stack well: Docker web services stay running continuously,
managed Postgres is available, and the same platform can host the frontend as
a static site or web service. A `render.yaml` blueprint at the repo root
provisions the database and API service.

### Plan requirements (important)

- **API web service: paid instance required.** Render's free web service spins
  down after ~15 minutes without inbound traffic. While asleep, no deposits
  are detected, no withdrawals progress, no outbox messages dispatch and no
  market data is polled. Use at least the `starter` instance type for the API.
- **PostgreSQL: paid plan required.** Render's free Postgres expires after 30
  days. Use `basic-256mb` or larger.
- Run **one API instance** initially. The background pollers are designed to
  be safe under concurrency (leases and idempotency keys), but a single
  instance keeps operational reasoning simple.

### Services

1. **PostgreSQL** - provisioned by `render.yaml` as `askvest-db`. After
   creation, connect once (Render Shell or `psql` with the external
   connection string) and create the least-privilege application role:

   ```sql
   CREATE ROLE askvest_app LOGIN PASSWORD '<generated-secret>';
   GRANT CONNECT ON DATABASE askvest TO askvest_app;
   GRANT USAGE ON SCHEMA public TO askvest_app;
   GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO askvest_app;
   GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO askvest_app;
   ```

   Adjust schema grants if the migrations create dedicated schemas; the app
   role must never be able to run DDL.

2. **API** - `type: web`, `runtime: docker`, built from
   `docker/api/Dockerfile`. Health check path `/health/ready`. Attach a custom
   domain for TLS.

3. **Migrator** - run out-of-band before each API release that adds
   migrations. Either use a Render Job/one-off task or run the image from CI
   or an operator machine:

   ```bash
   docker build -f docker/migrator/Dockerfile -t askvest-migrator .
   docker run --rm \
     -e ConnectionStrings__Platform="Host=<external-db-host>;Port=5432;Database=askvest;Username=askvest_owner;Password=<secret>;SSL Mode=Require" \
     -e Compliance__RealMoneyEnabled=false \
     askvest-migrator
   ```

   The migrator exits non-zero on failure; only start/keep the API on the new
   version after it succeeds.

### Required environment variables

Set secrets in the Render dashboard (the blueprint marks them `sync: false`
so no secret lands in the repo). Full list is in `docker-compose.yml`; the
production-relevant set:

| Variable | Purpose |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `ASPNETCORE_URLS` | `http://+:8080` (Render routes to the exposed port) |
| `ConnectionStrings__Platform` | API database connection as `askvest_app`, `SSL Mode=Require` |
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
it only in Render's environment configuration.

## 4. Frontend on Render (or anywhere)

The frontend can be a Render Static Site (or web service) in the same
Render account, or stay on Vercel/Netlify. Either way:

- Set the API base URL env var (e.g. `NEXT_PUBLIC_API_BASE_URL` or your
  framework's equivalent) to `https://<api-domain>`.
- Add the exact frontend origin(s) to `Cors__AllowedOrigins__*` on the API
  service and redeploy.

## 5. Frontend integration quick start

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
`Cors__AllowedOrigins__0`, `__1`, etc. The API is bearer-token only; there are
no cookies, so no credentialed-CORS complications.

## 6. Alternatives considered

- **Vercel for the API**: possible via Container Functions (beta), but
  instances scale to zero after ~5 idle minutes, which stops all background
  pollers (deposits, withdrawals, outbox). Not recommended for a
  money-moving product; acceptable only for demos with a keep-warm cron.
- **Splitting workers from the API**: the pollers currently run in-process.
  A dedicated worker deployment would need a code change (host flag or
  separate entrypoint); not required on Render since web services stay warm.

## 7. Verification checklist before handoff

- [ ] `GET /health/ready` returns 200 against the production database.
- [ ] Migrator ran successfully and exited 0 before API start.
- [ ] Sign-up -> email code -> login -> `GET /api/v1/account/me` works from the
      frontend origin (CORS verified in the browser, not just curl).
- [ ] One practice order and one catalog read exercised end to end.
- [ ] `Docs__Enabled=false` in production unless public docs are intended.
- [ ] No `fake` providers remain in routes that move real money.
- [ ] Secrets exist only in the platform environment configuration.
