# AskVest Backend

Phase 1: hardened .NET 10 backend with PostgreSQL, EF Core migrations, response envelope, idempotency, OpenAPI/Scalar, Serilog, strict JSON rules, containerized deployment, and the Identity module (sign-up, verification, login, sessions, refresh rotation, devices, password and PIN management, step-up authorization, security history, phone verification, and account deletion with anonymization).

## Prerequisites

- .NET SDK 10.0.x (see `global.json`)
- Docker with Compose v2
- Git
- OpenSSL (for generating development secrets)

## Setup

```bash
git clone git@github.com:askvest12/AskVest.git
cd AskVest
dotnet restore AskVest.slnx --locked-mode
cp .env.example .env
```

Fill in `.env`. The database passwords can be any strong value; the Identity
secrets must be generated once for development:

```bash
# ES256 signing key pair material (PKCS8 private key, base64)
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -outform DER | base64 -w0

# Password pepper and one-time-code hash secret (64 hex chars each)
openssl rand -hex 32
openssl rand -hex 32

# KYC webhook signing secret (64 hex chars)
openssl rand -hex 32
```

Set the results in `.env`:

- `ASKVEST_IDENTITY_SIGNING_KEY` — the base64 PKCS8 key from the first command
- `ASKVEST_IDENTITY_PASSWORD_PEPPER` — first `openssl rand -hex 32` output
- `ASKVEST_IDENTITY_CODE_SECRET` — second `openssl rand -hex 32` output
- `ASKVEST_IDENTITY_SIGNING_KEY_PREVIOUS` — leave empty unless rotating keys
- `ASKVEST_COMPLIANCE_KYC_WEBHOOK_SECRET` — the third `openssl rand -hex 32` output

Never commit real secret values; `.env` is git-ignored and `.env.example`
contains names only.

## Run locally

Production-like stack (real SMTP required via `ASKVEST_SMTP_*`):

```bash
docker compose up -d --build --wait
```

Development stack with Mailpit (captures all outgoing email locally):

```bash
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build --wait
```

- API: `http://localhost:5080` (`/health/live`, `/scalar/v1` for docs)
- Mailpit UI: `http://localhost:18025` — read verification codes here
- Mailpit SMTP: `localhost:11025`

The migrator runs automatically before the API starts.

## Identity smoke flows

1. `POST /api/v1/auth/sign-up` with `{ "email": "you@example.com", "password": "..." }` and an `Idempotency-Key` header.
2. Open Mailpit at `http://localhost:18025`, copy the 6-digit code from the verification email.
3. `POST /api/v1/auth/verify-email` with `{ "email": ..., "code": ... }`.
4. `POST /api/v1/auth/login` with `{ "email": ..., "password": ..., "device": { "installationId": "...", "platform": "android", "deviceName": "...", "appVersion": "1.0.0" } }` — the first device is trusted automatically; later devices get an email challenge via `POST /api/v1/auth/verify-device`.
5. Authenticated calls use `Authorization: Bearer <accessToken>`; rotate with `POST /api/v1/auth/refresh`.

## Test

```bash
./scripts/verify.sh                 # restore, build, all tests, vulnerability audit
dotnet test --project tests/AskVest.UnitTests
dotnet test --project tests/AskVest.IntegrationTests   # requires Docker (Testcontainers)
```

Smoke tests against the running stack:

```bash
ASKVEST_SMOKE_BASE_URL=http://localhost:5081 dotnet test --project tests/AskVest.SmokeTests
```

The smoke stack (`docker-compose.smoke.yml`) runs the API in Development mode
with Mailpit and probe endpoints enabled. It also enables real money for the
`NG` test country with the fake KYC and screening providers, so the full
Practice and Real eligibility journey can run over HTTP.

## Compliance smoke flows

The smoke suite walks a verified user through documents, consents, country,
KYC start and a signed provider webhook, then checks the gated
`/api/v1/practice` and `/api/v1/real` probe endpoints.

KYC webhooks are received at `POST /api/v1/webhooks/kyc/{provider}` and are
signed with HMAC-SHA256 over `{timestamp}.{rawBody}`. To call it by hand:

```bash
BODY='{"eventId":"evt-1","providerReference":"<ref>","outcome":"approved","verifiedCountry":"NG","fullName":"Test User","dateOfBirth":"1990-01-01"}'
TS=$(date +%s)
SIG=$(printf '%s' "$TS.$BODY" | openssl dgst -sha256 -hmac "$ASKVEST_COMPLIANCE_KYC_WEBHOOK_SECRET" | awk '{print $2}')
curl -X POST http://localhost:5081/api/v1/webhooks/kyc/fake \
  -H "X-Webhook-Timestamp: $TS" -H "X-Webhook-Signature: $SIG" \
  -H 'Content-Type: application/json' -d "$BODY"
```

The `providerReference` comes from `POST /api/v1/compliance/kyc/start`. A name
containing `SMOKE-SANCTIONED` triggers the fake screening hit path.

## Rotating signing keys

Access tokens are signed with ES256. To rotate:

1. Generate a new key pair as above.
2. Move the current `ASKVEST_IDENTITY_SIGNING_KEY` value into
   `ASKVEST_IDENTITY_SIGNING_KEY_PREVIOUS` and put the new key in
   `ASKVEST_IDENTITY_SIGNING_KEY`.
3. Restart the API. Tokens signed by the previous key keep validating until
   they expire (10 minutes); issue a fresh pair after the swap.

To change the password pepper or code secret, bump
`Identity__Secrets__PasswordPepperVersion` / regenerate the secret and expect
existing password hashes and one-time codes to be invalidated — users must
reset their password. Plan this as a maintenance event.

## Layout

- `src/AskVest.SharedKernel` - Result/Error primitives, paging, endpoint markers
- `src/AskVest.Platform` - EF Core context, migrations, readiness, idempotency storage
- `src/AskVest.Modules.Identity` - identity module: auth flows, sessions, devices, PIN, step-up, phone, deletion, messaging
- `src/AskVest.Api` - HTTP host: envelope, middleware, health, OpenAPI
- `src/AskVest.Migrator` - forward-only migration runner
- `tests/` - unit, architecture, integration (Testcontainers), smoke
- `docs/` - design docs and ADRs
- `docker/` - images and postgres init

## Troubleshooting

- Port conflicts: set `ASKVEST_API_PORT` / `ASKVEST_POSTGRES_PORT` in `.env`.
- `ConnectionStrings:Platform is required`: set the connection string env var or appsettings.
- Containers unhealthy: `docker compose logs api postgres migrator`.
- Emails not arriving in dev: check `http://localhost:18025` and `docker compose logs api | grep -i smtp`.

### Editor shows red errors or warnings that the build does not

The command-line build is the source of truth (`dotnet build` must show 0
warnings). If VS Code disagrees:

1. Install the recommended extensions: `ms-dotnettools.csdevkit` and
   `ms-dotnettools.csharp` (see `.vscode/extensions.json`).
2. Reload the window: `Ctrl+Shift+P` then "Developer: Reload Window".
3. Restart the C# language server: `Ctrl+Shift+P` then "C#: Restart Language
   Server".
4. Confirm the editor uses the pinned SDK: `.vscode/settings.json` points at
   `AskVest.slnx`; `global.json` pins SDK `10.0.114` with `latestFeature`
   roll-forward, so any 10.0.1xx SDK works.
5. Delete stale build output and restore:
   `find . -type d \( -name bin -o -name obj \) -exec rm -rf {} +` then
   `dotnet restore AskVest.slnx --locked-mode`.
6. If red lines persist, run `dotnet build` and `dotnet format AskVest.slnx
   --verify-no-changes`; both must be clean. `.editorconfig` is the shared
   rule set; rules that the compiler cannot enforce are set to suggestion so
   they never show as errors.
