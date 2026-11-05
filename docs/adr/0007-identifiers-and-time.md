# ADR 0007: Identifiers and time

- Status: Accepted

## Context

Distributed-safe, sortable identifiers and a single clock prevent an entire class of bugs.

## Decision

UUIDv7 via `Guid.CreateVersion7()` for all ids. `TimeProvider` injected everywhere; UTC only; ISO 8601 with `Z` on the wire. `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.UtcNow`, `Guid.NewGuid()` and `System.Random` are banned in `BannedSymbols.txt`.

## Consequences

Ids are time-ordered (better index locality) and generatable client-side. Tests use `FakeTimeProvider` for deterministic time. Enforced by the banned-API analyzer, not convention.

## Alternatives rejected

- Sequential ints: leak volume, need coordination.
- `Guid.NewGuid()` (v4): random, poor index locality; banned.
- Ambient clocks: untestable and timezone-ambiguous.
