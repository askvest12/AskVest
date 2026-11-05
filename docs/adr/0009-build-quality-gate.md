# ADR 0009: Build quality gate

- Status: Accepted

## Context

Strictness must be mechanical, not cultural, or it erodes.

## Decision

`TreatWarningsAsErrors` at every level, `AnalysisLevel=latest-all`, `EnforceCodeStyleInBuild`, banned APIs via `Microsoft.CodeAnalysis.BannedApiAnalyzers` + `BannedSymbols.txt`, central package versions with lock files (`RestorePackagesWithLockFile`, locked mode in CI), `NuGetAudit` at `low` sensitivity across all packages. Suppressions only in `.editorconfig`, each with a one-line reason. `dotnet format --verify-no-changes` runs in CI.

## Consequences

Every build is a gate: warnings, banned APIs, style and vulnerabilities all fail it. New suppressions require a written reason in the repo.

## Alternatives rejected

- Warnings as warnings: they accumulate and hide real issues.
- Per-project analyzer config: inconsistent enforcement; root files keep one policy.
