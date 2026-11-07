# Phase 2 verification evidence

Running log of commands and results for the Compliance and Eligibility phase.

## Step 1. Orientation and editor parity

### Repository verify script

`./scripts/verify.sh` on a clean tree:

- `dotnet restore --locked-mode`: ok
- `dotnet build -c Release`: 0 warnings, 0 errors
- Unit tests: 168 passed
- Architecture tests: 11 passed
- Integration tests: 136 passed, 1 failed:
  `MaintenanceApiTests.Maintenance_Removes_Only_Expired_Rows_And_Keeps_Live_And_Rotated_Tokens`
  expected 1 deleted step-up grant, got 2. The suite shares one Postgres
  container and never resets between tests, so expired grants left by earlier
  tests inflate the sweep count. Rerun alone:
  `dotnet test --project tests/AskVest.IntegrationTests -c Release --filter "FullyQualifiedName~Maintenance_Removes_Only_Expired"`
  passed (1/1). Pre-existing flake, unrelated to Phase 2.
- `dotnet list package --vulnerable --include-transitive`: no vulnerable
  packages in any project.

### Phase 1 pieces confirmed

- Identity contracts present: `IStepUpAuthorizer`, `IDeletionBlocker`.
- `IUserDirectory` and `ICurrentUserContext` were documented in
  `docs/design/08-identity-module.md` but never shipped. Added the smallest
  piece: `Contracts/IUserDirectory.cs` (`UserAccountSnapshot(IsActive,
  EmailVerified)`, `FindAsync` returns null for unknown ids) implemented by
  `Services/UserDirectoryService.cs` over `IdentityDbContext`, registered in
  `AddIdentityModule`. Compliance reads Identity only through this contract.
- Anonymous allow-list: `AnonymousEndpointCatalog` plus
  `AnonymousEndpointGuard` architecture guard (in Api and mirrored in
  ArchitectureTests).
- Returns-secrets tag: `ReturnsSecretsAttribute` plus
  `IdempotencyEndpointGuard` rule that secrets endpoints must be exempt.
- `Diagnostics:EnableProbes` flag gates `/diagnostics/probe`.
- `strict` rate limit policy in `RateLimitingExtensions`.
- Module blueprint: `docs/design/09-module-blueprint.md`.
- Mailpit smoke helpers: `tests/AskVest.SmokeTests/IdentitySmokeTests.cs`.

### Editor parity

- Deleted every `bin`, `obj`, `.vs` folder, then `dotnet restore
  --locked-mode`: ok.
- `dotnet build -c Debug`: 0 warnings, 0 errors.
- `dotnet build -c Release`: 0 warnings, 0 errors (one transient CLR crash
  `0x80131506` on the first attempt; clean on retry).
- `dotnet format AskVest.slnx whitespace --verify-no-changes`: initially
  flagged `PostgresFixture.cs` indentation (fixed by `dotnet format
  whitespace`) and UTF-8 BOM charset on EF migration files (fixed by marking
  `[src/**/Migrations/[0-9]*.cs]`, `*.Designer.cs` and `*ModelSnapshot.cs` as
  `generated_code = true` with `charset = utf-8-bom` in `.editorconfig`;
  `MigrationRunner.cs` in the same folder is hand-written and stays under
  normal rules).
- `dotnet format AskVest.slnx style --verify-no-changes`: initially flagged
  13 import-ordering issues (fixed by `dotnet format style`) and 29
  IDE1006 naming violations: every `private static readonly` field written
  PascalCase instead of `_camelCase`. Renamed them all to `_camelCase`
  (`_entries`, `_safeMethods`, `_coveredMethods`, `_jsonMethods`,
  `_docsPrefixes`, `_invalidEmail`, `_invalidCredentials`, `_notPending`,
  `_invalidCode`, `_known`, `_refreshInvalid`, `_refreshConflict`,
  `_notFound`, `_invalidPhone`, `_pinNotSet`, `_pinInvalid`,
  `_stepUpInvalid`, `_util`, `_deniedPasswords`, `_contextFactories`).
- `dotnet format analyzers --verify-no-changes`: clean.

### Severity table (deliberate violations in scratch files)

| Rule | Build fails | Action |
|---|---|---|
| `csharp_style_namespace_declarations = file_scoped` | yes (IDE0161) | kept at error |
| `csharp_prefer_braces = true` | yes (IDE0011) | kept at error |
| `dotnet_style_require_accessibility_modifiers` | yes (IDE0040) | kept at error |
| `csharp_using_directive_placement = outside_namespace` | yes (IDE0065) | kept at error |
| `dotnet_style_qualification_for_field/property/method/event = false` | no (`this._x` builds clean) | lowered to suggestion |
| `dotnet_naming_rule.*` (all five, severity error) | no (IDE1006 never reported by csc; verified with camelCase class, PascalCase field, camelCase const, lowercase interface) | lowered to suggestion |

Naming rules and qualification rules are evaluated by the IDE and
`dotnet format` only; the compiler never emits them, so at `error` severity
they produced red editor lines that `dotnet build` could not reproduce.
Existing violations were still fixed (renames above) before lowering.

- `.vscode/settings.json` points the editor at `AskVest.slnx`;
  `.vscode/extensions.json` recommends `ms-dotnettools.csdevkit` and
  `ms-dotnettools.csharp`. The installed C# extension version cannot be
  checked from this environment; `global.json` pins SDK `10.0.114` with
  `latestFeature` roll-forward and `.slnx` is supported by current C# Dev
  Kit. README gained a click-by-click editor troubleshooting section.

## Step 2. Design doc, guards and module scaffold

- `docs/design/compliance.md` written: data model, eligibility rules, provider
  adapter contract, configuration, deviations (feature folders over layered
  folders to respect the file budget).
- `docs/design/01-api-standards.md`, `04-state-machines.md`,
  `05-threat-model.md`, `07-delivery-roadmap.md` updated for Phase 2.
- `src/Modules/AskVest.Modules.Compliance` scaffolded per
  `09-module-blueprint.md`; registered in the host and `.slnx`.
- New guards in `AskVest.Api` plus mirrored architecture tests:
  `AnonymousEndpointCatalog`/`AnonymousEndpointGuard` (webhook allow-listed
  with reason), `RouteGroupPolicyGuard` for `/practice` and `/real` groups,
  `IdempotencyEndpointGuard` secrets/exemption rule. Synthetic violations
  proven in `RouteGroupPolicyGuardTests`, `IdempotencyGuardTests` and the
  anonymous guard tests.
- Commit `c58e383 docs(compliance): design, guards and module scaffold`.

## Step 3. Schema, migration, configuration and production guard

- Entities `ConsentRecord`, `ComplianceProfile`, `KycWebhookEvent`,
  `ComplianceEvent` in the `compliance` schema with per-module history table.
- Migration `20261107173357_InitialCompliance` verified against real
  PostgreSQL 17 in a scratch database; schema provisioning preamble and
  `SchemaAccess.RestrictToAppendOnly` for `compliance_events`.
- `ComplianceOptions` + `KycOptions` + `ScreeningOptions` with startup
  validation: document uniqueness/version/hash/link, ISO country codes,
  blocked/real-money overlap, webhook secret >= 32 bytes, tolerance, provider
  names. Production-like guard rejects fake providers when real-money
  countries are configured.
- Commit `0ea6858 feat(compliance): schema, migration, configuration and production guard`.

## Step 4. Documents, consents, country and eligibility endpoints

- `GET /compliance/documents`, `POST /compliance/consents` (idempotent),
  `PUT /compliance/country` (idempotent), `GET /compliance/eligibility`.
- Pure `EligibilityEvaluator` plus `EligibilityService` reading Identity only
  through the new `IUserDirectory` contract (`UserAccountSnapshot`).
- `PracticeEligible`/`RealEligible` policies with
  `EligibilityAuthorizationHandler` and
  `ComplianceAuthorizationResultHandler`: 403 `compliance.not_eligible` with
  one detail per blocker.
- Diagnostics probes `/practice/diagnostics/ping` and
  `/real/diagnostics/ping` mapped only when `Diagnostics:EnableProbes` is on.
- 23 compliance integration tests green; architecture tests 16 green.
- Commit `bc9d2c4 feat(compliance): documents, consents, country and eligibility endpoints`.

## Step 5. KYC provider abstraction, webhook and screening

- `IKycProvider`, `IScreeningProvider` (the only new interfaces; the third
  named interface is `IComplianceEligibility`). Fake adapters: HMAC-SHA256
  over `{timestamp}.{rawBody}` with `X-Webhook-Timestamp` /
  `X-Webhook-Signature`; screening hit on configured name marker.
- `KycService` (start/resume, attempt limit) and `KycWebhookService`
  (provider check, body cap, timestamp tolerance, constant-time signature,
  dedupe by provider+event id, approval runs screening, rejection records
  attempt, screening error fails the webhook for provider retry).
- `POST /api/v1/webhooks/kyc/{provider}` anonymous (signature is the
  credential), idempotency-exempt, strict rate limit, added to
  `AnonymousEndpointCatalog`.
- Screening hit sets `account_restricted`; approval stores verified country
  and expiry; personal data used in memory only.
- 17 KYC/webhook integration tests green; unit tests for signature,
  options validation and production guard green.
- Commit `bff5d97 feat(compliance): kyc provider abstraction, webhook and screening hook`.

## Step 6. Containers, README and CI

- `docker-compose.yml`: `Compliance__RealMoneyEnabled=false`, fake provider
  names, `Compliance__Kyc__WebhookSecret` from
  `ASKVEST_COMPLIANCE_KYC_WEBHOOK_SECRET` (`:?required`).
- `docker-compose.smoke.yml`: `RealMoneyEnabled=true` (Development
  appsettings already list US/GB/NG/KE), fake providers,
  `HitNameMarker=SMOKE-SANCTIONED`.
- `.env.example` carries the variable name only; `.env` is git-ignored.
- `ci.yml` smoke job generates the secret with `openssl rand -hex 32`.
- README: secret generation, `.env` mapping, compliance smoke section with a
  copy-paste signed-webhook curl.
- `docker compose config` validates for both files; images rebuilt;
  `docker compose up -d --build --wait` healthy; OpenAPI lists the six new
  routes; `docker history` shows no secrets.
- Full suite once: 417 total, 405 passed, 12 smoke skipped (no base URL),
  0 failed.
- Commit `009794e chore(compliance): compose, config, readme and ci`.

## Step 7. End to end, editor re-check, audit and report

### A. Smoke journey over real HTTP

`tests/AskVest.SmokeTests/ComplianceSmokeTests.cs`, two facts covering all
seven required flows against `http://localhost:5081` with Mailpit:

1. New verified user: `consent_required` + `country_required` blockers; both
   probes 403 `compliance.not_eligible` with matching details.
2. Practice consents + country NG: practice probe 200; real probe 403 listing
   real consents and `kyc_required`.
3. Real consents, `kyc/start`, signed approval webhook: real probe 200,
   `kycStatus=Approved`.
4. Same webhook replayed: 200 no-op; bad signature: 401
   `webhook.invalid_signature`; stale timestamp: 400 `webhook.stale_timestamp`.
5. Second user: three rejection webhooks then `kyc_attempts_exhausted`,
   fourth `kyc/start` 409.
6. Third user country KP: both probes 403, `country_not_supported`.
7. Fourth user approval with `SMOKE-SANCTIONED` name: `account_restricted`,
   real probe 403.

Result on the rebuilt image: `dotnet test --project tests/AskVest.SmokeTests`
with `ASKVEST_SMOKE_BASE_URL`/`ASKVEST_SMOKE_MAILPIT_URL`/
`ASKVEST_COMPLIANCE_KYC_WEBHOOK_SECRET` set: 14/14 passed, 0 skipped.

### B. Raw evidence

Key calls by hand with curl against the smoke stack (tokens redacted, webhook
signatures are HMAC outputs over the shown bodies):

```text
=== sign-up
$ curl -X POST http://localhost:5081/api/v1/auth/sign-up -H Content-Type:application/json -H Idempotency-Key: c476fdd8edbd4b259cbe993996dd0560 -d {"email":"evidence-1794078901@example.com","password":"CorrectHorseSmoke9"}
{"success":true,"message":"If this email can be verified, a verification code has been sent.","data":null,"error":null,"correlationId":"01a1b76f83fc74d99269f7510256cf64"}
HTTP 200

=== verify-email (code redacted)
{"success":true,"message":"Email verified.","data":null,"error":null,"correlationId":"01a1b76f86ad7334a5c2de6591cdd5af"}
HTTP 200

=== login -> token acquired (redacted)

=== eligibility (new user)
$ curl http://localhost:5081/api/v1/compliance/eligibility -H Authorization: Bearer <redacted>
{"success":true,"message":"Eligibility retrieved.","data":{"practiceAllowed":false,"realAllowed":false,"kycStatus":"NotStarted","blockers":{"practice":[{"code":"country_required","message":"A country of residence must be declared."},{"code":"consent_required","message":"Accept the current documents: terms, privacy."}],"real":[{"code":"country_required","message":"A country of residence must be declared."},{"code":"consent_required","message":"Accept the current documents: terms, privacy."},{"code":"consent_required","message":"Accept the current documents: terms, privacy, risk_disclosure, token_disclosure."},{"code":"kyc_required","message":"Identity verification has not been started."}]}},"error":null,"correlationId":"01a1b76f88d471ee8a9b10c2bb186fc7"}
HTTP 200

=== practice probe (expect 403)
$ curl http://localhost:5081/api/v1/practice/diagnostics/ping -H Authorization: Bearer <redacted>
{"success":false,"message":"The account is not eligible for this mode.","data":null,"error":{"code":"compliance.not_eligible","details":[{"field":"country_required","message":"A country of residence must be declared."},{"field":"consent_required","message":"Accept the current documents: terms, privacy."}]},"correlationId":"0HNPT0A500M39:00000001"}
HTTP 403

=== real probe (expect 403)
$ curl http://localhost:5081/api/v1/real/diagnostics/ping -H Authorization: Bearer <redacted>
{"success":false,"message":"The account is not eligible for this mode.","data":null,"error":{"code":"compliance.not_eligible","details":[{"field":"country_required","message":"A country of residence must be declared."},{"field":"consent_required","message":"Accept the current documents: terms, privacy."},{"field":"consent_required","message":"Accept the current documents: terms, privacy, risk_disclosure, token_disclosure."},{"field":"kyc_required","message":"Identity verification has not been started."}]},"correlationId":"0HNPT0A500M3A:00000001"}
HTTP 403

=== documents
{
    "success": true,
    "message": "Documents retrieved.",
    "data": {
        "documents": [
            {
                "type": "terms",
                "version": "dev-1",
                "url": "https://askvest.example/legal/terms",
                "sha256": "3cd4ce09a1de6487158536c2633bcdaca51c58a9a3f17dab2343de11c3d69a5a",
                "requiredFor": "practice",
                "accepted": false
            },
            {
                "type": "privacy",
                "version": "dev-1",
                "url": "https://askvest.example/legal/privacy",
                "sha256": "7f391c05d905e1c1d21ae43ab91215b6762ef83266eccb81972d2ef6b9296115",
                "requiredFor": "practice",
                "accepted": false
            },
            {
                "type": "risk_disclosure",
                "version": "dev-1",
                "url": "https://askvest.example/legal/risk-disclosure",
                "sha256": "1fe38db50174c30ad107c1a86ec8d19e9cf3f12d8a1b8e481d582a29dc0e39f6",
                "requiredFor": "real",
                "accepted": false
            },
            {
                "type": "token_disclosure",
                "version": "dev-1",
                "url": "https://askvest.example/legal/token-disclosure",
                "sha256": "54439deda9b18fc787732ee6b2332cf92e9889ecd83b07166b036df81688a419",
                "requiredFor": "real",
                "accepted": false
            }
        ]
    },
    "error": null,
    "correlationId": "01a1b76f89627afe94cf0fa25e729e44"
}

=== accept practice consents
$ curl -X POST http://localhost:5081/api/v1/compliance/consents -H Content-Type:application/json -H Idempotency-Key: 2241ef5ca0534ac18f5170aa69ddc72a -H Authorization: Bearer <redacted> -d {"documents": [{"type": "terms", "version": "dev-1", "sha256": "3cd4ce09a1de6487158536c2633bcdaca51c58a9a3f17dab2343de11c3d69a5a"}, {"type": "privacy", "version": "dev-1", "sha256": "7f391c05d905e1c1d21ae43ab91215b6762ef83266eccb81972d2ef6b9296115"}]}
{"success":true,"message":"Consents recorded.","data":null,"error":null,"correlationId":"01a1b76f8af47dfca02a2f16d99f31b8"}
HTTP 200

=== set country NG
$ curl -X PUT http://localhost:5081/api/v1/compliance/country -H Content-Type:application/json -H Idempotency-Key: 109dc45061ba4e8aa9cbc4d2bed978e6 -H Authorization: Bearer <redacted> -d {"country":"NG"}
{"success":true,"message":"Country recorded.","data":{"country":"NG"},"error":null,"correlationId":"01a1b76f8b4f7d91a56165c0b0d7155f"}
HTTP 200

=== practice probe (expect 200)
$ curl http://localhost:5081/api/v1/practice/diagnostics/ping -H Authorization: Bearer <redacted>
{"success":true,"message":"Probe received.","data":{"mode":"practice"},"error":null,"correlationId":"01a1b76f8b8f743e8cc6bdca82f0fa57"}
HTTP 200

=== real probe (expect 403)
$ curl http://localhost:5081/api/v1/real/diagnostics/ping -H Authorization: Bearer <redacted>
{"success":false,"message":"The account is not eligible for this mode.","data":null,"error":{"code":"compliance.not_eligible","details":[{"field":"consent_required","message":"Accept the current documents: risk_disclosure, token_disclosure."},{"field":"kyc_required","message":"Identity verification has not been started."}]},"correlationId":"0HNPT0A500M3F:00000001"}
HTTP 403

=== accept real consents
$ curl -X POST http://localhost:5081/api/v1/compliance/consents -H Content-Type:application/json -H Idempotency-Key: 101a124d0fa94835929af34645588638 -H Authorization: Bearer <redacted> -d {"documents": [{"type": "risk_disclosure", "version": "dev-1", "sha256": "1fe38db50174c30ad107c1a86ec8d19e9cf3f12d8a1b8e481d582a29dc0e39f6"}, {"type": "token_disclosure", "version": "dev-1", "sha256": "54439deda9b18fc787732ee6b2332cf92e9889ecd83b07166b036df81688a419"}]}
{"success":true,"message":"Consents recorded.","data":null,"error":null,"correlationId":"01a1b76f8c6f78008ea0487d45de3bdd"}
HTTP 200

=== kyc/start
{
    "success": true,
    "message": "Verification session ready.",
    "data": {
        "providerReference": "fake-01a1b76f8ccb7ddc90eab62decd31b17",
        "sessionUrl": "https://kyc.example.test/session/fake-01a1b76f8ccb7ddc90eab62decd31b17",
        "kycStatus": "InReview"
    },
    "error": null,
    "correlationId": "01a1b76f8cba7a7c8fd77bb5c584bf6f"
}
=== approval webhook (signed)
$ curl -X POST http://localhost:5081/api/v1/webhooks/kyc/fake -H Content-Type:application/json -H X-Webhook-Timestamp: 1794078903 -H X-Webhook-Signature: effd0b0fe0b9724f94f27eee4c6bf9c3aac4ac89d858c80ac3258256088dc9bc -d {"eventId":"evt-ev-1794078903","providerReference":"fake-01a1b76f8ccb7ddc90eab62decd31b17","outcome":"approved","verifiedCountry":"NG","fullName":"Evidence User","dateOfBirth":"1990-01-01"}
{"success":true,"message":"Webhook received.","data":null,"error":null,"correlationId":"01a1b76f8e6876608ec09a18627cadb2"}
HTTP 200

=== eligibility after approval
$ curl http://localhost:5081/api/v1/compliance/eligibility -H Authorization: Bearer <redacted>
{"success":true,"message":"Eligibility retrieved.","data":{"practiceAllowed":true,"realAllowed":true,"kycStatus":"Approved","blockers":{"practice":[],"real":[]}},"error":null,"correlationId":"01a1b76f8e9772068565984064224992"}
HTTP 200

=== real probe (expect 200)
$ curl http://localhost:5081/api/v1/real/diagnostics/ping -H Authorization: Bearer <redacted>
{"success":true,"message":"Probe received.","data":{"mode":"real"},"error":null,"correlationId":"01a1b76f8ec0716fb27a61ef53b53afa"}
HTTP 200

=== replay same webhook
$ curl -X POST http://localhost:5081/api/v1/webhooks/kyc/fake -H Content-Type:application/json -H X-Webhook-Timestamp: 1794078904 -H X-Webhook-Signature: 6b898c70a2b06018d9ca6b182fd8d75cc187cf72342fa7970f2dc903657e7f32 -d {"eventId":"evt-ev-1794078903","providerReference":"fake-01a1b76f8ccb7ddc90eab62decd31b17","outcome":"approved","verifiedCountry":"NG","fullName":"Evidence User","dateOfBirth":"1990-01-01"}
{"success":true,"message":"Webhook received.","data":null,"error":null,"correlationId":"01a1b76f8f0e7231b982dcc60f589deb"}
HTTP 200

=== bad signature (expect 401)
$ curl -X POST http://localhost:5081/api/v1/webhooks/kyc/fake -H Content-Type:application/json -H X-Webhook-Timestamp: 1794078904 -H X-Webhook-Signature: 932dad10e7deced2afcc5a8f0d6fe425e7fe9e1089f2e76c68ba2b4c94326136 -d {"eventId":"evt-ev-1794078903","providerReference":"fake-01a1b76f8ccb7ddc90eab62decd31b17","outcome":"approved","verifiedCountry":"NG","fullName":"Evidence User","dateOfBirth":"1990-01-01"}
{"success":false,"message":"The webhook signature is not valid.","data":null,"error":{"code":"webhook.invalid_signature","details":null},"correlationId":"01a1b76f8f66798aa7667f61daff198f"}
HTTP 401

=== stale timestamp (expect 400)
$ curl -X POST http://localhost:5081/api/v1/webhooks/kyc/fake -H Content-Type:application/json -H X-Webhook-Timestamp: 1794075304 -H X-Webhook-Signature: 22d73e43f0278ac2e7084e78088ed5629719eb59078b2c2966e9938752fe32ec -d {"eventId":"evt-ev-1794078903","providerReference":"fake-01a1b76f8ccb7ddc90eab62decd31b17","outcome":"approved","verifiedCountry":"NG","fullName":"Evidence User","dateOfBirth":"1990-01-01"}
{"success":false,"message":"The webhook timestamp is missing or stale.","data":null,"error":{"code":"webhook.stale_timestamp","details":null},"correlationId":"01a1b76f8fb473378a635cd8957267cb"}
HTTP 400
```

### C. Failure drills

Approved user (`practiceAllowed`/`realAllowed` true, `kycStatus=Approved`,
practice ping 200), then `docker stop askvest-smoke-postgres-1`:

```text
=== setup: signup, verify, consents, country, kyc approval
--- eligibility before drill
        "practiceAllowed": true,
        "realAllowed": true,
        "kycStatus": "Approved",
practice ping before: HTTP 200
=== drill: stop postgres
--- eligibility with DB down
{"success":false,"message":"The service is temporarily unavailable.","data":null,"error":{"code":"service.unavailable","details":null},"correlationId":"01a1b770025a721f96a759f395cdb873"}
HTTP 503
--- practice ping with DB down
{"success":false,"message":"The service is temporarily unavailable.","data":null,"error":{"code":"service.unavailable","details":null},"correlationId":"01a1b77002a7723eb18f1283146dbbe8"}
HTTP 503
=== drill: start postgres, wait healthy
postgres health: healthy
--- eligibility after restart
        "practiceAllowed": true,
        "realAllowed": true,
        "kycStatus": "Approved",
--- practice ping after restart
{"success":true,"message":"Probe received.","data":{"mode":"practice"},"error":null,"correlationId":"01a1b77034ee77a885ee62c9685254c9"}
HTTP 200
```

Eligibility and the gate answer 503 `service.unavailable` with no partial
state; after postgres is healthy again the same token is still fully
eligible. No writes succeeded while the database was down.

### D. Editor re-check

- Deleted all `bin`/`obj`, `dotnet restore --locked-mode`: ok.
- `dotnet build -c Debug`: 0 warnings, 0 errors.
- `dotnet build -c Release`: 0 warnings, 0 errors.
- `dotnet format AskVest.slnx whitespace|style|analyzers
  --verify-no-changes`: initially flagged whitespace and import ordering in
  `DocumentEndpoints.cs`/`KycEndpoints.cs`; fixed by `dotnet format`, all
  three verifies now clean.
- Severity table unchanged from Step 1 (naming and qualification rules remain
  suggestion; everything else error). Zero findings at error severity.

### E. Self-audit

| # | Check | Result |
|---|-------|--------|
| 1 | Clean build from scratch, Debug and Release, zero warnings/errors | Pass: both built after deleting every bin/obj |
| 2 | All test layers green, smoke journey fully passed, none skipped | Pass: unit 207, architecture 16, integration 182, smoke 14/14 with stack |
| 3 | Every endpoint covered by integration test and smoke journey | Pass: documents, consents, country, eligibility, kyc/start, webhook, both probes |
| 4 | Architecture guards green, synthetic violations proven | Pass: 16 architecture tests incl. guard violation cases; anonymous allow-list includes the webhook |
| 5 | Error codes and docs in sync, each reached by a test | Pass: not_eligible, consent_version_mismatch, kyc_attempts_exhausted, invalid_signature, stale_timestamp all asserted in integration and/or smoke tests |
| 6 | Logs and database inspected: no names, DOBs, document data, payloads, secrets | Pass: psql dump of compliance tables shows only provider/country/type metadata and body hashes; API logs grepped for every name, DOB, event id and both secrets: 0 hits |
| 7 | compliance_events append-only for the application role | Pass: as `askvest_app`, UPDATE and DELETE are permission denied, INSERT succeeds (probe row removed afterwards as admin) |
| 8 | Real money unreachable when disabled, empty list, or fake provider in production-like config | Pass: evaluator unit tests plus `ComplianceProductionGuardTests` and options validator tests; compose prod sets RealMoneyEnabled=false and no real-money countries |
| 9 | Container hardening re-verified, no new secrets in image history | Pass: secrets only via runtime env; `docker history` grep for secret names/values: 0 |
| 10 | Dependencies audited clean, lock files updated, ADR 0013 | Pass: `dotnet list package --vulnerable --include-transitive` clean on all 10 projects; Compliance adds no new packages (EFCore.NamingConventions, FluentValidation, Npgsql EF, EFCore.Design already in use); no ADR needed |
| 11 | File budget respected | Partial: see deviation note below |
| 12 | grep audit: no suppressions outside .editorconfig, no TODO, no banned APIs, no em dashes | Pass: only EF-generated `#pragma warning disable 612, 618` in migration Designer/Snapshot files, identical to Identity's generated migrations; 0 TODO/FIXME, 0 banned API uses, 0 em dashes in new files |
| 13 | Editor parity: zero findings at lowest severity | Pass: all three dotnet format verifies clean after fixes |
| 14 | Fresh clone follows README alone to healthy stack and passing smoke | Pass by construction: this environment followed exactly the README steps (generate secrets, compose up, run smoke); not re-verified on a literal second clone |
| 15 | Unverifiable items listed | See below |

File budget detail: the module has 50 `.cs` files under
`src/Modules/AskVest.Modules.Compliance`, of which 3 are EF-generated
migration files, leaving 47 hand-written sources versus the ~25 budget. The
overshoot is structural, not speculative: the repo's one-type-per-file rule
plus per-entity EF configurations, six endpoint classes, six services,
three authorization types, four options/configuration types, four entities,
four contracts and three providers account for the count. Exactly three
interfaces exist (`IComplianceEligibility`, `IKycProvider`,
`IScreeningProvider`), as specified. New test files: 12 (11 under
`tests/`, within the ~12 budget). New design doc: exactly one
(`docs/design/compliance.md`); this evidence file is the required
verification record, matching the Phase 1 pattern.

Unverifiable items: real KYC and screening provider integrations (only the
documented adapter contract and fakes exist); remote CI execution (workflow
updated but not run on a runner from this environment); legal confirmation
of the blocked/real-money country lists (configuration only, defaults are
placeholders pending compliance review).

### F. Final report

**Status: Phase 2 complete.**

Phase 1 pieces relied on: `IStepUpAuthorizer`, `IDeletionBlocker`,
`AnonymousEndpointCatalog`, `ReturnsSecretsAttribute`,
`Diagnostics:EnableProbes`, the `strict` rate-limit policy, Mailpit smoke
helpers, module blueprint. Added during this phase: `IUserDirectory` +
`UserDirectoryService` (Phase 1 documented the contract but never shipped
it), `RouteGroupPolicyGuard`, `AnonymousEndpointGuard`, the secrets rule in
`IdempotencyEndpointGuard`, `SchemaAccess.RestrictToAppendOnly`.

File count: 47 hand-written module sources + 3 generated migration files
(50 total) vs ~25 budget; deviation explained in the audit table. Tests:
12 new test files. Rerun: `dotnet test --solution AskVest.slnx` and
`ASKVEST_SMOKE_BASE_URL=http://localhost:5081
ASKVEST_SMOKE_MAILPIT_URL=http://localhost:18025
ASKVEST_COMPLIANCE_KYC_WEBHOOK_SECRET=<from .env>
dotnet test --project tests/AskVest.SmokeTests`.

Deviations: file budget overshoot (structural, documented); feature folders
instead of blueprint layers (already noted in `compliance.md`); the smoke
override relies on `appsettings.Development.json` for the real-money country
list rather than duplicating it in compose. One pre-existing flake noted in
Step 1 (`Maintenance_Removes_Only_Expired_Rows...`) did not recur in the
final full run.

Unverified: real providers, remote CI, legal sign-off on country lists.

Editor: whitespace/import fixes applied; all format verifies clean; naming
and qualification rules at suggestion per Step 1 severity table.

Stack: `cp .env.example .env`, fill secrets per README,
`docker compose -f docker-compose.yml -f docker-compose.smoke.yml up -d
--build --wait`, then the smoke command above.

Phase 3 must know: eligibility is exposed to other modules only through
`IComplianceEligibility` (`GetEligibilityAsync` returns practice/real
allowed plus blocker lists); `/practice` and `/real` route groups already
carry the eligibility policies so Phase 3 endpoints mapped inside them are
gated automatically; account restriction surfaces as the
`account_restricted` blocker and `ScreeningStatus.Hit` on the profile; the
consent/country/KYC tables and `compliance_events` append-only pattern and
per-module migration history are the patterns to copy; the deletion flow
must consult compliance state (blocked country, KYC, screening) when Phase
3 wires `IDeletionBlocker`.
