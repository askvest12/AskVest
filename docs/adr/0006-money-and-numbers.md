# ADR 0006: Money and numbers

- Status: Accepted

## Context

Financial correctness depends on exact decimal arithmetic. Binary floating point cannot represent money safely.

## Decision

`decimal` everywhere in money paths, stored as `numeric(28,18)`. JSON serializes money and quantities as strings via a strict converter that rejects numbers, exponents and thousands separators. One rounding helper with an explicit mode; display rounding is separate from ledger rounding.

## Consequences

No float/double in money paths (enforced by review and grep audits). Clients must send strings, which is unambiguous across languages.

## Alternatives rejected

- `double`/`float`: rounding errors compound into real money bugs.
- JSON numbers for decimals: parsers differ on precision; strings are exact.
- Per-call-site rounding: silent inconsistencies; one helper forces explicit choices.
