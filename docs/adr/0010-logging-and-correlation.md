# ADR 0010: Logging and correlation

- Status: Accepted

## Context

Financial APIs must be debuggable without leaking personal data. Correlation ids tie client reports to server logs.

## Decision

Serilog compact JSON to console. Correlation id from `X-Correlation-Id` (validated) or generated UUIDv7, echoed on responses and embedded in every envelope. Never log request bodies, query strings, headers or personal data; log route templates, not raw paths. OpenTelemetry arrives in Phase 17.

## Consequences

Logs are safe to ship to any collector. Support can trace a client-reported correlation id end to end. A dedicated log-redaction integration test guards the policy.

## Alternatives rejected

- Logging request/response bodies: leaks credentials, tokens and PII (T13).
- OpenTelemetry now: premature; revisit at scale in Phase 17.
