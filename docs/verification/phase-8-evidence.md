# Phase 8 verification evidence

Portfolio and Watchlist. Verified on .NET SDK 10.0.114, PostgreSQL 17,
Docker Compose smoke stack (`askvest-smoke` project, API on port 5081).

## Step 1: orientation and baseline

Read: `CLAUDE.md`, all files in `docs/design/`, Phase 0 to 7 evidence,
and `AskVest-Backend-Phase-8-Portfolio-and-Watchlist.md` in full.

Baseline on the Phase 7 tip: full suite green (428 unit, 52
architecture, 398 integration, 19 smoke with env set), audit clean.
Contract names recorded: `IPortfolio`, `PortfolioResponse`,
`PortfolioHolding`, `PortfolioCashLine`, `PortfolioTokenBreakdown`,
`PortfolioTotals`, `WatchlistEntry`.

## Step 2: design docs, guards, small earlier-module changes, scaffold

- Portfolio added to system overview, API standards, threat model and
  roadmap; `docs/design/03-money-and-ledger-principles.md` records the
  read-only replay convention.
- `CLAUDE.md` gained rule 25: Portfolio is read-only, stores no money
  facts, replays ledger movements on demand, holdings must equal ledger
  balances, watchlist is the only table it owns.
- Ledger contract gained `GetMovementsAsync` (net per-journal,
  per-asset movement rows, cursor paginated) and `GetAssetKindAsync`.
- Practice contract gained `GetInstrumentIdsForAssetCodesAsync`.
- Module scaffold `src/Modules/AskVest.Modules.Portfolio` with
  `PortfolioBoundaryTests` proving Portfolio references only SharedKernel,
  Platform and the Ledger, Catalog, MarketData and Practice contracts.
- Commit: `feat(portfolio): design updates, ledger movement reads and
  module scaffold`.

## Step 3: watchlist schema, options, replay engine

- `portfolio` schema with `watchlist_items` (user id, instrument id,
  added at; composite primary key); own EF history table; forward-only
  migration. The application role gets CRUD on `watchlist_items` and
  SELECT on the history table only.
- `PortfolioOptions` validated at startup: `CashEquivalentAssets`
  (USD, USDC), `WatchlistMaximum` (50), `MaxJournalsPerReplay` (20000),
  `DisplayDecimals` (2).
- `PortfolioReplay` is pure and deterministic: movements grouped by
  journal ordered by posted time then journal id; reversal journals
  classified by the inverse of the reason they reverse (`trade_buy`
  becomes `trade_sell` and vice versa); buys add quantity and cost,
  sells remove weighted-average cost and realize proceeds less removed
  cost, splits change quantity without cost, dividends become income
  attributed by instrument reference or unattributed, every other
  reason updates quantity and sets `has_unclassified_movements`; journal
  count above the cap returns `service.unavailable`; oversells apply
  only held quantity and flag `has_inconsistency`; malformed trade
  journals degrade to unclassified; exact `decimal` arithmetic with
  display rounding `MidpointRounding.AwayFromZero`.
- Commit: `feat(portfolio): watchlist schema, options and replay engine`.

## Step 4: portfolio endpoints and contract

- `GET /api/v1/practice/portfolio` behind `PracticeEligible`,
  `GET /api/v1/real/portfolio` behind `RealEligible`.
- Response: book, as-of, valuation currency, `partial` flag, cash lines
  (asset, kind, available, held, included-in-total), holdings (quantity,
  cost basis, average cost, latest price with label/session/usability,
  market value, unrealized profit and percent, realized profit, income,
  flags, per-token breakdowns) and totals.
- Practice assets resolve through `IPractice`; Real token assets resolve
  through `ICatalog.FindTokenByAssetCodeAsync`, so one instrument with
  tokens on two chains aggregates into one holding with two breakdown
  lines and no chain-specific code in Portfolio.
- Commit: `feat(portfolio): portfolio endpoints and contract`.

## Step 5: watchlist endpoints

- `GET /api/v1/watchlist`, `PUT /api/v1/watchlist/{instrumentId}`,
  `DELETE /api/v1/watchlist/{instrumentId}`; entries carry instrument
  symbol, name, catalog status and the latest labelled price.
- Add and remove are idempotent set operations and are exempt from
  idempotency storage with a written reason: re-adding is a harmless
  success, removing a missing item is a harmless success.
- Adds take a per-user advisory lock, enforce the configured cap with
  `portfolio.watchlist_full` (422), and answer `resource.not_found` for
  unknown or non-active instruments. Ownership is filtered by user id.
- Commit: `feat(portfolio): watchlist`.

## Step 6: compose, README, CI

- Compose carries `Portfolio__CashEquivalentAssets__0/1`,
  `Portfolio__WatchlistMaximum`, `Portfolio__MaxJournalsPerReplay`,
  `Portfolio__DisplayDecimals`.
- README documents the read-only replay model, watchlist semantics and
  the smoke command; CI already runs the whole suite.
- OpenAPI shows all five routes; mutating watchlist routes document the
  idempotency exemption.
- Commit: `chore(portfolio): compose, readme and ci`.

## Step 7: smoke journey, drills, audit

### A. Smoke journey (`tests/AskVest.SmokeTests/PortfolioSmokeTests.cs`)

One journey over real HTTP covering all five spec steps: a practice
user starts the account, buys at normal price, switches the fake feed
to `drop`, buys cheaper, sells part, and the portfolio reports weighted
average cost, quantity, realized and unrealized profit, price label and
totals with money serialized as strings; a stop order holds shares while
the holding still reports total quantity, and reset empties the
portfolio and restores starting credits; three watchlist instruments are
added with labelled prices, one is re-added (harmless), one removed, the
feed switches to `halted` and labels change; a real-eligible user
credits the two sample AAPL token assets on `eip155:11155111` and
`eip155:17000` and sees one aggregated holding with two chain breakdown
lines, `hasUnclassifiedMovements`, cash, and an untouched practice book;
a real-ineligible user gets 403 on the Real portfolio, anonymous gets
401, and a second user cannot see the first user's data. Result: 20/20
smoke tests pass, 0 skipped, on the main stack twice and once on a fresh
clone.

### B. Raw HTTP evidence

`docs/verification/phase-8-raw-http.log` records catalog reads,
watchlist add/list/remove with labelled prices, eligibility gating, real
deposits of both token assets, the aggregated two-chain portfolio,
practice/real isolation and the 401/403 gates. Secrets redacted.

### C. Failure drills

1. Database stopped: `GET /api/v1/practice/portfolio` and
   `GET /api/v1/watchlist` answered HTTP 503 `service.unavailable` in
   the standard envelope; after `docker start` on postgres both
   answered 200 again.
2. Heavy mixed practice activity (parallel buys and sells across
   instruments, stop orders armed, feed dropped): the database now holds
   328 practice orders (310 filled, 18 cancelled), 1548 journals and
   4056 journal entries for the smoke database. The startup
   reconciliation cycle on the fixed build logged
   `Ledger reconciliation completed with zero mismatches.` and the API
   log contains zero `Portfolio replay mismatch` entries, proving
   holdings reconcile with ledger balances after the activity.

### D. Bug found and fixed by the audit

`CheckConsistencyAsync` treated a replay-absent asset as a mismatch even
when its ledger balance was zero. Zero-balance rows persist after sells
and resets, so healthy portfolios logged spurious mismatch errors. The
check now reads an absent replay quantity as zero and only flags
nonzero disagreements; regression test
`Practice_Portfolio_Ignores_Zeroed_Assets_From_Before_Reset` covers it.
Commit: `fix(portfolio): treat absent replay assets as zero in
consistency check`.

### E. Editor re-check

`dotnet format --verify-no-changes` clean; `dotnet build -c Debug` and
`dotnet build -c Release` both 0 warnings 0 errors. One new `.editorconfig`
entry disables CA1031 for `PortfolioSmokeTests.cs` with a one-line
reason (scenario restore in `finally` must not mask the journey's own
failure), matching the existing Practice and Learning precedents. The
only `#pragma warning` occurrences are EF-generated migration artifacts,
excluded as generated code. One transient analyzer crash
(`AD0001 ... DoNotPassDisposablesIntoUnawaitedTasksAnalyzer`
NullReferenceException) appeared twice during the phase; it is an
intermittent tooling fault already seen in earlier phases, cleared on
retry, and no suppression was added.

### F. Self-audit

| # | Check | Result |
|---|-------|--------|
| 1 | Clean build Debug and Release | Pass: `dotnet build -c Debug` and `-c Release` 0 warnings 0 errors; Docker image build produced no warnings |
| 2 | All test layers green, smoke fully passed | Pass: unit 453, architecture 63, integration 411, smoke 20/20, 0 skipped on main stack and on fresh clone |
| 3 | Every endpoint covered | Pass: all five routes have integration tests (`PortfolioApiTests`, `PortfolioWatchlistTests`, `PortfolioSchemaTests`) and the smoke journey |
| 4 | Replay engine proven rule by rule, paging invariance | Pass: 19 replay unit tests cover buys, sells, oversell, splits, dividends (attributed and unattributed), unclassified movements, malformed journals, reversals, input-order invariance, the journal cap, exact decimals; `Practice_Portfolio_Replay_Spans_Movement_Pages` posts 501 probe journals so the replay crosses the 500-row movement page boundary with identical results |
| 5 | Portfolio figures reconcile with Phase 6 orders and ledger balances | Pass: `CheckConsistencyAsync` compares replayed quantity to `available + held` per position asset on every portfolio read; drill 2 ran heavy mixed activity and logged zero mismatches; integration tests assert buy/sell quantities and cost against ledger postings |
| 6 | Flags and labels propagate | Pass: `has_unclassified_movements`, `has_inconsistency`, `valuation_unsupported_currency`, `price_unavailable` and `partial` are asserted in unit, integration and smoke tests; price label/session/usability come straight from `IMarketData` |
| 7 | Multi-token cross-chain aggregation, no chain-specific code | Pass: `Real_Portfolio_Gate_And_Two_Chain_Aggregation` and the smoke journey prove one holding with two token breakdowns for `SMPSAPL2`/`SMPSAPL3` on two chains; Portfolio resolves tokens only through `ICatalog` metadata |
| 8 | Portfolio never writes ledger; app role scoped | Pass: PortfolioDbContext touches only `portfolio.watchlist_items`; `information_schema.role_table_grants` shows `askvest_app` holds DELETE,INSERT,SELECT,UPDATE on `watchlist_items` and SELECT on `__ef_migrations_history`, nothing else in the schema; architecture tests forbid ledger references |
| 9 | Practice/Real isolation and gating | Pass: route groups carry `PracticeEligible`/`RealEligible`; smoke proves 403 for real-ineligible, 401 anonymous, untouched practice book after real deposits, and ownership isolation between users |
| 10 | Watchlist cap under 60 parallel adds, rerun three times | Pass: `Watchlist_Cap_Holds_Under_Sixty_Parallel_Adds` fires 60 concurrent adds against the 50 cap; ran four consecutive times green (three during the audit plus a final confirmation) |
| 11 | Logs inspected | Pass: Portfolio service logs only mismatch asset codes on inconsistency; API request logs carry route templates; no amounts, balances, account ids, bodies or query strings in Portfolio log lines |
| 12 | Error codes and docs in sync | Pass: `portfolio.watchlist_full` is in `ErrorCodes.cs` and `docs/design/01-api-standards.md` (422) and is reached by the cap test and integration suite |
| 13 | Hardening, deps, lock files | Pass: `dotnet list package --vulnerable --include-transitive` clean for all projects; no new packages; 16 `packages.lock.json` files committed; containers run non-root `askvest` with `no-new-privileges` and all capabilities dropped |
| 14 | Budget | Partial deviation: Portfolio module has 26 `.cs` files (23 hand-written excluding migrations) vs about 14, same one-type-per-file structural pattern as Phases 2-7; new test files 7 vs about 8 (2 unit, 1 architecture, 3 integration, 1 smoke); one new public contract `IPortfolio`; zero new docs beyond the mandated evidence files |
| 15 | Grep audit | Pass: no `TODO`/`FIXME`, no banned clock/id APIs, no suppressions outside `.editorconfig`, no em dashes in new files (pre-existing em dashes only inside phase-0/1 evidence docs quoting the audit), no `double`/`float` in money paths (the only `double` in src is `PracticeOptions.ResetCooldownHours`, a duration config documented in Phase 6) |
| 16 | Editor parity | Pass: format verify clean, zero analyzer warnings at build in both configurations |
| 17 | Fresh clone README-only | Pass: cloned to `/tmp/askvest-p8-check`, followed README (`.env` from example plus generated secrets on alternate ports `ASKVEST_POSTGRES_PORT=56433`, `ASKVEST_MAILPIT_UI_PORT=19025`, `ASKVEST_SMOKE_API_PORT=5181`), stack healthy, smoke 20/20 with 0 skipped |
| 18 | Unverifiable items | Listed below |

## Unverifiable items

- Real issuer corporate actions: splits and dividends are proven against
  synthetic ledger journals; no real issuer action feed exists yet.
- Real token deposits and withdrawals: the Real book was exercised only
  through catalog sample tokens and diagnostic probe postings.
- FX effects: instruments quoted in non-USD currencies are flagged
  `valuation_unsupported_currency`; no real FX rates exist.
- Remote CI not observed.
- Production compose boot (no real provider credentials by design).

## Commits

- `40c55e8` feat(portfolio): design updates, ledger movement reads and module scaffold
- `84bcb91` feat(portfolio): watchlist schema, options and replay engine
- `3cde4fb` feat(portfolio): portfolio endpoints and contract
- `a838845` feat(portfolio): watchlist
- `8e3dd76` chore(portfolio): compose, readme and ci
- `fef4970` fix(portfolio): treat absent replay assets as zero in consistency check
- `323a8fc` test(portfolio): end-to-end smoke journey over real http
- this commit: docs(portfolio): phase 8 verification evidence

## Deviations

- File budget exceeded for the same structural reasons as Phases 2-7
  (one type per file, EF configurations, contract records).
- Watchlist mutations are exempt from idempotency storage by design
  (idempotent set operations), documented on the endpoints and in the
  API standards.
- `ILedger` gained two read methods (`GetMovementsAsync`,
  `GetAssetKindAsync`) and `IPractice` gained
  `GetInstrumentIdsForAssetCodesAsync`; these are additions to existing
  contracts, not new interfaces.

## Stack

`docker compose -f docker-compose.yml -f docker-compose.smoke.yml up
-d --build --wait` then
`ASKVEST_SMOKE_BASE_URL=http://localhost:5081
ASKVEST_SMOKE_MAILPIT_URL=http://localhost:18025
ASKVEST_COMPLIANCE_KYC_WEBHOOK_SECRET=<from .env>
dotnet test --project tests/AskVest.SmokeTests -c Release`.
Stack left up and healthy.

## For later phases

- `IPortfolio` (in `AskVest.Modules.Portfolio.Contracts`) exposes
  `GetPortfolioAsync(userId, book)`, `GetWatchlistAsync`,
  `AddWatchlistItemAsync` and `RemoveWatchlistItemAsync`; consume it
  rather than recomputing positions.
- Cost basis convention: weighted average cost per asset. Buys add
  `quantity` and `-cashAmount` to cost; sells remove
  `TotalCost * applied / quantityBefore` rounded to six decimals
  (`PortfolioRounding.RoundCost`); residual dust on a closed position
  moves to realized profit. Practice order fees post to the fees
  account and never enter cost basis.
- Journal classification: `trade_buy`/`trade_sell` are well-formed only
  with exactly one position row and one cash row (fiat or stablecoin);
  anything else degrades to unclassified quantity with
  `has_unclassified_movements`. `corporate_action_split` adjusts
  quantity per position row. `dividend` sums cash rows into income,
  attributed when a row carries `reference_type = 'instrument'` and a
  reference id, otherwise unattributed. `reversal` journals classify by
  the inverse of the referenced journal's reason. All other reasons
  (fee, hold, release, adjustment, deposit, withdrawal, practice credit
  grant, practice reset handled earlier by epoch truncation) update
  position quantity as unclassified.
- Portfolio reads truncate movements at the latest `practice_reset`
  journal per book before replaying.
- Replay cap: `Portfolio:MaxJournalsPerReplay` (20000) returns
  `service.unavailable` beyond it; if real usage approaches the cap a
  later phase should add a materialized snapshot signal rather than
  raising the cap silently.
- FX gap: totals assume USD valuation; non-USD quote currencies and
  non-cash-equivalent assets are flagged, not converted. Any future FX
  work needs rates before `partial` can clear.
- Holdings quantity equals `available + held` ledger balances;
  `CheckConsistencyAsync` flags `has_inconsistency` on any disagreement.
