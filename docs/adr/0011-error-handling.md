# ADR 0011: Error handling

- Status: Accepted

## Context

Expected failures (validation, business rules, not found) are normal control flow; exceptions mean something unexpected happened.

## Decision

Expected failures return `Result`/`Error` values from SharedKernel, mapped to status codes and envelope error codes. One global `IExceptionHandler` catches the unexpected: logs with correlation id, returns `server.unexpected` with a fixed generic message. `BadHttpRequestException` maps to `request.malformed`/`too_large`/`unsupported_media_type`. Status code pages envelope empty-body framework responses.

## Consequences

Clients always see the envelope and can branch on `error.code`. Stack traces and internals never reach the wire.

## Alternatives rejected

- Exceptions for expected failures: expensive and encourages catch-and-continue.
- Leaking exception messages: aids attackers (T14, T15 territory).
