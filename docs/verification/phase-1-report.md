# Phase 1 Final Report: Identity and Access

**Status: Phase 1 complete.** All 17 steps implemented, verified and committed.
Self-audit: all 19 rows have evidence in `phase-1-evidence.md`.

## Environment and library choices

- Runtime: .NET 10 (`net10.0`), ASP.NET Core minimal APIs, EF Core 10 +
  Npgsql, PostgreSQL 17.
- Password/PIN hashing: `Konscious.Security.Cryptography.Argon2` 1.3.1
  (Argon2id, memory 19456 KiB, 2 iterations, parallelism 1, 32-byte hash,
  16-byte salt, versioned server pepper). Measured ~130-340 ms per hash on
  the dev workstation; concurrency capped at 4 via `CredentialHashingLimiter`.
- Access tokens: ES256 JWT (`Microsoft.IdentityModel.JsonWebTokens`),
  15-minute lifetime, session-bound; refresh tokens are opaque, rotating,
  stored as keyed hashes.
- Email: MailKit 4.18.2 behind `IEmailTransport`; Mailpit v1.32.0 in dev and
  smoke; production rejects loopback/Mailpit hosts at startup.
- SMS: feature-flagged; development adapter forwards SMS as email, production
  requires a real provider (none configured yet).
- Phone numbers: `libphonenumber-csharp` 9.0.42, normalized to E.164.
- Breach check: HaveIBeenPwned k-anonymity range API, fail-open, disabled by
  default (`PasswordBreachCheckEnabled`).
- Test framework: xunit.v3 4.0.2 on Microsoft.Testing.Platform.

## Module file tree

`src/Modules/AskVest.Modules.Identity` (~100 source files):

- `Configuration/` - IdentityOptions, IdentitySecretsOptions, MessagingOptions
- `Contracts/` - request records, IDeletionBlocker, IStepUpAuthorizer
- `Crypto/` - Argon2id core, password/PIN hashers, keyed hashers, OTP hasher,
  access-token service, policy, normalizers, breach checker, limiter
- `Domain/Enums/` - user status, code purpose/destination, event type,
  throttle kind
- `Endpoints/` - AuthEndpoints, AccountEndpoints, PinStepUpEndpoints,
  PhoneEndpoints
- `Messaging/` - transports (SMTP, capturing, dev SMS, null), templates,
  dispatch worker with retries
- `Middleware/` - PendingDeletionGateMiddleware
- `Persistence/` - IdentityDbContext, 9 entity configurations, 9 entities,
  design-time factory, 4 migrations
- `Services/` - sign-up, email verification, login, verify-device, refresh,
  session, password, PIN, step-up, phone, account, deletion, anonymization,
  maintenance, throttle, security-event recorder, bearer handler
- `Validation/` - FluentValidation validators for all request contracts

Host-side additions: `IdempotencyEndpointGuard`, user-scoped
`IIdempotencyScopeResolver`, bearer scheme wiring, `service.unavailable`
exception mapping for database outages.

## Test counts and rerun

| Layer | Count | Command |
|---|---|---|
| Unit | 168 | `dotnet test --project tests/AskVest.UnitTests -c Release` |
| Integration | 137 | `dotnet test --project tests/AskVest.IntegrationTests -c Release` |
| Architecture | 11 | `dotnet test --project tests/AskVest.ArchitectureTests -c Release` |
| Smoke | 12 | `ASKVEST_SMOKE_BASE_URL=http://127.0.0.1:5082 ASKVEST_SMOKE_MAILPIT_URL=http://127.0.0.1:18026 dotnet test --project tests/AskVest.SmokeTests -c Release` |

All green, zero skipped. `./scripts/verify.sh` runs the non-smoke layers;
smoke requires the compose stack below.

## Deviations from the Phase 1 document

- `IdempotencyEndpointGuard` added in `AskVest.Api` so the idempotency and
  returns-secrets rules are enforced by a reusable guard with synthetic-
  violation tests, rather than a scan living only inside a test.
- In-code analyzer pragmas were moved to `.editorconfig` file-scoped
  suppressions with reasons (CA2000 ownership transfer, CA5350 HIBP SHA-1),
  keeping hand-written code pragma-free per project rules.
- `xunit.v3` upgraded 3.2.2 -> 4.0.2 to clear the deprecated
  `Microsoft.Testing.Platform.MSBuild` transitive package.
- The diagnostics probe endpoint remains the only mutating non-module
  endpoint (Phase 0 carryover, idempotency-exempt by design).
- Phone endpoints exist but are feature-flagged off outside explicit opt-in.

## Unverified items

- Remote CI: workflow is manual-only; no run exists yet for Phase 1 commits.
- Real SMS provider: no gateway configured; only the dev/forwarding adapter.
- Production secret manager: env vars only.
- Live HaveIBeenPwned call: fail-open path unit-tested with a fake transport.
- Phone flag enabled in the live smoke stack: covered by integration tests
  only.
- Argon2 timings are workstation measurements, not production benchmarks.

## Running the stack and reading email

```bash
cp .env.example .env   # fill in generated secrets per README
docker compose up -d --build --wait           # dev stack
docker compose -f docker-compose.yml -f docker-compose.smoke.yml up -d --build --wait  # smoke stack
```

Mailpit UI: `http://localhost:18025` (dev) / smoke port per `.env`
(`ASKVEST_MAILPIT_UI_PORT`). API docs: `/scalar` when `Docs__Enabled=true`.
All verification evidence scripts live in `scripts/` and
`docs/verification/`.

## What Phase 2 must know

- Extend `StepUpPurposes` for new privileged actions; grants are single-use,
  120-second, bound to user+session+purpose+optional binding hash.
- Honor `IDeletionBlocker`: register blockers for balances, open orders,
  pending KYC etc.; `account.deletion_blocked` returns the reason list.
- Follow `docs/design/09-module-blueprint.md`: own schema + history table,
  contracts-only public surface, markers for idempotency/returns-secrets/
  pending-deletion.
- Sign-up still needs terms and consent acceptance fields.
- The phone feature flag (`Identity:Phone:Enabled` or equivalent config)
  gates the phone routes; decide its production default in Phase 2.
- `security_events` is append-only for `askvest_app`; new event types go in
  the `SecurityEventType` enum and are covered by the coverage test.
