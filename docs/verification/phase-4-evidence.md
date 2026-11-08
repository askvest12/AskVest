# Phase 4 verification evidence

Instrument Catalog. Verified on .NET SDK 10.0.114, PostgreSQL 17, Docker
Compose smoke stack.

## Step 1: orientation and baseline

Read: `CLAUDE.md`, all files in `docs/design/`, Phase 0 to 3 phase specs and
evidence, and `AskVest-Backend-Phase-4-Instrument-Catalog.md` in full.
Contract names confirmed in code: `ITransactionScope` (with `Transaction`
exposed for EF enlistment), `ILedger.RegisterAssetAsync`,
`IComplianceEligibility`, `PageResult<T>`/cursor primitives, `ErrorCodes`,
module blueprint rules.

Baseline on the Phase 3 tip: `dotnet restore --locked-mode` OK,
`dotnet build -c Release` 0 warnings 0 errors, full test suite green
(225 unit, 24 architecture, 236 integration, 15 smoke with env set).

## Step 2: design docs, research sheet and module scaffold

- `docs/design/00-system-overview.md`: catalog chain-agnostic rules section
  (CAIP-2 chains, CAIP-19 computed asset ids, chain family handler seam,
  listing guard, exit principle, data-file import, no real data in repo,
  gateway key convention, second-family recipe added in Step 7).
- `docs/design/01-api-standards.md`: `catalog.address_invalid` error code.
- `docs/design/06-provider-abstractions.md`: chain-aware provider contract,
  provider fact sheet template and open research questions for the
  developer's provider/chain/issuer research.
- `docs/design/05-threat-model.md`: catalog-specific threats.
- Scaffolded `src/Modules/AskVest.Modules.Catalog` with architecture guards
  (`CatalogBoundaryTests`: module boundary, contracts-only cross-module use,
  no external references to internals, chain-specific library confinement
  with synthetic violation proven).
- Commit: `docs(catalog): chain-agnostic design, research sheet and module scaffold`.

## Step 3: schema, migration and chain family seam

- `catalog` schema with `chains`, `issuers`, `instruments`, `tokens`,
  `token_countries`, `catalog_events`; own EF history table; API role gets
  SELECT only (write attempts proven denied, see audit row 6).
- `IChainFamilyHandler` seam plus `ChainFamilyRegistry`; `EvmChainFamilyHandler`
  validates EIP-155 chain ids, canonicalizes and EIP-55-checksums addresses,
  computes CAIP-19 asset ids and explorer links. BouncyCastle.Cryptography
  2.7.0 added for Keccak-256, listed in ADR 0013.
- Commit: `feat(catalog): schema, chain family seam and evm handler`.

## Step 4: importer, listing guard, ledger registration and data files

- `CatalogDataReader` + `CatalogImportValidator` (validate-only mode) +
  `CatalogImportService`: all-or-nothing import inside `ITransactionScope`,
  Catalog EF context bound to the shared connection/transaction, Ledger
  asset registration per token, catalog events, outbox messages for status
  changes, delisting (never deleting) of removed entries, sample-data refusal
  in Production.
- `ListingGuard` table: approved issuer, enabled supported chain, complete
  disclosures, at least one allowed country, gateway key, decimals at most
  18, valid review dates, active instrument. Ever-listed tokens keep their
  identity fields immutable; `everListed` file flag permits re-import of
  previously listed tokens in non-listed states.
- `data/catalog/` ships structurally valid but empty (production research
  still open); `data/catalog-sample/` holds fictional data covering every
  status and blocker.
- Commit: `feat(catalog): importer, listing guard and ledger asset registration`.

## Step 5: availability, contract and read API

- `AvailabilityEvaluator`: pure function producing buy/sell blockers
  (`token_not_listed`, `token_suspended`, `issuer_not_approved`,
  `chain_disabled`, `disclosure_review_expired`, `country_not_allowed`,
  `country_unknown`, `no_execution_route`). Exit principle: expired review,
  country changes and delisting never block selling while chain and gateway
  work.
- `IComplianceEligibility.GetEffectiveCountryAsync` added (verified country
  first, declared second).
- `ICatalog` contract plus `CatalogChain`/`CatalogInstrument`/`CatalogToken`/
  `TokenAvailability` records for other modules.
- Read endpoints: `GET /api/v1/catalog/instruments` (cursor pagination,
  kind and search filters), `GET /api/v1/catalog/instruments/{id}`,
  `GET /api/v1/catalog/tokens/{id}`, `GET /api/v1/catalog/chains`
  (enabled only). Gateway key and reviewer label are never serialized.
- Commit: `feat(catalog): availability rules, icatalog contract and read api`.

## Step 6: packaging, compose, README and CI

- `docker/migrator/Dockerfile` copies `data/` into the image; build arg
  `INCLUDE_SAMPLE_DATA` (default false) removes `catalog-sample` from
  production images. Verified: production image contains only `data/catalog`;
  smoke image contains both.
- `docker-compose.yml`: migrator gets `Compliance__RealMoneyEnabled=false`
  and `Compliance__Kyc__WebhookSecret` (the importer resolves
  `IComplianceEligibility.IsRealMoneyCountry`, whose options validation
  requires the secret).
- `docker-compose.smoke.yml`: migrator builds with `INCLUDE_SAMPLE_DATA=true`,
  `DOTNET_ENVIRONMENT=Test`, `Catalog__LoadSampleData=true` and the smoke
  real-money country list. `Test` is used instead of `Development` because
  Development turns on build-time service validation, which fails on the
  web-only `AddAuthorization` registrations the Migrator never uses.
- README: catalog data location, add/change token steps, local validation,
  smoke test command, and the explicit warning that production catalog data
  stays empty until issuer/provider/chain research completes.
- CI: dedicated "Catalog data validation" step running the catalog unit
  tests; smoke job already runs the smoke suite.
- Stack verified: migrator logged `Catalog import committed: Chains=2,
  Issuers=2, Instruments=3, Tokens=8, Events=15, Messages=5, Warnings=0`;
  OpenAPI lists all four catalog paths; unauthenticated call returns 401.
- Commit: `chore(catalog): package data files, compose config, readme and ci`.

## Step 7: smoke journey, failure drills, editor re-check, audit

### A. Smoke journey over real HTTP

`tests/AskVest.SmokeTests/CatalogSmokeTests.cs` (commit `test(catalog):
end-to-end smoke journey over real http`) runs against the smoke stack with
sample data and passed 1/1; the full smoke suite passes 16/16 with zero
skips. The journey covers: unauthenticated 401 `auth.unauthenticated`;
sign-up, verify, login and declared country `NG`; instrument listing (3
sample instruments), `kind=stock` filter, name search, cursor pagination
across pages; per-token availability for every sample blocker; full token
disclosure fields including explorer link; sensitive-field absence
(`gatewayKey`, `reviewerLabel`, `sample-gateway`, `sample-reviewer` absent
from the raw response); enabled-chains-only listing; `resource.not_found`
for unknown instrument and token ids; and a second user with no country
getting `country_unknown`.

Observed availability over real HTTP (NG user): SNVDA listed buyable+sellable;
SNVDA-S suspended `token_not_listed`/`token_suspended`; SNVDA-D delisted
`token_not_listed` buy but sellable; SAPL draft `token_not_listed`;
SAPL-E `disclosure_review_expired` buy, sellable; SAPL-H `chain_disabled`
both ways; SGETF `country_not_allowed` (US-only token, NG user); SGETF-X
`issuer_not_approved` (suspended issuer).

Idempotent re-import: `docker compose run --rm migrator` against unchanged
data logged `Events=0, Outbox=0` with all 8 tokens unchanged.

### B. Raw evidence

Key calls by hand with curl against `http://localhost:5081` (NG user token,
correlation ids elided):

```
GET /api/v1/catalog/instruments            (no auth)
-> {"success":false,"error":{"code":"auth.unauthenticated"}} HTTP:401

GET /api/v1/catalog/instruments?limit=2    (NG user)
-> items: sample-aapl (SAPL, stock), sample-global-etf (SGETF, etf),
   nextCursor present, hasMore true

GET /api/v1/catalog/chains
-> only eip155:11155111 (Ethereum Sepolia sample); Holesky disabled absent

GET /api/v1/catalog/instruments/sample-nvda -> tokens[].availability
  SNVDA    listed    {"buyable":true,"sellable":true,"buyBlockers":[],"sellBlockers":[]}
  SNVDA-D  delisted  {"buyable":false,"sellable":true,"buyBlockers":["token_not_listed"],"sellBlockers":[]}
  SNVDA-S  suspended {"buyable":false,"sellable":false,"buyBlockers":["token_not_listed"],"sellBlockers":["token_suspended"]}

GET /api/v1/catalog/instruments/sample-aapl -> tokens[].availability
  SAPL    draft  {"buyable":false,"sellable":false,"buyBlockers":["token_not_listed"],"sellBlockers":["token_not_listed"]}
  SAPL-E  listed {"buyable":false,"sellable":true,"buyBlockers":["disclosure_review_expired"],"sellBlockers":[]}
  SAPL-H  listed {"buyable":false,"sellable":false,"buyBlockers":["chain_disabled"],"sellBlockers":["chain_disabled"]}

GET /api/v1/catalog/instruments/sample-global-etf -> tokens[].availability
  SGETF    listed {"buyable":false,"sellable":true,"buyBlockers":["country_not_allowed"],"sellBlockers":[]}
  SGETF-X  listed {"buyable":false,"sellable":true,"buyBlockers":["issuer_not_approved"],"sellBlockers":[]}

GET /api/v1/catalog/tokens/{SNVDA id}
-> 28 disclosure keys present (claim, backing, custodian, redemptionAndExit,
   dividendPolicy, votingRights, transferRestrictions, issuerFees,
   noIssuerFees, riskStatements x3, disclosureDocumentLink, tradingSchedule,
   reviewedAt, reviewExpiresAt, allowedCountries, explorerLink, ...);
   explorerLink = https://sepolia.etherscan.io/address/0xaaaAAAAAAaAaaaaAAAaaAAAAaAaaaaAaaAaaAA01
   (EIP-55 checksum form); grep for gatewayKey/reviewerLabel/sample-gateway/
   sample-reviewer over the raw body: no matches.

GET /api/v1/catalog/instruments/nope -> 404 resource.not_found
GET /api/v1/catalog/tokens/{random guid} -> 404 resource.not_found
```

### C. Failure drills

1. Broken data: a copy of `data/catalog-sample` with a malformed token
   address and an unknown chain id was bind-mounted over `/app/data` and the
   Migrator run again. It exited 1 and reported all five problems at once
   (invalid address, address immutability, unknown chain, chain immutability,
   address immutability). Database counts before and after identical
   (3 instruments, 8 tokens, 15 events) and the running API kept serving the
   previous catalog (3 instruments via authenticated GET).
2. Database stopped: `docker compose stop postgres`; both
   `GET /api/v1/catalog/instruments` and `GET /api/v1/catalog/chains`
   returned HTTP 503 `service.unavailable`. After `start postgres` the
   instruments endpoint served normally again.

### D. Editor re-check

`dotnet format AskVest.slnx --verify-no-changes` clean; no new analyzer or
style findings introduced by Phase 4 files.

### Test-isolation fix found during verification

`LedgerSchemaTests` raw-SQL guard tests left persisted journals whose entries
bypassed balance maintenance, so `Reconciliation_Reports_Zero_Mismatches`
failed with `mismatch ... delta -20` whenever the Ledger namespace ran
together (test ordering shifted when Phase 4 tests joined the assembly).
Commit `test(ledger): remove persisted guard journals that broke
reconciliation` deletes the fixture journals after each guard test; the
Ledger namespace now passes 45/45 and the full integration suite 286/286.

### E. Self-audit

| # | Check | Result |
|---|---|---|
| 1 | Clean build from scratch, Debug and Release | `find . -name bin/obj -delete` then `dotnet build AskVest.slnx -c Release` and `dotnet build AskVest.slnx`: 0 warnings, 0 errors each (one transient CLR crash retried, seen before in this repo) |
| 2 | All test layers green, smoke fully passed, none skipped | Unit 279, Architecture 30, Integration 286, Smoke 16; smoke run with all env vars: 0 skipped |
| 3 | Every endpoint covered by integration test and smoke | `CatalogApiTests` + `CatalogContractTests` (15 tests) and `CatalogSmokeTests` cover all four endpoints including 401, 404, filters, pagination, availability states |
| 4 | Importer all-or-nothing and idempotent | `CatalogImportTests` (26 tests: idempotent reimport, rollback on validation failure, rollback on ledger registration failure) plus broken-data drill above and `Events=0` reimport log |
| 5 | Listing guard field by field; immutability after listing | `ListingGuardTests` table-driven cases plus importer tests for every guard field and for chain/address/decimals/issuer/instrument/asset-code immutability once ever-listed |
| 6 | Application role cannot write catalog tables | `CatalogSchemaTests` runs INSERT/UPDATE/DELETE as `askvest_app` on all six catalog tables, each denied with the actual Postgres permission error |
| 7 | No chain-specific code outside the EVM handler | `CatalogBoundaryTests.Chain_Specific_Library_Types_Stay_Inside_The_Evm_Handler` scans sources; `Guard_Catches_Synthetic_Violations` proves a BouncyCastle reference outside `Chains/Evm/` is flagged |
| 8 | Second chain family needs handler + data only | `docs/design/00-system-overview.md` "Catalog chain-agnostic rules" now states the exact recipe; schema stores addresses as text, decimals capped at 18, ids as CAIP strings, so nothing assumes EVM |
| 9 | No real catalog data; sample refused in Production | `data/catalog/*.json` contain empty arrays (verified by `CatalogDataReaderTests` over the real folder); `CatalogImportService` refuses `LoadSampleData` when `IsProduction()` (unit + integration coverage); production image physically lacks `catalog-sample` |
| 10 | Gateway key and reviewer label never in API responses | token detail DTO omits both fields; smoke test asserts their absence in the raw JSON; endpoint code reviewed |
| 11 | Error codes and docs in sync | `catalog.address_invalid` in `ErrorCodes`, `ErrorCatalogue` (422) and `01-api-standards.md`; reached by `EvmChainFamilyHandlerTests`, `CatalogContractTests`; `validation.failed` for a bad kind filter asserted in `CatalogApiTests` |
| 12 | Doc 06 provider contract, fact sheet, research questions | `docs/design/06-provider-abstractions.md` sections "Chain-aware provider contract (Phase 4)", "Provider fact sheet" and "Open research questions" present |
| 13 | Hardening, dependencies, lock files, ADR 0013 | compose hardening keys untouched (`read_only`, `cap_drop`, `no-new-privileges`, non-root); `dotnet list package --vulnerable --include-transitive` clean for all 12 projects; `packages.lock.json` updated for BouncyCastle; ADR 0013 lists it |
| 14 | Budget and interfaces | deviation below; exactly two new interfaces: `ICatalog` and `IChainFamilyHandler` |
| 15 | grep audit | no `#pragma`/`SuppressMessage` outside EF-generated migration files, no `TODO`/`FIXME`, no banned APIs (`DateTime.Now`, `DateTime.UtcNow`, `Guid.NewGuid`, `System.Random`, `float`/`double`), no em dashes in Phase 4 sources, docs, data or commits |
| 16 | Editor parity | `dotnet format --verify-no-changes` clean (whitespace, style, analyzers) |
| 17 | Fresh clone | `git clone` to /tmp, `dotnet restore --locked-mode` OK, `.env` generated per README, `docker compose -f docker-compose.yml -f docker-compose.smoke.yml up -d --build --wait` healthy (migrator imported 2/2/3/8 sample rows), smoke suite 15/15 passed on the clone, stack torn down with `down -v` |
| 18 | Unverifiable items | listed below |

### Deviations from the phase document

- Catalog module ships 61 `.cs` files (58 hand-written plus 3 EF migration
  artifacts) versus the ~22 budget: one-type-per-file, nine small enums, eight
  data-file DTOs, EF configurations and endpoint/result plumbing, the same
  structural pattern as Phases 2 and 3.
- New test files: 12 (5 unit, 1 architecture, 5 integration incl. shared
  helper, 1 smoke) versus ~10.
- Smoke migrator runs `DOTNET_ENVIRONMENT=Test` instead of `Development`:
  Development enables build-time DI validation which fails on web-only
  `AddAuthorization` services registered by module extensions the Migrator
  hosts but never serves; `Test` is already an accepted non-production
  environment in the module guards.
- The Migrator needed `Compliance__Kyc__WebhookSecret` and real-money country
  configuration because the importer calls `IComplianceEligibility.
  IsRealMoneyCountry` for allowed-country warnings; documented in compose.
- `ITransactionScope` gained a `Transaction` property (Phase 3 interface
  extended, not a new interface) so the Catalog context can bind to the
  ambient transaction during `SaveChangesAsync`.
- Cursor pagination filters and orders in memory after DB-side filters;
  the active instrument set is small and bounded, and EF cannot translate
  the ordinal `string.Compare` keyset predicate. Comment in
  `CatalogReadService` records why.
- Sample token `SAPL-E` ships `everListed: true` so the importer accepts an
  already-listed token whose review has expired; availability still blocks
  buying with `disclosure_review_expired`, which is the intended drill case.

### Unverified items

- Remote CI (`workflow_dispatch`) has not been observed running Phase 4.
- The Production-environment sample refusal is proven by tests and by the
  production image lacking the sample folder; a full production compose boot
  was not run (it needs real SMTP and production secrets by design).
- The `Test`-environment Migrator path is exercised only by the smoke stack;
  no other environment name was tried.
