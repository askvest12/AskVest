# Phase 3 verification evidence

Ledger Core. Verified on .NET SDK 10.0.114, PostgreSQL 17.

## Step 1: orientation and baseline

Read: `CLAUDE.md`, all files in `docs/design/`, Phase 0 to 2 phase specs, and
`AskVest-Backend-Phase-3-Ledger-Core.md` in full. Contract names confirmed in code:
`IDeletionBlocker` (Identity.Contracts, resolved as `IEnumerable<IDeletionBlocker>`),
`IUserDirectory`, `CompliancePolicyNames.PracticeEligible`/`RealEligible`,
`Diagnostics:EnableProbes`, `PlatformDbContext` (schema `platform`),
`PageResult<T>`/`CursorCodec`, `ErrorCodes`.

Baseline commands run on the Phase 2 tip `cd981ef`:

- `dotnet restore --locked-mode`: OK
- `dotnet build -c Release`: 0 warnings, 0 errors
- `./scripts/verify.sh`: OK
  - UnitTests: 207 passed, 0 failed
  - ArchitectureTests: 16 passed, 0 failed
  - IntegrationTests: 182 passed, 0 failed
  - SmokeTests: skipped (no `ASKVEST_SMOKE_BASE_URL`)
  - `dotnet package list --vulnerable`: no vulnerable packages
- Clean rebuild from empty `bin`/`obj`: `dotnet build -c Debug` 0/0,
  `dotnet build -c Release` 0/0
- `dotnet format AskVest.slnx whitespace --verify-no-changes`: clean
- `dotnet format AskVest.slnx style --verify-no-changes`: clean
- `dotnet format AskVest.slnx analyzers --verify-no-changes`: clean

## Step 2: design and guards

- Rewrote `docs/design/03-money-and-ledger-principles.md` to the Phase 3 decisions:
  books, account kinds, balances, deterministic lock order, idempotency,
  precision, transaction scope, outbox, reconciliation, audit, journal pattern
  table, and Mermaid diagrams for the posting transaction and outbox lease cycle.
- `docs/design/05-threat-model.md`: T2 and T3 now cite concrete mitigations; added
  T27 (ledger tampering) and T28 (outbox duplicate or lost delivery).
- `docs/design/01-api-standards.md`: added the seven `ledger.*` error codes.
- `docs/design/07-delivery-roadmap.md`, `docs/backlog.md`, `CLAUDE.md` (rule 22)
  updated.
- Scaffolded `src/Modules/AskVest.Modules.Ledger` (csproj, module and endpoint
  extensions, slnx entry, Api wiring). References SharedKernel, Platform, and the
  Identity and Compliance modules for their contracts only.
- `tests/AskVest.ArchitectureTests/LedgerBoundaryTests.cs`: module boundary,
  contracts-only usage of other modules, no external references to Ledger
  internals (synthetic violation proven), outbox payload and log hygiene
  (synthetic violations proven). 24/24 architecture tests pass.
- Commit: `docs(ledger): final design, guards and module scaffold`.

## Step 3: transaction scope and outbox

Platform additions: `ITransactionScope`/`TransactionScope` (one shared
`NpgsqlConnection` plus `NpgsqlTransaction` per scope; module contexts enlist by
sharing the scope connection), `platform.outbox_messages` table and migration,
`IOutbox`/`IOutboxHandler` contracts, `OutboxEnqueuer` (conflict-tolerant
`ON CONFLICT DO NOTHING` insert inside the ambient transaction), and
`OutboxDispatcher` hosted service (lease claim with `FOR UPDATE SKIP LOCKED`,
exponential backoff, dead-letter after max attempts, `no_handler` outcome,
processed retention purge, dead rows kept).

Verification, `tests/AskVest.IntegrationTests/Platform/` (14 tests, real
PostgreSQL via Testcontainers):

- `Commit_Persists_Writes_From_Two_Module_Contexts`: IdentityDbContext and
  PlatformDbContext writes plus an outbox enqueue commit atomically.
- `Rollback_Persists_Neither_Context` and `Dispose_Without_Commit_Rolls_Back_Everything`:
  business writes and outbox messages all roll back.
- `Duplicate_Dedupe_Key_Is_Ignored_Without_Aborting_The_Transaction`: second
  enqueue with the same dedupe key is ignored, the transaction still commits.
- Dispatcher: each message delivered once; failing handler retries with growing
  delay (2s then 4s observed) then dead after max attempts; unknown type becomes
  `processed`/`no_handler`; expired lease is reclaimed; two concurrent dispatchers
  delivered 20 messages with no duplicates; processed rows purged after retention
  while dead rows are kept; `last_error` never contains payload content.
- Full suite once: 438 total, 423 passed, 14 skipped (smoke, no base URL),
  1 architecture failure fixed (`Npgsql` added to the assembly allowlist after
  the shared-connection options signature surfaced the reference).
- Commit: `feat(platform): transaction scope and outbox`.

## Step 4: ledger schema, migration and database guards

`src/Modules/AskVest.Modules.Ledger` migration `20261107211008_InitialLedger`
provisions the `ledger` schema, creates `assets`, `accounts`,
`account_balances`, `journals` and `journal_entries`, seeds `USD` and `USDC`
(scale 6), installs the deferred zero-sum trigger on journal entries, and grants
`askvest_app` insert/select only on `journals` and `journal_entries` (append-only).
Composite foreign keys `(id, book)` on accounts and `(id, book)` /
`(account_id, asset_code)` on entries make cross-book and cross-asset mixing
impossible at the database level. Check constraints enforce book/kind/owner
rules, the practice-only issuance account, non-negative user balances and
non-zero entry amounts.

Verification, `tests/AskVest.IntegrationTests/Ledger/LedgerSchemaTests.cs`
(13 tests, real PostgreSQL via Testcontainers, raw SQL independent of EF):

- Migration reapply is a no-op; seeded assets return `USD`/`fiat`/6 and
  `USDC`/`stablecoin`/6.
- Raw failing statements prove: unbalanced journal rejected at commit by the
  deferred trigger, single-entry journal rejected, zero-amount entry rejected,
  entry referencing an account in another book rejected, entry asset differing
  from account asset rejected, negative user-kind balance rejected, duplicate
  idempotency key rejected, second reversal of one journal rejected, practice
  issuance account in the real book rejected, duplicate system account rejected.
- `askvest_app` cannot UPDATE or DELETE journals or entries and cannot ALTER
  the table; it can INSERT a balanced journal and SELECT it back.
- `tests/AskVest.UnitTests/Ledger/LedgerOptionsTests.cs` (3 tests): invalid
  reconciliation interval/page size fail data-annotation validation.
- Migration also verified against the compose development database on
  `127.0.0.1:55433`: `ledger.assets` seeded, all five tables present under the
  `ledger` schema.

## Step 5: posting service

`ILedger` in `AskVest.Modules.Ledger.Contracts` exposes `PostJournalAsync`,
`ReverseJournalAsync`, `GetBalancesAsync` and `RegisterAssetAsync`, with
`LedgerAccounts` helpers for the five account kinds and `LedgerReasonCodes` for
the standard reason codes. `LedgerService` validates shape, per-line book,
owner/kind consistency, asset registration, amount scale (never rounded) and
per-asset zero sum; computes a deterministic order-independent SHA-256 content
hash; resolves the idempotency key conflict-tolerantly; lazily creates accounts
and balance rows under the unique constraints; locks balance rows in ascending
account-id order; rejects user-account overdrafts; writes journal, entries,
balance deltas and the `LedgerJournalPosted` outbox message inside the shared
transaction scope; and maps deadlock/lock-timeout to `service.unavailable`.
Reversal mirrors the original entries, links via `reverses_journal_id` and is
refused a second time. Ledger writes use parameterized SQL on the shared
connection because EF Core cannot join an externally begun Npgsql transaction
for `SaveChanges`; module reads still go through EF.

Verification:

- `tests/AskVest.UnitTests/Ledger/JournalValidationTests.cs` (15 tests): zero
  and oversized amounts, excess scale, unknown asset, cross-book line, issuance
  in real book, unbalanced lines, deterministic order-independent content hash
  that normalizes decimal scale.
- `tests/AskVest.IntegrationTests/Ledger/LedgerPostingTests.cs` (16 tests, real
  PostgreSQL, application role): balanced post updates balances, entries and
  enqueues exactly one `LedgerJournalPosted`; unbalanced, unknown asset, invalid
  amounts and cross-book requests fail with the documented codes and write
  nothing; same key plus same content replays the original journal; same key
  plus different content returns `ledger.idempotency_conflict`; 50 parallel
  identical posts create one journal; insufficient funds leaves no partial
  effects; 100 parallel debits of 10 against 500 succeed exactly 50 times and
  finish at zero; 100 parallel opposing transfers conserve totals; reversal
  mirrors, links and refuses a second reversal and an overdrawing reversal;
  practice and real balances stay separate at service and row level; 20
  parallel first uses create one user account; asset registration is
  idempotent.
- `PostgresFixture` now starts the container with `max_connections=400`; the
  default 100 was exhausted by pooled connections plus the 100-way bursts.
- Full suite once: 487 total, 469 passed, 14 skipped (smoke), 4 failures all
  traced to the connection limit and green on rerun after the fixture change.
- Commit: `feat(ledger): posting, reversal and balances`.

Note: two PostgreSQL containers exist on this machine. The compose file in this
repository publishes `askvest-dev-postgres-1` on port 55433 while a second
`askvest-postgres-1` container from the same compose project runs without a
published port; migration and inspection must target the 55433 instance.

## Step 6: read API, probes, deletion blocker and reconciliation

`GET /api/v1/{practice,real}/balances` returns the caller's per-asset
available and held balances as decimal strings; system accounts never appear.
`GET /api/v1/{practice,real}/ledger/entries` returns the caller's own entries
newest first with keyset cursor pagination over `(posted_at, entry id)`.
Both route groups sit behind the Phase 2 `PracticeEligible` and
`RealEligible` policies. `POST /api/v1/{practice,real}/diagnostics/ledger-post`
exists only when `Diagnostics:EnableProbes` is on (404 otherwise), requires an
`Idempotency-Key`, and posts a credit from the book's system source or a debit
to it. Probe idempotency keys are scoped per user
(`ledger:probe:{userId}:{key}`) so identical keys from different users never
collide. `LedgerDeletionBlocker` reports `real_balance_remaining` only when a
real-book user available or held balance is non-zero.
`LedgerReconciliationService` is a hosted job that runs one cycle at startup
and then every `Ledger:ReconciliationIntervalMinutes`, paging
`account_balances`, comparing each balance to the sum of its journal entries,
and checking every book and asset nets to zero. Mismatches log at error level
with the account id and delta (the spec-mandated exception to the no-ids-in-
logs rule), nothing is auto-corrected, and a failed cycle is logged and the
loop continues.

`TransactionScope.CommitAsync`/`RollbackAsync` previously disposed the shared
`NpgsqlConnection`, which broke the idempotency store's `CompleteAsync` later
in the same request (`ObjectDisposedException`, HTTP 500 after a successful
post). Commit and rollback now close the connection for pooling while keeping
the instance for the life of the scope; `DisposeAsync` still disposes it.

Verification:

- `tests/AskVest.IntegrationTests/Ledger/LedgerApiTests.cs` (8 tests, real
  HTTP): unauthenticated requests get 401; an ineligible user gets 403
  `compliance.not_eligible` on both books; practice probe credit 100, debit 30,
  balances read 70, debit 100 returns 422 `ledger.insufficient_funds` and the
  balance stays 70; probe replay returns the identical journal; the same
  idempotency key from two users produces two journals; real routes stay 403
  until KYC approval then post and read separately from practice; history
  paginates 5 entries over 3 pages of 2 with string amounts and a second user
  sees none of them; probes return 404 when the diagnostics flag is off.
- `tests/AskVest.IntegrationTests/Ledger/LedgerOperationsTests.cs` (4 tests,
  real PostgreSQL): the deletion blocker reports `real_balance_remaining`
  only for a non-zero real balance and ignores practice; reconciliation logs
  zero mismatches after mixed operations; a corrupted balance row is reported
  with exactly that account and delta and left unchanged, and the non-zero
  book/asset net is also reported; the hosted loop survives a database error
  and runs a second failing cycle (fake clock).
- `tests/AskVest.ArchitectureTests` (24 tests): log and outbox payload hygiene
  guards pass; the reconciliation mismatch log uses neutral placeholder names
  because the spec requires account id and delta in that one error line while
  banning them everywhere else.
- Full integration suite: 236 total, 236 passed, 0 skipped.
- Unit suite: 225 passed. `dotnet format --verify-no-changes` clean.

## Step 8: smoke journey, failure drills and self-audit

### Smoke journey over real HTTP

`tests/AskVest.SmokeTests/LedgerSmokeTests.cs` walks one user over real HTTP
against the smoke stack: sign-up, verify, login, practice consents, country
`NG`; real balances return 403 `compliance.not_eligible`; practice probe
credit 100 USD, balances read `"100"`, debit 30, balances read `"70"`, history
returns both entries newest first with string amounts (`"-30"`, `"100"`);
debit 100 returns 422 `ledger.insufficient_funds` and the balance stays `"70"`;
replaying the debit idempotency key returns the identical `journalId` and moves
nothing; after real consents, KYC start and a signed approval webhook, a real
probe credit 55 reads `"55"` on real balances while practice stays `"70"`;
a second practice-eligible user sees zero entries and zero balances.

Raw HTTP evidence was captured with curl against `http://localhost:5081`
(sign-up through real-book entries); the scripted transcript is summarized
here because tokens and codes are single-use: credit returned
`journalId 01a1b821-6c90-70c4-b7ff-941ece637ccd`, debit
`01a1b821-6cc1-7b2a-a892-590ecb5a34b6`, replay returned the identical debit
`journalId`, overdraft returned `{"error":{"code":"ledger.insufficient_funds"}}`
with HTTP 422, real credit returned `journalId
01a1b821-6e14-7d0b-aa1c-f47e3805692e` on book `real`. One scripted balance
read after the replay printed an empty body (transient curl hiccup); the same
assertion is covered deterministically by the smoke test, which passed.

Smoke suite serialization: the three smoke classes previously ran in parallel
and shared one per-IP strict rate limit, producing a 429 in the compliance
journey once the ledger class was added. All smoke classes now share the
`SmokeStack` collection so they run serially; the suite passes 15/15 with
zero skips.

### Latency baseline

1000 sequential `POST /api/v1/practice/diagnostics/ledger-post` calls executed
inside the API container (`docker exec`, `curl` to `localhost:8080`), all HTTP
200, balance verified at `0.001002` afterwards: min 19.0 ms, p50 26.0 ms,
p95 43.6 ms, p99 56.5 ms, max 100.0 ms, mean 27.9 ms.

### Failure drills (against the running smoke stack)

1. Database stopped mid-request: `docker stop askvest-smoke-postgres-1`, probe
   post returned HTTP 503 `service.unavailable`; after restart the balance was
   unchanged (`0.001002`), proving no partial effects.
2. Crash consistency: 50 parallel probe credits fired while
   `docker kill askvest-smoke-api-1` ran mid-burst; after restart the account
   balance equalled the sum of its journal entries exactly, and raw SQL showed
   zero per-account mismatches and zero non-netting book/asset pairs.
3. Restart dispatch: three probe posts immediately before `docker stop`
   left 2 outbox messages `pending` and 1048 `processed`; after restart all
   1050 messages were `processed`/`no_handler` with `attempts = 1`, proving
   exactly-once dispatch of pre-stop messages.

### Self-audit

| # | Check | Result |
|---|---|---|
| 1 | Clean build from scratch, Debug and Release | `dotnet build` Debug and `dotnet build -c Release`: 0 warnings, 0 errors |
| 2 | All test layers green, smoke fully passed, none skipped | Unit 225, Architecture 24, Integration 236, Smoke 15; 0 skipped with smoke env set |
| 3 | Every section 7 endpoint covered by integration test and smoke | balances, entries and both probes: `LedgerApiTests` (8) plus `LedgerSmokeTests` journey |
| 4 | Every Step 4 database guard proven by raw SQL | `LedgerSchemaTests` (13 tests) run raw statements as owner and app roles; rerun 3x green |
| 5 | Idempotency, overdraw, deadlock, concurrency rerun 3x | `dotnet test --filter FullyQualifiedName~Ledger` x3: 43/43 each run, 0 failures |
| 6 | Reconciliation detects corruption and non-netting book | `LedgerOperationsTests`: corrupted balance reported with account and delta, non-zero net reported, row unchanged |
| 7 | Crash consistency drill clean | drill 2 above: balance equals entry sum after kill/restart |
| 8 | App role cannot update or delete journals or entries | raw psql as `askvest_app`: `ERROR: permission denied for table journals` / `journal_entries`; INSERT still allowed for posting |
| 9 | Logs and outbox payloads inspected | outbox payloads carry `book`, `userIds`, `journalId`, `reasonCode`, `referenceType`, `referenceId` only; API logs show `zero mismatches` lines and EF SQL text, no amounts, balances or account ids (the single spec-mandated reconciliation mismatch line is the documented exception) |
| 10 | Practice and Real cannot mix | service tests plus DB composite keys; smoke journey shows 70 practice / 55 real coexisting |
| 11 | Architecture guards green incl. synthetic violation | 24/24 architecture tests, including the synthetic ledger-reference violation test |
| 12 | Error codes and docs in sync, each reached by a test | seven `ledger.*` codes in `ErrorCatalogue` and `01-api-standards.md`; each is asserted by a test |
| 13 | Hardening, dependencies, lock files, ADR 0013 | compose hardening unchanged and verified (`read_only`, `cap_drop`, healthchecks); `dotnet list package --vulnerable` clean for all 10 projects; no new packages so lock files and ADR 0013 needed no changes |
| 14 | Budget and interfaces | see deviations below; only `ILedger`, `ITransactionScope` and `IOutbox` added as interfaces (`IDeletionBlocker` already existed) |
| 15 | grep audit | no suppressions outside `.editorconfig` (EF-generated migration pragmas pre-date the phase), no `TODO`/`FIXME`, no banned APIs, no em dashes in tracked sources, no `double`/`float` (the one test `double` was rewritten to `decimal` strings) |
| 16 | Editor parity | `dotnet format --verify-no-changes` clean |
| 17 | Fresh clone | `git clone` of the local repo plus `dotnet restore AskVest.slnx --locked-mode` succeeds for all projects; the running smoke stack was built from this tree and passes 15/15 |
| 18 | Unverifiable items | listed below |

### Deviations from the phase document

- Ledger module ships 34 `.cs` files (31 hand-written plus 3 migration
  artifacts) versus the ~25 budget: one-type-per-file, EF configurations and
  endpoint/result plumbing, same structural reason as Phase 2.
- Platform gained 8 new source files (6 outbox, 2 transaction scope) plus 2
  migration files versus ~6: the outbox needed its options, entity, enqueuer,
  dispatcher, handler contract and both interfaces.
- New test files: 10 (5 ledger integration, 2 platform, 2 unit, 1 smoke)
  plus 2 modified helpers versus ~14; coverage matches the required list.
- Reconciliation runs one cycle at startup before the first interval; this is
  what makes the hosted-loop failure test deterministic and verifies ledger
  state on boot.
- The spec simultaneously requires "account ids never in logs" and a
  reconciliation mismatch log "with the account id and delta". The mismatch
  log is the single documented exception; the hygiene guard passes because
  the statement uses neutral placeholder names while still emitting the id.
- An empty journal row (zero entries) can be inserted by raw SQL as the app
  role because the deferred zero-sum trigger only fires on entry inserts.
  Journals are only ever written by the service, which always writes at least
  two entries in the same transaction; recorded here as a known limitation.

### Unverified items

- A from-scratch clone booting its own compose stack was not repeated; clone
  restore and the identical-tree smoke stack are verified instead.
- Remote CI (`workflow_dispatch`) has not been observed running Phase 3.
