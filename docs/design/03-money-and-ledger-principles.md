# 03. Money and Ledger Principles

Design only in Phase 0; built in Phase 3.

## Double entry

Every journal entry set sums to zero per asset. A trade is at least two lines (debit one account, credit another) plus explicit lines for fees, spreads and FX where they apply. Ledger rows are immutable: the `askvest_app` role gets no UPDATE or DELETE on ledger tables.

## Books

Two books: Practice and Real. They can never post to each other; the separation is enforced by schema/module boundaries, route groups and tests that block crossover (threat T22).

## Balances

Balances are either derived by summing journal lines or maintained transactionally with invariant checks (non-negative where required, per-asset sum zero). Phase 3 decides row locking versus serializable isolation for balance changes.

## Idempotent posting

Every posting carries an idempotency key under a unique constraint inside the same database transaction as its effects. A retried command replays the stored result instead of double posting (threat T2, T3).

## Rounding

One rounding helper with an explicit mode parameter. Rounding is never silent: the mode is chosen at the call site and documented. Display rounding is separate from ledger rounding; the ledger always rounds at `numeric(28,18)` precision.

## Fees, spreads, FX, quote expiry

Each is an explicit ledger line, never netted invisibly inside a price. FX conversions post a conversion journal with the rate snapshot stored. Quote expiry is a state on the order, not a ledger concept.

## Reconciliation

Provider balances and transactions are reconciled against ledger state on a schedule (Phase 15 tooling). Discrepancies create reconciliation records, never silent adjustments.

## Audit

Stored for audit: journal entries with idempotency keys, account snapshots at posting time, provider references, actor identity, and timestamps. Ledger rows are retained even when personal data is anonymized.
