# Phase 10 verification evidence

Real Order Engine. Verified on .NET SDK 10.0.114, PostgreSQL 17,
Docker Compose smoke stack (`askvest-smoke` project, API on port 5081,
Mailpit on 18025). Spec: `AskVest-Backend-Phase-10-Real-Order-Engine.md`.

## Commits

- `aeb40a2` docs(trading): order machine, gateway contract, guards and module scaffold
- `2ea649d` feat(trading): schema, configuration, state machine and fake gateway
- `7945bc6` feat(trading): quotes, order placement and async settlement
- `906c383` test(smoke): trading stack wiring, probes and auth rate-limit retry
- `e1191e8` fix(trading): keep request validators internal to the module surface
- final: docs(trading): phase 10 verification evidence

## Step 8A: smoke journey over real HTTP

`tests/AskVest.SmokeTests/TradingSmokeTests.cs`, three facts against the
running containers (`ASKVEST_SMOKE_BASE_URL=http://localhost:5081
ASKVEST_SMOKE_MAILPIT_URL=http://localhost:18025
dotnet test --project tests/AskVest.SmokeTests --filter TradingSmokeTests`).
All passed in one run (30s).

Covered:

1. `RealOrder_BuyThenSell_SettlesExactly`: signup through Mailpit verify,
   real documents, consents, country NG, KYC approval webhook, learning
   gate, PIN, USD ledger credit through the diagnostics post, catalog
   token discovery, firm quote (`state=open`, `settlementAsset=USD`,
   `termsHash` present), step-up grant bound to the hash, order
   placement returns `pending_submission`, same-key replay returns the
   same order id, a second placement on the used quote returns the same
   order (one order per quote), order reaches `executed`, USD available
   drops by exactly the quoted total, held returns to zero and the token
   balance equals the quoted quantity. A sell-all quote then buys back
   to zero tokens with no residual holds.
2. `RealOrder_Rejected_Releases_And_MismatchedFill_KeepsHold`: scenario
   `rejected` drives the order to `failed` with USD held back to zero;
   scenario `fill_mismatch` drives it to `needs_reconciliation` with USD
   held greater than zero and no tokens delivered.
3. `RealOrder_RequiresBoundStepUp_And_HidesOtherUsersQuotes`: a grant
   bound to different terms is refused `auth.step_up_invalid`; an
   unauthenticated quote read is 401; another eligible user's read of the
   quote is 404 `resource.not_found`; a user without real eligibility
   gets 403 on quote creation.

## Step 8B: integration coverage against real PostgreSQL

`tests/AskVest.IntegrationTests/Trading/TradingApiTests.cs` runs the same
journeys in-process through `WebApplicationFactory` against the shared
Testcontainers PostgreSQL, with catalog import, fake market data poller,
fake KYC provider and the real ledger. Five facts, all passing:

- buy then sell settles exactly with balance assertions and idempotent
  replay;
- rejected submission fails and releases the hold;
- mismatched fill parks in `needs_reconciliation` keeping the hold;
- step-up binding, cross-user `resource.not_found` and eligibility 403;
- below-minimum order refused `trading.order_invalid`, invalid side and
  unauthenticated quote refused.

`tests/AskVest.IntegrationTests/Trading/TradingSchemaTests.cs` (step 3)
proves migration idempotency, unique quote-id and user+key constraints,
app-role read/write grants, append-only `trading_events` (UPDATE and
DELETE answer SQLSTATE 42501) and no DDL for the app role.

Unit layer: 647 tests green, including transition table, terms hash, fee
math, limit evaluation, fill comparison, gateway scenarios and options
validation. Architecture layer: 89 tests green including the Trading
boundary suite (no chain-specific code, gateway usage limited to quote,
dispatcher and progress job, `/api/v1/real` routes carry the real policy,
no external references to Trading entities, Assistant has no Trading
reference). One real finding was fixed: the two FluentValidation
validators were public and leaked into the module surface; they are now
internal (`e1191e8`).

## Step 8C: raw evidence

`docs/verification/phase-10-raw-http.log` shows the OpenAPI surface
(`POST /api/v1/real/quotes`, `GET /api/v1/real/quotes/{id}`,
`POST /api/v1/real/orders`, `GET /api/v1/real/orders`,
`GET /api/v1/real/orders/{id}`, `POST /api/v1/real/orders/{id}/cancel`,
`GET /api/v1/real/balances`, `POST /api/v1/diagnostics/trading-scenario`)
and unauthenticated calls answering 401 `auth.unauthenticated`.

## Step 8D: failure drills

Not executed as live kill drills. Partial coverage instead: the fake
gateway scenarios `submit_timeout_lost`, `submit_timeout_filled`,
`status_unavailable`, `slow_submit`, `delayed_fill`, `quote_error`,
`rejected` and `fill_mismatch` are implemented and unit tested; the
submission handler treats unknown results as reconciliation and repeated
delivery cannot double-submit (order id is the gateway idempotency key);
the progress job only settles exact matches. Live drills (database stop
during placement, API kill mid-burst, gateway outage restore, kill during
settlement) are listed as unverified below.

## Audit checks (spec F)

| # | Result | Evidence |
|---|--------|----------|
| 1 | Pass | `dotnet build` Debug and Release clean, 0 warnings 0 errors (transient AD0001 analyzer crashes cleared on retry, no suppression). |
| 2 | Pass with note | Unit 647, Architecture 89, Integration 452 full suite plus 5 new TradingApiTests green in filtered runs; full-suite rerun including them was in progress at report time (see unverified). Smoke 25/25 with env set. |
| 3 | Pass | Quotes create/get, orders place/list/get/cancel, balances and the scenario probe are covered by TradingApiTests and TradingSmokeTests. |
| 4 | Pass | `OrderTransitionsTests` prove every legal and illegal transition; submission (`submitting`/`submitted`) and execution (`executed`) are separate states proven in smoke and integration. |
| 5 | Pass | Step-up grant must be fresh, correct purpose and bound to the quote `termsHash`; wrong binding refused `auth.step_up_invalid` (smoke + integration). Expired and replayed grant paths are covered by unit tests of `StepUpService` consumption semantics. |
| 6 | Pass | Quote state `open`/`used`/`expired` enforced at placement; used quote returns the existing order; expiry checked against `TimeProvider`. |
| 7 | Pass | Hold, fill and release journals follow `docs/design/05-ledger-module.md` patterns via `OrderJournals`; smoke asserts balances equal quoted totals exactly. |
| 8 | Pass | `FillComparison` requires exact equality on quantity, price, gross, gateway fee and total; `fill_mismatch` scenario parks in reconciliation with the hold kept (smoke + integration). |
| 9 | Pass | `submit_timeout_lost`, `status_unavailable` and timeouts route to `needs_reconciliation` without releasing holds (unit + service logic; live drill not run). |
| 10 | Pass with note | Held equals reserved is asserted via balances in smoke and integration after each scenario; one journal per outcome enforced by idempotency keys `real:order:<id>:hold|fill|release`. |
| 11 | Partial | Per-user advisory lock (`LockUserAsync`) serializes placement; limits and open-order cap evaluated inside the lock. A dedicated parallel-burst test was not added; behavior covered by lock design and sequential tests. |
| 12 | Pass | Min/max/rolling/open-order limits (`LimitEvaluationTests` + `trading.order_invalid` integration), buy/sell switches, catalog availability, `RealEligible` policy and the market-data deviation guard (`MaxQuoteDeviationPercent`) all enforced; sells skip review-expiry and country blockers per `AvailabilityEvaluator`. |
| 13 | Pass | `AppRole_CannotUpdateOrDeleteTradingEvents` proves UPDATE and DELETE answer 42501. |
| 14 | Pass | Module logs ids, states and codes only; grep of API logs during smoke runs shows no amounts, tokens or grant values. |
| 15 | Pass | Boundary tests prove no chain-specific code; fake gateway and scenario probe refuse Production; gateway key and reference never serialized (response DTOs carry catalog identity only). |
| 16 | Pass | `trading.not_available`, `trading.order_invalid`, `trading.quote_unavailable`, `trading.quote_invalid` added to `ErrorCodes`, the catalogue and design docs; each is reached by tests. |
| 17 | Pass | Container hardening unchanged; `dotnet list package --vulnerable` clean; lock files updated. |
| 18 | Deviation | Trading module has 45 source files vs about 30 budgeted (same one-type-per-file pattern as earlier phases); 11 trading test files vs about 16 (under budget). Zero new docs beyond mandated evidence. |
| 19 | Pass | No suppressions outside `.editorconfig` (one CA1031 entry with reason for the progress job), no `TODO`/`FIXME`, no banned APIs, no em dashes, money paths use `decimal` only. |
| 20 | Pass | `dotnet format --verify-no-changes` clean on touched files; editor findings fixed during development (import ordering, nullable handling, accessibility). |
| 21 | Partial | The smoke stack was rebuilt from the working tree and all 25 smoke tests pass; a fresh-clone README-only run was not repeated this phase. |
| 22 | Listed | See unverified items. |

## Unverified items

- Live failure drills from spec section D (database stop during
  placement, API kill mid-burst, gateway outage restore, kill during
  settlement) were not executed; scenario-level equivalents are covered
  by tests but the kill/restart drills remain open.
- A dedicated parallel-placement concurrency test (same quote, overdraw,
  cancel versus dispatch, duplicate outbox delivery, two progress jobs)
  was not written; the per-user advisory lock and idempotency design cover
  the mechanism but the burst proof is pending.
- Full integration suite rerun including `TradingApiTests` (452 + 5)
  was still executing at report time; the 452-test suite and the 5 new
  tests each passed in their own runs.
- No real issuer gateway exists; the fake gateway proves the contract
  only. Production rejects `Provider=fake`.
- Remote CI run not executed locally.

## Final report

**Phase 10 status: incomplete** - the implementation, unit, integration
and real HTTP smoke coverage are green, but the spec's live failure
drills (section D) and the dedicated parallel-placement concurrency
test (audit row 11) were not executed, so not all 22 audit rows pass
with evidence.

Launch blockers that remain, stated plainly: a real gateway adapter and
its contract tests, legal and licensing sign-off, limits reviewed by
compliance, the operator tools for reconciliation cases from Phase 15,
the real-model evaluation from Phase 9, and an independent security
review of identity, ledger and trading recommended before real money.
Real money must not be enabled until these are cleared.

File counts: 45 Trading source files (budget about 30, deviation noted),
11 trading test files (budget about 16). Test counts: Unit 647,
Architecture 89, Integration 457 (452 suite + 5 trading API facts),
Smoke 25. Rerun: `dotnet test --solution AskVest.slnx`; smoke needs
`ASKVEST_SMOKE_BASE_URL`, `ASKVEST_SMOKE_MAILPIT_URL` and
`ASKVEST_COMPLIANCE_KYC_WEBHOOK_SECRET` exported.

Deviations: module file count over budget; `SubmittedTimeoutSeconds`
validator floor is 60s so the smoke override uses 60; the assistant
evaluation smoke helper now retries on the shared strict auth window;
used-quote placement returns the existing order rather than an error
(one order per quote is the idempotency boundary).

What later phases must know: `trading_events` is the append-only order
event stream for Phase 13; `needs_reconciliation` orders hold user funds
until Phase 15 operator tooling resolves them; settlement journals use
references `real:order:<orderId>:hold|fill|release`; the `IIssuerGateway`
contract and `FakeIssuerGateway` scenarios are the seam a real adapter
must implement; Phase 11 must treat the ledger balance as the user's
claim and handle token withdrawals.

Real money must not be enabled until the launch blockers in this report
are cleared. Request Phase 11 (Wallets and Custody Abstraction).
