# ADR 0013: Dependency policy

- Status: Accepted

## Context

Supply-chain risk is a first-class threat (T16). Versions must be deliberate and auditable.

## Decision

Central package management (`Directory.Packages.props`), transitive pinning, lock files committed per project, `NuGetAudit` enabled at `low` sensitivity across all packages, Dependabot weekly for nuget/docker/github-actions, no prerelease packages. Every package is justified below.

## Package register

| Package | Purpose |
|---|---|
| Microsoft.CodeAnalysis.BannedApiAnalyzers | Banned API enforcement (RS0030) |
| Npgsql.EntityFrameworkCore.PostgreSQL | EF Core provider for PostgreSQL |
| EFCore.NamingConventions | snake_case mapping |
| Microsoft.EntityFrameworkCore.Design | migrations tooling (PrivateAssets) |
| Microsoft.Extensions.Options.DataAnnotations | options validation |
| Microsoft.AspNetCore.OpenApi | built-in OpenAPI document |
| Scalar.AspNetCore | API reference UI |
| Serilog.AspNetCore | structured logging |
| Serilog.Formatting.Compact | compact JSON log formatter |
| FluentValidation | request validation |
| FluentValidation.DependencyInjectionExtensions | DI registration |
| xunit.v3 | test framework (Microsoft.Testing.Platform) |
| Microsoft.NET.Test.Sdk | test SDK |
| Shouldly | assertion library |
| Microsoft.Extensions.TimeProvider.Testing | FakeTimeProvider for tests |
| Microsoft.AspNetCore.Mvc.Testing | WebApplicationFactory for API tests |
| Testcontainers.PostgreSql | real PostgreSQL for integration tests |
| Respawn | database reset between tests |
| NetArchTest.Rules | architecture rule tests |

## Consequences

Every dependency has a stated reason and a locked, audited version. Adding a package requires updating this table.

## Alternatives rejected

- Floating versions or `latest`: unauditable supply chain.
- Skipping the audit: known vulnerabilities would ship silently.
