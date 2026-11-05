# ADR 0012: Configuration and secrets

- Status: Accepted

## Context

Configuration errors should fail at startup, not at first request. Secrets must never live in the repo, images or logs.

## Decision

Environment-based configuration with options classes validated on start (`ValidateOnStart`, data annotations). Diagnostic probes refuse to start in Production. Production secrets come from a secret manager (decided in Phases 16-17). `.env` is gitignored; `.env.example` documents keys without values.

## Consequences

Misconfiguration stops the deploy loudly. Local dev uses `.env` generated with random passwords; nothing real is committed.

## Alternatives rejected

- Runtime-validated config: errors surface mid-request instead of at boot.
- Secrets in appsettings or env files committed to git: violates T17.
