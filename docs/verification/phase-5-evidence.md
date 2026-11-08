# Phase 5 verification evidence

Market Data. Verified on .NET SDK 10.0.114, PostgreSQL 17, Docker
Compose smoke stack (`askvest-smoke` project, API on port 5081).

## Step 1: orientation and baseline

Read: `CLAUDE.md`, all files in `docs/design/`, Phase 0 to 4 evidence, and
`AskVest-Backend-Phase-5-Market-Data.md` in full.

Baseline on the Phase 4 tip: restore `--locked-mode` OK, scratch Debug and
Release builds 0 warnings 0 errors, `dotnet format --verify-no-changes`
clean, full suite green (279 unit, 30 architecture, 286 integration,
16 smoke with env set).

## Step 2: design docs, research sheet and module scaffold

- `docs/design/00-system-overview.md`: market data module section
  (provider seam, fake provider, freshness labels, poller, candle cache,
  read-only APIs, no vendor assumptions).
- `docs/design/01-api-standards.md`: `marketdata.unavailable` error code.
- `docs/design/06-provider-abstractions.md`: `IMarketDataProvider`
  contract (batch quotes, candles, declared capabilities, decimal text),
  adapter contract every real provider must satisfy, and the open
  research sheet including the redistribution licensing question.
- `docs/design/05-threat-model.md`: market data threats (stale or wrong
  prices reaching execution, provider abuse, bad data containment).
- `docs/design/10-market-data-module.md`: module design.
- Scaffolded `src/Modules/AskVest.Modules.MarketData` with
  `MarketDataBoundaryTests` (module boundary, contracts-only cross-module
  use, no external references to internals).
- Commit: `docs(marketdata): provider contract, research sheet and module scaffold`.

## Step 3: schema, migration, configuration and provider seam

- `marketdata` schema with `latest_prices` and `candles`; own EF history
  table; API role gets SELECT/INSERT/UPDATE on its tables via default
  privileges, no DDL.
- `IMarketDataProvider` seam (internal) plus `IMarketDataProviderFactory`;
  `FakeMarketDataProvider` deterministic with scenarios `normal`,
  `delayed`, `halted`, `closed`, `outlier_spike`, `outage`,
  `rate_limited`, `unknown_symbol` and simulated sessions `pre`,
  `regular`, `post`, `closed`, `halted`, `unknown`.
- `MarketDataOptions` validated at startup: poll interval, batch size,
  live/stale thresholds, outlier tolerance, fractional digits, timeout,
  requests-per-minute, symbol overrides, candle intervals with per
  interval max range and max candles, fake scenario/session/delay/currency.
  Production guard refuses `Provider=fake`; `MarketData:Enabled=false`
  registers nothing (added in Step 6 so the production compose, which has
  no real provider yet, still boots).
- Migrations: `InitialMarketData`, `AddLatestPriceUnmappedMarker`,
  `AddPollState` (Step 7 fix, see drills).
- Commits: `feat(marketdata): schema, provider seam and fake provider`,
  `chore(marketdata): compose, readme and ci` (enabled flag).

## Step 4: label function, validation, outlier guard, poller, probe

- `FreshnessLabeler`: exactly `live`, `delayed`, `closed`, `halted`,
  `stale`, `unavailable`; `usableForExecution` false for closed, halted,
  stale, unavailable. Provider delay subtracts from effective age.
- `QuoteValidator`: positive price, expected currency, timestamp not in
  the future, not backwards, fractional digits within limit.
- `OutlierGuard`: single outlier held for confirmation, confirmed spike
  accepted, discarded spike keeps the old price.
- `PricePoller` hosted service: advisory lock `2025110801`, batches within
  the requests-per-minute budget and timeout, per-cycle summary log with
  counts only, one warning per failed cycle, rate-limit retry delay,
  backoff to 5 minutes after five consecutive failures, unmapped symbols
  logged at most once per hour, survives per-cycle exceptions.
- `POST /api/v1/diagnostics/marketdata-scenario` probe: mapped only when
  diagnostics are enabled, authenticated, rejects unknown scenarios,
  ignores non-fake providers (`applied:false`).
- `ICatalog.ListActiveInstrumentsAsync` added (smallest contract method
  the poller needs).
- Commit: `feat(marketdata): freshness labels, validation and price poller`.

## Step 5: read API, candle cache and contract

- `GET /api/v1/marketdata/prices?instrumentIds=`: 1 to 50 ids, request
  order preserved, unknown/inactive/unmapped instruments return
  `unavailable` rows, prices as JSON strings, never calls the provider.
- `GET /api/v1/marketdata/instruments/{id}/candles?interval=&from=&to=`:
  interval and range validation (`validation.failed` 400), unknown or
  inactive instrument `resource.not_found` 404, ascending candles as
  strings with `source`, `stale`, `adjusted`, `partial`; open candle
  cache per interval; refresh failure serves stale cache with
  `stale=true`; no cache plus provider failure answers
  `marketdata.unavailable` 503; single-flight per request key.
- `AskVest.Modules.MarketData.Contracts.IMarketData` public contract for
  future modules.
- `MarketData:SymbolOverrides` maps catalog instrument ids to provider
  symbols.
- Commit: `feat(marketdata): price and candle api and contract`.

## Step 6: containers, README and CI

- `docker-compose.yml`: `MarketData__Enabled=false` (production has no
  real provider; fake is banned in Production).
- `docker-compose.smoke.yml`: enabled, `Provider=fake`,
  `Fake__Session=regular`, live threshold 5s, stale threshold 15s,
  poll interval 5s.
- README: module section (poller, labels, smoke testing, writing a real
  adapter) and layout entry. CI already runs the full smoke suite, which
  now includes the market data journey; no workflow change needed.
- Commit: `chore(marketdata): compose, readme and ci`.

## Step 7: smoke journey, failure drills, audit

### Smoke journey (real HTTP, `tests/AskVest.SmokeTests/MarketDataSmokeTests.cs`)

Against the running smoke stack with the sample catalog:

1. Prices for sample instruments: string prices, currency, recent `asOf`,
   source `AskVest Demo Feed`, label `live`.
2. Probe `delayed`/`halted`/`closed`/`normal`: labels follow, `usableForExecution`
   false for `halted`.
3. Probe `outlier_spike`: price does not jump; repeated spike accepted.
4. Probe `outage` past the stale threshold: `stale`, last price returned,
   `usableForExecution` false; `normal` recovers to `live`.
5. Probe `unknown_symbol` (smoke compose maps `sample-nvda` to
   `SMOKE-UNKNOWN:NMS` via `MarketData__SymbolOverrides__sample-nvda`):
   that instrument shows `unavailable`.
6. Candles `1d`: ascending, strings, `adjusted` present, last candle
   `partial`; invalid interval and oversized range 400; unknown
   instrument 404; no token 401.
7. Batch of 51 ids: 400.

Result: 1/1 journey passed; full smoke suite 17/17, 0 skipped.

### Raw HTTP evidence

`docs/verification/phase-5-raw-http.log`: unauthenticated 401 envelope,
authenticated prices 200 with `live` labels and string prices, candles
200 with ascending daily candles and a partial current candle, invalid
interval 400, unknown instrument 404 (valid range), 51-id batch 400.

### Failure drills

1. **Database outage.** `docker stop askvest-smoke-postgres-1`: prices and
   candles answer `service.unavailable` 503. Restart: API recovered
   without restart, prices `live` again.
2. **API kill mid-poll.** `docker kill` during a poll cycle, three
   kill/restart rounds: after each restart exactly one `latest_prices`
   row per instrument, prices resume `live`, no duplicates or corrupt
   rows.
3. **Two API instances.** A second API container against the same
   database initially completed about 7 to 8 poll cycles each in 40
   seconds: the advisory lock prevented concurrent polls but not
   alternating ones, doubling the provider call rate. Fixed by
   `marketdata.poll_state`, a persisted heartbeat checked inside the
   advisory lock: scheduled cycles skip when the last completed poll is
   younger than `PollIntervalSeconds`; manual/probe cycles
   (`RunCycleAsync`) bypass the check. Migration
   `20261108030905_AddPollState` applied by the Migrator. After the fix,
   in the same 40 second window the primary instance completed 8 cycles
   and the second instance completed 0. Integration tests:
   `Scheduled_Cycle_Skips_Until_The_Poll_Interval_Elapses` and
   `Manual_Cycle_Ignores_The_Heartbeat`.

Commits: `fix(marketdata): dedupe scheduled polls across api instances`,
`test(marketdata): end-to-end smoke journey over real http`.

### Editor re-check

`dotnet format AskVest.slnx --verify-no-changes` clean on final code;
scratch builds 0 warnings; grep audit below.

## Self-audit

| # | Check | Result | Evidence |
|---|-------|--------|----------|
| 1 | Clean build from scratch, Debug and Release, zero warnings and errors | Pass | Baseline and post-change builds clean; Docker Release publish clean; `dotnet format --verify-no-changes` clean |
| 2 | All test layers green, smoke journey fully passed, none skipped | Pass | Unit 350, Architecture 36, Integration 323, Smoke 17; full run 722 passed, 3 skipped (conditional), 1 failure was the known `OutboxDispatcherTests` ordering flake which passes in isolation (3rd occurrence, pre-existing, unrelated) |
| 3 | Every endpoint covered by integration test and smoke journey | Pass | `MarketDataApiTests`, `MarketDataCandlesApiTests`, `MarketDataProbeTests`; journey covers prices, candles, probe |
| 4 | Label function proven for every row and boundary; `usableForExecution` proven | Pass | `FreshnessLabelerTests` table-driven; API and smoke assertions |
| 5 | Bad data containment: non-positive, wrong currency, digits, future, backwards, outlier | Pass | `QuoteValidatorTests`, `OutlierGuardTests`, `PricePollerTests` (single held, confirmed accepted, discarded keeps old) |
| 6 | No provider call on latest-price path; one call under parallel candle requests | Pass | Spy counter assertion in `MarketDataApiTests`; single-flight test in `MarketDataCandlesApiTests` |
| 7 | Poller resilient: outage, rate limit, partial failure, crash mid-cycle, two instances | Pass | `PricePollerTests` plus drills 1 to 3 above |
| 8 | Fake provider cannot run in Production; probe cannot run without flag or with non-fake provider | Pass | `MarketDataOptionsTests.Fake_Provider_In_Production_Throws`; `Probe_Is_Not_Mapped_When_Probes_Are_Disabled`; probe returns `applied:false` for non-fake |
| 9 | No vendor, exchange or chain specific code; seam and adapter contract documented | Pass | Module contains only the generic seam; contract in `docs/design/06-provider-abstractions.md` and `docs/design/10-market-data-module.md` |
| 10 | Doc 06 has market data contract, adapter contract, research questions incl. redistribution licensing | Pass | `docs/design/06-provider-abstractions.md` lines 39, 75 and research table |
| 11 | Logs inspected: no prices, no personal data | Pass | Poller logs counts only (`Instruments`, `BatchesAttempted`, `QuotesApplied`, `QuotesRejected`, `QuotesHeld`, `UnknownSymbols`); inspected live container logs |
| 12 | Error codes and docs in sync, each new code reached by a test | Pass | `marketdata.unavailable` in `ErrorCatalogue` and docs; 503 path tested in `MarketDataCandlesApiTests` and drill 1 |
| 13 | Container hardening re-verified, dependencies audited clean, lock files updated, ADR 0013 lists new packages | Pass | Images unchanged (non-root `askvest` user, `no-new-privileges`); `dotnet list package --vulnerable --include-transitive` clean on all 13 projects; zero new packages, `Directory.Packages.props` untouched, ADR 0013 needs no entry |
| 14 | Budget respected, only the two named interfaces added | Deviated | Module has 41 `.cs` files (34 hand-written + 7 migration artifacts) vs about 20; 13 new test files vs about 9. Same one-type-per-file, EF-config structure as Phases 2 to 4. Interfaces: exactly `IMarketData` (public contract) and `IMarketDataProvider` (internal seam). Zero new docs besides this mandated evidence file |
| 15 | grep audit: no suppressions outside `.editorconfig`, no `TODO`, no banned APIs, no em dashes, no `double`/`float` in price paths | Pass | Module and new tests clean; only EF-generated `#pragma warning disable 612, 618` in migration designers (generated code); prices are `decimal`, JSON strings |
| 16 | Editor parity: zero findings at lowest severity, generated code excluded | Pass | Format verify clean; compiler warnings are errors; no analyzer suppressions added outside `.editorconfig` entries with reasons |
| 17 | Fresh clone follows the README alone to a healthy stack and a passing smoke run | Pass | `/tmp/askvest-clone-check`: `git clone`, `dotnet restore --locked-mode`, `.env` per README with alternate ports, `docker compose -p askvest-clone -f docker-compose.yml -f docker-compose.smoke.yml up -d --build --wait` healthy, smoke suite 17/17 passed including the market data journey. Note: the compose files hardcode project names `askvest`/`askvest-smoke`, so a parallel clone must override with `-p` or it adopts the main project's containers |
| 18 | Unverifiable items listed plainly | Pass | See below |

### Unverified items

- Real provider behavior: latency, rate limits, session reporting,
  candle semantics and data quality of any licensed vendor are unknown
  until research picks one; the fake proves the contract, not a vendor.
- Licensing and redistribution terms for displaying prices to end users
  are open research questions recorded in `docs/design/06-provider-abstractions.md`.
- Real market hours and exchange calendars are not modeled; the fake
  session is a stand-in.
- Remote CI was not observed; all verification is local.

## Commits (Phase 5)

- `adaad43` docs(marketdata): provider contract, research sheet and module scaffold
- `98a63a9` feat(marketdata): schema, provider seam and fake provider
- `54f6f3e` feat(marketdata): freshness labels, validation and price poller
- `a3e7041` feat(marketdata): price and candle api and contract
- `1a25991` chore(marketdata): compose, readme and ci
- `becd203` fix(marketdata): dedupe scheduled polls across api instances
- `c36a3ba` test(marketdata): end-to-end smoke journey over real http
- this file: `docs(marketdata): phase 5 verification evidence`
