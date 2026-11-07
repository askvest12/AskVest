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

## Steps 2 to 8

Recorded per step below as they land.
