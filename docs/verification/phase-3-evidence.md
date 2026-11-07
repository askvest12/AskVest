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

## Steps 4 to 8

Recorded per step below as they land.
