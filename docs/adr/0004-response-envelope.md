# ADR 0004: Response envelope

- Status: Accepted

## Context

Mobile clients need one predictable response shape, including for framework-level errors (404, 405, 429, 500).

## Decision

Every response uses the envelope `{ success, message, data, error, correlationId }`. `error.code` is the machine-readable contract; `message` is for humans. See `docs/design/01-api-standards.md`.

## Consequences

Clients branch on `error.code` only. Framework errors are normalized through status code pages and the exception handler so nothing escapes the envelope.

## Alternatives rejected

- RFC 7807 problem details: two shapes (success and error) plus per-endpoint error formats would complicate mobile parsing.
- Bare DTOs: inconsistent errors, no correlation id on failures.
