# Phase 6 verification evidence

Practice Mode. Verified on .NET SDK 10.0.114, PostgreSQL 17, Docker
Compose smoke stack (`askvest-smoke` project, API on port 5081).

## Step 1: orientation and baseline

Read: `CLAUDE.md`, all files in `docs/design/`, Phase 0 to 5 evidence, and
`AskVest-Backend-Phase-6-Practice-Mode.md` in full.

Baseline on the Phase 5 tip: restore `--locked-mode` OK, scratch Debug and
Release builds 0 warnings 0 errors, `dotnet format --verify-no-changes`
clean, full suite green (350 unit, 36 architecture, 323 integration,
17 smoke with env set).

## Step 2: design docs, guards and module scaffold

- Design docs updated for practice accounts, orders, stops, reset policy,
  the `P.` asset namespace and the `PracticeOrderFilled` event.
- `ErrorCatalogue` gained the practice codes: `practice.market_unavailable`,
  `practice.insufficient_position`, `practice.order_invalid`,
  `practice.reset_not_allowed`, `practice.too_many_open_stops`.
- Catalog importer now rejects any token asset code starting with `P.`
  (prefix reserved for practice-mapped assets).
- `FakeMarketDataProvider` gained the `drop` scenario used by the stop
  trigger path and drills.
- Scaffolded `src/Modules/AskVest.Modules.Practice` plus
  `PracticeBoundaryTests` in the architecture suite.
- Commit: `docs(practice): design, guards and small earlier-module changes`.

## Step 3: schema, migration, configuration, module wiring

- `practice` schema with `practice_accounts`, `practice_orders`,
  `practice_events`; own EF history table; forward-only migration;
  `practice_events` is append-only for the application role.
- `PracticeOptions` validated at startup: starting credits, fee basis
  points, quantity scale, min/max order amount, max open stops, stop
  evaluation interval, reset cooldown and rolling reset limit.
- Module wired into Api and Migrator behind the `PracticeEligible`
  policy on `/api/v1/practice`.
- Commit: `feat(practice): schema, configuration and module wiring`.

## Step 4: account, reset, positions

- `POST /api/v1/practice/account` grants the one-time starting credits
  (default 10000 USD) exactly once per user through the real ledger
  (`Book.Practice`); replay returns the stored response.
- `POST /api/v1/practice/account/reset` cancels open orders, releases
  holds, sweeps Practice balances, reissues credits and enforces the
  cooldown plus rolling 30 day reset limit.
- `GET /api/v1/practice/balances`, `GET /api/v1/practice/positions` and
  `GET /api/v1/practice/ledger/entries` read the Practice book only.
- Commit: `feat(practice): account, reset and positions`.

## Step 5: market orders, order reads, contract

- `POST /api/v1/practice/orders` places market buys (USD amount in,
  quantity out at the usable price minus fee) and market sells
  (quantity or sell-all). Fills are synchronous ledger journals.
- `GET /api/v1/practice/orders` cursor-paginated, owner filtered;
  `GET /api/v1/practice/orders/{orderId}` returns `resource.not_found`
  for another user's order; `POST .../cancel` cancels open stops.
- `IPracticeOrders` public contract plus `PracticeOrderFilled` outbox
  event for every filled order.
- Commit: `feat(practice): market orders, order history and contract`.

## Step 6: simulated stop-loss and evaluator

- Stop-loss orders are sell-only, require a stop price below the current
  usable price, hold the quantity in `user_held`, cap open stops per
  user, and fill at the current usable price when it reaches the stop
  (gaps and slippage visible in the fill price).
- `PracticeStopEvaluator` hosted service: PostgreSQL advisory lock
  `2026110802L`, keyset paging over `(created_at, id)` in batches of
  200, fresh DI scope per order, per-order failure isolation,
  `FOR UPDATE` order lock inside the transaction so trigger versus
  cancel races resolve exactly once.
- Commit: `feat(practice): simulated stop-loss and evaluator`.

## Step 7: compose, README, CI

- `Practice__*` settings in `docker-compose.yml`; smoke override uses
  50 basis point fee, 2 second stop interval and a sub-hour reset
  cooldown (0.001 hours, about 4 seconds) so the journey is observable.
- README documents Practice mode, credits, `P.` namespace, order and
  stop behavior, reset policy and smoke commands.
- OpenAPI transformer documents the required `Idempotency-Key` header on
  every mutating endpoint (23 of 29 operations carry it; the rest are
  anonymous or exempt by design). Scalar lists all nine Practice
  routes.
- CI already runs the whole smoke suite.
- Commit: `chore(practice): compose, readme and ci`.

## Step 8: smoke journey, drills, audit

### A. Smoke journey (`tests/AskVest.SmokeTests/PracticeSmokeTests.cs`)

One journey over real HTTP covering all ten spec steps: double account
start yields one 10000 grant; buy 1000 USD with 0.5 percent fee and
cash reduction; sell half then sell-all; three ordered history entries;
idempotent replay returns the identical response with no extra order;
stop-loss holds quantity, cancel releases it; `drop` scenario fills the
stop below its stop price; `halted` answers `practice.market_unavailable`
with the label in details; `ledger.insufficient_funds`,
`practice.insufficient_position` and `practice.order_invalid` reached;
twenty parallel buys never overdraw; reset restores 10000 with empty
positions and an immediate second reset answers
`practice.reset_not_allowed`; a second user cannot see the first user's
orders; Real routes answer 403 before KYC; Practice entries never appear
in Real balances. Result: 18/18 smoke tests pass, 0 skipped, run twice
against the main stack and once against a fresh clone.

### B. Raw HTTP evidence

`docs/verification/phase-6-raw-http.log` records the key calls: account
start and replay, market buy and sells, idempotent replay, stop place
and cancel, stop trigger under `drop` (stop about 30.22, fill 25.36,
state `filled`), `halted` rejection, the three validation errors, reset
and balances.

### C. Failure drills

1. Database stopped mid-order: `POST /api/v1/practice/orders` answered
   HTTP 503 `service.unavailable`; after restart the account cash and
   order count were unchanged. No partial effects.
2. Burst of parallel orders with the API killed abruptly mid-flight:
   in-flight requests returned connection errors as expected; after
   restart, ledger reconciliation reported zero balance mismatches, zero
   non-zero book/asset nets, zero filled orders without a fill journal,
   zero stuck market orders and zero missing hold journals.
3. API killed while stop triggers were pending (15 open stop orders,
   `drop` scenario armed): after restart the evaluator resumed; each of
   the 15 orders ended `filled` exactly once - 15 `stop_triggered`
   events, 15 distinct fill journals, zero duplicate fill journals, zero
   missing journals. Final reconciliation clean. Note: the fake
   provider's scenario is in-memory, so the drop had to be re-armed
after the restart for the remaining orders to trigger; this also
   proved open stops simply wait when the price is not usable.

### D. Editor re-check

`dotnet format AskVest.slnx --verify-no-changes` clean;
`dotnet build -c Release` 0 warnings 0 errors; Debug build from scratch
in the fresh clone 0 warnings 0 errors. Analyzer suppressions added for
the smoke file carry one-line reasons in `.editorconfig` (scenario
restore in `finally`, intentional overlapping requests in the parallel
burst). The only `#pragma warning` occurrences are EF-generated
migration artifacts, excluded as generated code.

### E. Self-audit

| # | Check | Result |
|---|-------|--------|
| 1 | Clean build Debug and Release | Pass: Release `dotnet build -c Release` 0/0; Debug `dotnet build` in fresh clone 0/0; Docker `dotnet publish -c Release` produced both images with no warnings |
| 2 | All test layers green, smoke fully passed | Pass: unit 381, architecture 45, integration 373, smoke 18/18, 0 skipped (twice on main stack, once on clone) |
| 3 | Every Practice endpoint covered | Pass: all nine routes have integration tests (`PracticeApiTests`, `PracticeAccountTests`, `PracticeOrderTests`, `PracticeStopTests`) and the smoke journey |
| 4 | Journal lines match section 6 | Pass: real entries below; buy fill debits user USD, credits clearing net of fee, credits fees, moves `P.SNVDA` from clearing to user; hold and release journals verified in order tests |
| 5 | Grant once, no overdraw/oversell under load | Pass: smoke journey plus drills; twenty parallel buys and repeated bursts never overdraw; rerun across smoke runs and three drill bursts |
| 6 | Trigger/cancel race 50x, two evaluators once | Pass: `PracticeStopTests` loops the race 50 times; advisory-lock dedupe covered by `A_Failing_Order_Does_Not_Stop_Others_And_Locks_Dedupe` style tests and the two-instance drill pattern from Phase 5 reused for the evaluator lock |
| 7 | No execution on unusable price | Pass: stale, closed, halted and unavailable scenarios each refuse orders and stop triggers in integration tests; smoke proves `halted` -> `practice.market_unavailable` |
| 8 | Reset boundaries with fake clock | Pass: `PracticeResetPolicyTests` plus integration tests cover cooldown, rolling 30 day cap and immediate re-reset rejection; credits cannot be farmed because reset reissues only after limits pass |
| 9 | Practice never reaches real book | Pass: architecture boundary tests, service-level tests and database checks show Practice journals only in `Book.Practice`; `P.` prefix rejected by the Catalog importer (unit + import tests) |
| 10 | Consistency and reconciliation clean after drills | Pass: zero mismatches, zero non-zero nets, zero orphan journals after all three drills (queries in section C) |
| 11 | `practice_events` append-only | Pass: `Application_Role_Cannot_Update_Or_Delete_Practice_Events` asserts `PostgresException` with SQLState `42501` (insufficient_privilege) for both UPDATE and DELETE as the app role |
| 12 | Logs inspected | Pass: api logs show EF commands with parameters masked (`@p0='?'`); module logs carry counts and route templates only; no amounts, balances, account ids or personal data found in the Practice log lines inspected |
| 13 | Error codes and docs in sync | Pass: every new code is in `ErrorCatalogue`, the design docs and reached by tests (`market_unavailable`, `insufficient_position`, `order_invalid`, `reset_not_allowed`, `too_many_open_stops`, plus reused `insufficient_funds`, `resource.not_found`) |
| 14 | Hardening, deps, lock files | Pass: `dotnet list package --vulnerable --include-transitive` clean for every project including Practice; no new package types beyond the module standard set (EF Core Npgsql, NamingConventions, FluentValidation, EF Design); lock files unchanged and restore `--locked-mode` clean |
| 15 | Budget | Partial deviation: Practice module has 44 hand-written + 3 migration `.cs` files vs about 24 budget; new test files 11 vs about 12 (6 integration incl. shared fixture, 4 unit, 1 smoke). Same one-type-per-file structural pattern as Phases 2-5; only contract interface added is `IPracticeOrders` |
| 16 | grep audit | Pass: no `TODO`/`FIXME`, no banned clock/id APIs, no em dashes, no suppressions outside `.editorconfig`; `double` appears once in `PracticeOptions.ResetCooldownHours`, a duration setting, not a money path (money stays `decimal`) |
| 17 | Editor parity | Pass: format verify clean, zero analyzer warnings at build |
| 18 | Fresh clone README-only | Pass: cloned to `/tmp/askvest-p6-check`, followed README (`.env` from example plus generated secrets, alternate ports, `-p askvestp6` because compose project names are fixed), stack healthy, smoke 18/18 with 0 skipped, then `down -v` |
| 19 | Unverifiable items | Listed below |

### Real ledger entries (drill database, market buy fill)

```
journal_id                            book     kind             asset    amount
01a1baaf-4947-7047-a9f9-6a7f92051311  practice user_available  USD      -1004.999972000000000000
01a1baaf-4947-7047-a9f9-6a7f92051311  practice system_clearing USD        999.999972000000000000
01a1baaf-4947-7047-a9f9-6a7f92051311  practice system_fees     USD          5.000000000000000000
01a1baaf-4947-7047-a9f9-6a7f92051311  practice system_clearing P.SNVDA    -31.446540000000000000
01a1baaf-4947-7047-a9f9-6a7f92051311  practice user_available  P.SNVDA      31.446540000000000000
```

Net zero per asset per book; fee visible as its own line.

## Commits

- `2becc6b` docs(practice): design, guards and small earlier-module changes
- `cdc4106` feat(practice): schema, configuration and module wiring
- `cb20fcd` feat(practice): account, reset and positions
- `e50f347` feat(practice): market orders, order history and contract
- `1a3abea` feat(practice): simulated stop-loss and evaluator
- `5841e82` chore(practice): compose, readme and ci
- `d243a4b` test(practice): end-to-end smoke journey over real http
- this commit: docs(practice): phase 6 verification evidence

## Deviations

- File count exceeds the ~24 budget (47 total): one type per file, nine
  enums, DTOs and EF configurations, matching prior phases.
- `PracticeOptions.ResetCooldownHours` is `double` hours (not money);
  changed from `int` to allow the smoke override's sub-hour cooldown.
- Smoke override reset cooldown is 0.001 hours instead of the spec's
  suggested small value, for the same reason; documented in README.
- The stop-kill drill required re-arming the `drop` scenario after API
  restart because the fake provider scenario is in-memory; recorded in
  section C.

## Unverified

- Remote CI run not observed locally (workflow runs the same smoke
  suite that passed here).
- Production compose boot not exercised (no real provider/SMTP by
  design; module defaults are covered by tests).
- Real market hours behavior is Phase 5 research, unchanged here.

## Test counts and rerun

- Unit: `dotnet test --project tests/AskVest.UnitTests -c Release` - 381 passed
- Architecture: `dotnet test --project tests/AskVest.ArchitectureTests -c Release` - 45 passed
- Integration: `dotnet test --project tests/AskVest.IntegrationTests -c Release` - 373 passed
- Smoke: `ASKVEST_SMOKE_BASE_URL=http://localhost:5081 ASKVEST_SMOKE_MAILPIT_URL=http://localhost:18025 dotnet test --project tests/AskVest.SmokeTests -c Release` - 18 passed, 0 skipped (requires `ASKVEST_COMPLIANCE_KYC_WEBHOOK_SECRET` from `.env` for the compliance journeys)

## Stack commands

```bash
docker compose -f docker-compose.yml -f docker-compose.smoke.yml up -d --build --wait
ASKVEST_SMOKE_BASE_URL=http://localhost:5081 ASKVEST_SMOKE_MAILPIT_URL=http://localhost:18025 dotnet test --project tests/AskVest.SmokeTests -c Release
```

## For later phases

- Phase 7 consumes `PracticeOrderFilled` outbox events (payload carries
  ids only; order reads provide the rest).
- Phase 8 reads order history through the existing cursor-paginated
  reads; no new endpoints needed for basics.
- Phase 10 extends `PracticeOrderService`: market fills, stop holds and
  `TriggerStopAsync` are the seams; keep all ledger writes inside the
  shared transaction scope.
- `P.` asset codes are the practice namespace; the Catalog importer
  rejects real tokens with that prefix.
- Practice journal patterns: `user_available`/`user_held` debits and
  credits against `system_clearing`/`system_fees`, always inside
  `Book.Practice`, idempotency key persisted in the same transaction.
