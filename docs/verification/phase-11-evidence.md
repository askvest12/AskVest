# Phase 11 verification evidence

Wallets and Custody. Verified on .NET SDK 10.0.114, PostgreSQL 17,
Docker Compose smoke stack (`askvest-smoke` project, API on port 5081,
Mailpit on 18025). Spec: `AskVest-Backend-Phase-11-Wallets-and-Custody.md`.

## Commits

- `4b33aa1` docs(wallets): custody plan, guards and small earlier-module changes
- `0e5619c` feat(wallets): phase 11 schema, custody and screening seams
- `808a9c0` feat(wallets): phase 11 endpoints, services and background jobs
- `0109e1b` fix(wallets): correct settle journal sign and add API integration tests
- `43f2c11` fix(catalog): backfill empty external_transfers and add wallets smoke coverage
- `200f26d` docs(wallets): phase 11 verification evidence
- `d28340a` fix(wallets,catalog): idempotency exemption on address delete and external_transfers default

## What was built

- `AskVest.Modules.Wallets` module: own `wallets` schema, forward-only
  `InitialWallets` migration, own history table, module blueprint
  registration in API, migrator and test fixture.
- Entities: wallet addresses (deposit instructions), deposits,
  withdrawal addresses, withdrawals, wallet events, custody pauses and
  reserves state. Unique constraints on provider deposit identity and on
  user plus idempotency key for withdrawals.
- `ICustodyProvider` and `IAddressScreeningProvider` seams with a
  deterministic in-memory fake each; fakes refuse Production at startup
  and expose scenario probes only when diagnostics are enabled.
- Deposit pipeline: instruction issuance per user and chain, background
  polling job under a PostgreSQL advisory lock, dedupe by provider
  deposit id, destination and token resolution, sender screening,
  dust floor, single ledger credit, quarantine on screening hits.
- Withdrawal pipeline: address book with canonicalization, internal
  address and own-deposit checks, screening, cooling off; previews with
  explicit fee, net quantity, USD valuation, expiry and a deterministic
  terms hash; placement behind an `authorize_withdrawal` step-up grant
  bound to that hash; gross hold in the real ledger inside the same
  transaction; outbox submission idempotent on the platform withdrawal
  id; progress job for status, settle and release; stuck submission
  recovery; rolling 24 hour limits; cancel while requested.
- Reserves job comparing custody holdings to ledger clearing balances
  per chain and token with consecutive-mismatch pause.
- Real-eligibility-gated routes under `/api/v1/real`, standard envelope,
  ownership filtering (`resource.not_found`), validation and
  idempotency on every state-changing endpoint.

## Bugs found and fixed during verification

1. `WithdrawalJournals.Settle` credited clearing with `-net`, leaving
   the journal unbalanced per asset; every settlement failed and
   withdrawals parked in `needs_reconciliation`. Clearing now carries
   `+net`, matching the Trading fill convention (`0109e1b`).
2. `AddressBookService` compared chain status to `"active"`; catalog
   chains use `"enabled"`, so every address add failed
   `wallet.not_available` (`0109e1b`).
3. The rolling-limit predicate called a domain helper inside an EF
   `Where`, which cannot be translated; the state list is now inline
   (`0109e1b`).
4. `AddExternalTransfers` defaulted pre-existing rows to `''`, which
   the policy converter cannot parse, breaking catalog reads and the
   migrator import on upgraded databases. Forward-only
   `BackfillExternalTransfers` sets them to `not_supported` (`43f2c11`).
5. `DELETE /api/v1/real/withdrawal-addresses/{addressId}` declared
   idempotency only in prose; the architecture test requires the
   `IdempotencyExempt` metadata. Deletion is repeat-safe (missing rows
   answer `resource.not_found`), so it is exempt with a written reason
   (`d28340a`).
6. `catalog.tokens.external_transfers` still carried `DEFAULT ''` from
   the original AddColumn migration, so raw inserts that omit the column
   produced rows the enum converter cannot parse. Forward-only
   `ExternalTransfersDefault` sets the database default to
   `not_supported`, and `CatalogSchemaTests` names the column explicitly
   (`d28340a`).

## Step 8A: API-level integration tests

`tests/AskVest.IntegrationTests/Wallets/WalletsApiTests.cs`, two facts
through `WebApplicationFactory` against shared Testcontainers PostgreSQL
with imported catalog, fake market data, fake KYC and the real ledger.
Both pass:

- `Deposit_Then_Withdrawal_Completes_And_Releases_Nothing`: deposit
  instruction issued, injected deposit credited by the polling job,
  deposit listed as `credited`, external address added behind a
  `change_withdrawal_address` grant, preview returned `previewed` with a
  terms hash, `authorize_withdrawal` grant bound to the hash placed the
  withdrawal, async progression reached `completed`, final balances show
  3 of 5 tokens available and zero held.
- `Screening_Hit_And_StepUp_Binding_Are_Enforced`: a screened address is
  refused `wallet.address_invalid`; a grant bound to a different address
  is refused `auth.step_up_invalid`.

`tests/AskVest.IntegrationTests/Wallets/WalletsSchemaTests.cs` (step 3)
proves schema constraints including withdrawal uniqueness against real
PostgreSQL.

## Step 8B: smoke tests over real HTTP

`tests/AskVest.SmokeTests/WalletsSmokeTests.cs`, two facts against the
running containers (`ASKVEST_SMOKE_BASE_URL=http://localhost:5081
ASKVEST_SMOKE_MAILPIT_URL=http://localhost:18025`, secrets from `.env`).
Both pass in one run (52s). The full smoke suite including all earlier
phases passes: 27/27 over real HTTP against the running containers.

- `Deposit_Then_Withdrawal_Completes_And_Replay_Is_Safe`: full
  eligibility journey (Mailpit verify, documents, consents, country,
  KYC webhook, learning gate, PIN), deposit instruction fetch, deposit
  injection through the diagnostics probe, balance credit observed via
  `GET /api/v1/real/balances`, deposit listed `credited`, address added
  behind step-up, preview `previewed`, placement, same-key replay
  returning the same withdrawal id, a second user's read answering 404
  `resource.not_found`, async progression to `completed`, final
  available 3 and held 0 for `SMPNVDA1`.
- `Rejected_Custody_Submission_Releases_The_Hold`: custody scenario
  `rejected` drives the placed withdrawal to `failed` and balances
  return to 5 available, 0 held; scenario reset to `normal`.

The smoke override (`docker-compose.smoke.yml`) routes
`eip155:11155111` and `eip155:17000` to the fake custody provider, sets
`Screening:Provider=fake`, shrinks deposit, progress and reserves
intervals and disables cooling off for the journey.

## Full integration suite

`tests/AskVest.IntegrationTests` against a fresh Testcontainers
PostgreSQL: 466 total, 0 errors, 0 failed, 0 skipped (1662s). This run
includes the two `WalletsApiTests`, the `WalletsSchemaTests`, the idempotency
architecture suite and every earlier module suite.

## Unit and architecture layers

- Unit: 109 Wallets tests green (transitions, terms hash, fee math,
  limit evaluation, reserves comparison, fake custody and screening
  scenarios, module options validation including the Production refusal).
- Architecture: 100 tests green including the Wallets boundary suite.
- Full solution build clean, 0 warnings 0 errors (the known flaky
  AD0001 analyzer crash cleared on retry, no suppression).

## Honestly unverified

- Live kill drills (database stop mid-placement, API kill during
  submission burst, custody outage restore, kill during settlement) were
  not executed; the fake scenarios cover the outcome space
  (`submit_timeout_lost`, `submit_timeout_filled`, `status_unavailable`,
  `delayed`, `holdings_mismatch`) at unit level only.
- The `submit_timeout_lost`, `submit_timeout_filled`, `delayed`,
  `status_unavailable` and `holdings_mismatch` custody scenarios and the
  screening `error` scenario plus the reserves-mismatch pause are unit
  and service tested but not exercised over HTTP in this phase.
- A dedicated parallel-placement concurrency burst was not added; the
  per-user advisory lock serializes placement by design.
- Deposit deduplication across repeated provider pages is covered by the
  unique provider identity constraint and unit tests, not by a live
  replay drill.
- Cooling-off enforcement is covered by unit tests; the smoke stack runs
  with `AddressCoolingOffHours=0`.
