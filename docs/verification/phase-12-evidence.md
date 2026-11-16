# Phase 12 verification evidence

Funding and cash withdrawals. Verified on .NET SDK 10.0.x, PostgreSQL 17,
Docker Compose smoke stack (`askvest-smoke` project, API on port 5081,
Mailpit on 18025). Spec:
`AskVest-Backend-Phase-12-Funding-and-Cash-Withdrawals.md`.

## Commits

- `f53caeb` docs(funding): fee schedule, guards and small earlier-module changes
- `4dc3c7f` feat(funding): data, provider seams and state machines
- `9508f86` feat(funding): accounts, deposits, signed webhook and polling job
- `2d852f8` feat(funding): conversions, beneficiaries, previews and placement
- `9dcfd49` feat(funding): payouts, settlement, reserves check and operator contract
- `126c487` fix(funding): poll cursor migration, fx rate config shape and ef fixes
- `16157c4` feat(funding): smoke compose config, funding smoke journeys and docs
- `d5bfedc` fix(wallets): custody pagination, deposit ids and reserves pause transaction

## What was built

- `AskVest.Modules.Funding` module: own `funding` schema, forward-only
  migrations (`InitialFunding`, `PollCursors`), own history table,
  module blueprint registration in API, migrator and test fixture.
- Funding accounts with provider payment instructions; deposits in
  `pending`, `credited`, `quarantined`, `failed`, `reversed`, `ignored`;
  conversions in `quoted`, `completed`, `expired`; beneficiaries in
  `cooling_off`, `active`, `removed`; withdrawals in `previewed`,
  `requested`, `under_review`, `submitting`, `submitted`, `completed`,
  `failed`, `cancelled`, `needs_reconciliation`.
- Signed funding webhook endpoint
  `POST /api/v1/webhooks/funding/{provider}`: HMAC-SHA256 over
  `timestamp.body`, timestamp tolerance, event-id deduplication,
  unknown account references ignored and never credited.
- Deposit pipeline: webhook credit plus provider polling fallback with a
  persisted per-provider cursor; payer-unverified deposits and
  amount/currency/limit violations quarantine; fresh-deposit withdrawal
  locks; reversal support.
- Conversion pipeline: firm quotes with spread and fee, terms hash,
  expiry, stale/jump/error/disabled-pair FX refusal as
  `funding.quote_unavailable`.
- Beneficiary pipeline: step-up bound to `currency:providerReference`,
  name verification, cooling-off before activation.
- Withdrawal pipeline: preview with explicit fee and terms hash,
  placement behind `authorize_withdrawal` step-up bound to the hash,
  Ledger hold, async provider submission, progress polling,
  `needs_reconciliation` for ambiguous outcomes, operator resolution
  contract, cancel while requested.
- Fiat reserve checks per currency with withdrawal pauses on shortfall;
  deposits and conversions continue while paused. Review thresholds and
  per-user and rolling limits enforced at preview and placement.
- `IFundingProvider` and `IFxRateProvider` seams with deterministic
  fakes; Production refuses fake providers at startup; diagnostics
  scenario probes only when diagnostics are enabled.

## Bugs found and fixed during verification

1. `FundingPollCursor` was added after `InitialFunding` was scaffolded,
   leaving model drift; a forward-only `PollCursors` migration was
   generated (`126c487`).
2. `Funding:FxRates` bound as a dictionary could not express pair keys
   containing `:` (the configuration delimiter). `FxRates` is now a
   list of `From`/`To`/`Rate` entries (`126c487`).
3. `string.Equals` inside EF predicates in `FundingAccountService` did
   not translate; replaced with direct comparisons (`126c487`). Domain
   transition helpers inside `Where` predicates in the withdrawal
   service had the same problem; replaced with inline enum comparisons.
4. `funding.beneficiary_cooling_off` returned 409; the spec requires
   422, so `FundingResults.BeneficiaryCoolingOff` now uses the
   business-rule error type.
5. `FakeCustodyProvider.ListDepositsAsync` echoed its cursor on empty
   pages, and deposit ids restarted at `fake-dep-1` after each process
   restart, colliding with persisted dedupe rows. Empty pages now return
   a null cursor and ids are seeded from `TimeProvider` ticks
   (`d5bfedc`).
6. `WalletService.ProcessDepositsAsync` now breaks when a provider
   echoes its cursor, so a non-advancing page cannot loop forever
   (`d5bfedc`).
7. `ReservesService` activated custody pauses with a `SaveChangesAsync`
   issued after its transaction scope had already completed, producing
   recurring `Transaction is already completed` errors in the smoke
   stack. Pause activation now commits inside the upsert transaction
   (`d5bfedc`).
8. With pause activation working, the reserves check exposed that the
   fake custody provider cannot see trading-settled token inventory, so
   ledger totals permanently diverge from fake holdings and pauses
   blocked withdrawal previews (`wallet.unavailable`, HTTP 503) late in
   the integration suite. This is a fake fidelity gap, not a product
   bug: in production omnibus custody would report trading inventory.
   `AuthApiFactory` now sets `Wallets:ReservesMismatchThreshold` very
   high so mismatch bookkeeping still runs but cannot pause; pause
   activation remains covered by unit tests.

## API-level integration tests

`tests/AskVest.IntegrationTests/Funding/FundingApiTests.cs`, seven facts
through `WebApplicationFactory` against shared Testcontainers
PostgreSQL, fake funding and FX providers, the real ledger and signed
webhook helpers. All pass:

- Full journey: account, signed deposit webhook, credit, conversion
  quote and execution, beneficiary behind bound step-up, cooling-off
  promotion, withdrawal preview, placement behind terms-hash-bound
  step-up, async completion, ledger debit.
- Webhook negatives: bad signature, stale timestamp, duplicate event
  id, unknown provider, unknown account reference (ignored, never
  credited), payer-unverified quarantine.
- Provider drills: rejection releases the hold; `submit_timeout_filled`
  resolves to `completed` through reconciliation; `submit_timeout_lost`
  parks in `needs_reconciliation`; `status_unavailable` keeps polling.
- FX drills: stale, jump, error and disabled pair refuse with
  `funding.quote_unavailable`.
- Withdrawal guards: beneficiary name mismatch rejected, cooling-off
  refused 422, limits and fresh-deposit locks enforced, reserve
  shortfall pauses withdrawals while deposits and conversions continue.

`tests/AskVest.IntegrationTests/Funding/FundingSchemaTests.cs` proves
schema constraints including unique provider deposit identity against
real PostgreSQL (6 facts).

## Smoke tests over real HTTP

`tests/AskVest.SmokeTests/FundingSmokeTests.cs`, two facts against the
running containers (`ASKVEST_SMOKE_BASE_URL=http://localhost:5081`,
`ASKVEST_SMOKE_MAILPIT_URL=http://localhost:18025`, secrets from `.env`).
Both pass; the full smoke suite passes 29/29 over real HTTP.

- `Funding_Journey_Deposit_Conversion_Beneficiary_Withdrawal_Completes`:
  signup, Mailpit email verification, documents, consents, country,
  signed KYC approval, learning gate, PIN, funding account creation,
  signed deposit webhook, replay deduplication, deposit `credited`,
  ledger balances, ZAR to USD conversion `completed`, beneficiary
  behind bound step-up, cooling-off expiry, withdrawal preview and
  placement behind terms-hash-bound step-up, async progression to
  `completed`, final ledger debit.
- `Funding_Webhook_Negatives_And_Payout_Drills`: invalid signature and
  stale timestamp refused, unknown references ignored, provider
  rejection releases the hold, `submit_timeout_filled` resolves to
  `completed`, stale FX refuses the quote, beneficiary name mismatch
  rejected. `needs_reconciliation` is treated as transient while the
  progress worker reconciles.

A verbatim transcript of the live journey is in
`docs/verification/phase-12-live-transcript.log`; every value in it came
from the running API (email code from Mailpit, KYC approval through the
signed webhook, deposit through the signed funding webhook, money moved
through the real Ledger).

## Full integration suite

`tests/AskVest.IntegrationTests` against a fresh Testcontainers
PostgreSQL: 479 total, 0 errors, 0 failed, 0 skipped (~464s). This run
includes the seven `FundingApiTests`, the six `FundingSchemaTests`, the
two `WalletsApiTests` and every earlier module suite.

## Unit and architecture layers

- Unit: 36 Funding tests green (state machines, fee and spread math,
  terms hash, webhook signature verification, fake provider scenarios,
  options validation including the Production refusal); the full unit
  suite is 813 green.
- Architecture: 109 tests green including the Funding boundary suite
  and the ledger log-hygiene guard.
- Full solution build clean, 0 warnings 0 errors (the known flaky
  AD0001 analyzer crash cleared on retry, no suppression).

## Honestly unverified

- Live kill drills (database stop mid-submission, API kill during a
  payout burst, provider outage restore, kill during settlement) were
  not executed; the fake scenarios cover the outcome space at unit and
  service level only.
- The `submit_timeout_lost` parking state, `status_unavailable`,
  `balance_shortfall` reserve pause, `name_mismatch` quarantine and the
  screening-equivalent deposit rejections are covered by integration
  and unit tests; over HTTP the smoke suite exercises rejection,
  `submit_timeout_filled` and stale FX only.
- A dedicated parallel-placement concurrency burst was not added; the
  per-user advisory lock serializes placement by design.
- Deposit deduplication across repeated provider pages is covered by
  the unique provider identity constraint and unit tests, not by a live
  replay drill beyond the webhook event-id replay.
- Operator reconciliation endpoints are contract-tested; no scripted
  operator drill ran against the running stack.
