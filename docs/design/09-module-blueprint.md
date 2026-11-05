# Module Blueprint

Every business module follows this template. Phase 1 implements it for Identity.

## Layout

- Project: `src/Modules/AskVest.Modules.<Name>` referencing only `AskVest.SharedKernel` and `AskVest.Platform`.
- Internal folders: `Domain`, `Application`, `Infrastructure`, `Endpoints`, `Contracts`.
- `Contracts` is the only namespace other projects may reference.
- One `Add<Name>Module` service-registration extension and one `Map<Name>Endpoints` endpoint-mapping extension.
- One `DbContext` per module, its own schema and its own EF history table, registered with the shared migration runner.
- Routes under `/api/v1/<module>`; OpenAPI tag per module.
- Per-module design doc at `docs/design/modules/<name>.md`.
- Tests live in the shared test projects under folders named after the module.

## Rules

1. Default-deny authorization: every endpoint requires an authenticated active account unless listed on the anonymous allow-list with a written reason.
2. Every query for a user-owned resource filters by owner; foreign ids answer `resource.not_found`.
3. Endpoints returning secrets carry the returns-secrets tag and are exempt from idempotency replay storage; the tag and the idempotency requirement may never combine.
4. Migrations are forward-only and call the shared schema provisioning helper before creating objects.
5. The module never logs personal data, secrets, tokens or codes.
