# Identity Module

Owns users, credentials, sessions, devices, one-time codes, throttling, step-up grants, security events and account lifecycle. Schema `identity`. Route prefix `/api/v1` with `/auth` and `/account` groups.

## Flows

### Sign-up and email verification

```mermaid
sequenceDiagram
  participant C as Client
  participant A as API
  participant M as Mail
  C->>A: POST /auth/sign-up {email, password}
  A->>A: normalize email, validate password policy
  A->>A: branch on account state (pending, active, missing)
  A->>M: queue verification code email (pending only)
  A-->>C: uniform success envelope
  C->>A: POST /auth/verify-email {email, code}
  A->>A: constant-time compare, attempt count, expiry
  A-->>C: success envelope (no tokens)
  C->>A: POST /auth/login
```

### Login with device verification

```mermaid
sequenceDiagram
  participant C as Client
  participant A as API
  participant M as Mail
  C->>A: POST /auth/login {email, password, device}
  A->>A: throttle check, credential check
  alt trusted device
    A-->>C: authenticated {access, refresh, session}
  else unknown device
    A->>M: queue device verification code
    A-->>C: device_verification_required {challengeId}
    C->>A: POST /auth/verify-device {challengeId, code}
    A-->>C: authenticated {access, refresh, session}
  end
```

### Refresh with reuse detection

```mermaid
sequenceDiagram
  participant C as Client
  participant A as API
  C->>A: POST /auth/refresh {refreshToken, installationId}
  A->>A: hash lookup under row lock
  alt valid
    A->>A: rotate token, new pair, same transaction
    A-->>C: new token pair
  else rotated token replayed inside grace, same device
    A-->>C: 409 auth.refresh_conflict
  else rotated token replayed otherwise
    A->>A: revoke session, security event, alert email
    A-->>C: 401 auth.refresh_invalid
  end
```

### Step-up consumption

```mermaid
sequenceDiagram
  participant C as Client
  participant M as Module
  participant A as API
  C->>A: POST /auth/step-up {pin, purpose, bindingHash}
  A-->>C: single-use grant token (120s)
  C->>M: business call carrying grant
  M->>A: IStepUpAuthorizer.ConsumeAsync(user, session, purpose, binding)
  A->>A: atomic check owner, session, purpose, binding, expiry, single use
  A-->>M: Result success or auth.step_up_invalid
```

## Data model

Tables in schema `identity`: `users`, `user_roles`, `devices`, `sessions`, `refresh_tokens`, `one_time_codes`, `throttle_buckets`, `step_up_grants`, `security_events`. Conventions per `02-data-standards.md`: UUIDv7 ids, UTC timestamps, snake_case, optimistic concurrency on mutable tables, no cross-module foreign keys. `security_events` is append only: the application role loses update and delete rights in the same migration.

## State machines

- User status: `pending_verification` to `active` on code verification; `active` to `deletion_requested` to `anonymized`; `disabled` set by later admin tooling.
- Session: created to revoked (reason recorded). No un-revoke.
- Device: untrusted to trusted on first verified login or device code; trusted to revoked.
- One-time code: issued to consumed or expired; attempts increment atomically; single use.
- Step-up grant: issued to consumed or expired; single use, 120 seconds.

## Limits

All limits from the phase specification section 4 are configurable under the `Identity` configuration section and validated at startup: token lifetimes, code lifetime and attempts, resend cooldown and hourly cap, throttle thresholds and doubling, PIN rules and lock, session cap, deletion cooling-off, password bounds.

## Contracts (namespace `AskVest.Modules.Identity.Contracts`)

- `ICurrentUserContext`: user id, session id, device id, roles for the current request.
- `IStepUpAuthorizer`: consume a grant for a purpose and optional binding hash, returns `Result`.
- `IAccountDeletionBlocker`: modules register blockers; identity calls all during deletion request.
- `IUserDirectory`: confirm a user id exists and is active.
