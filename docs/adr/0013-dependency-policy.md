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
| Microsoft.EntityFrameworkCore | EF Core (transitively pinned) |
| Microsoft.EntityFrameworkCore.Relational | EF Core relational layer (transitively pinned) |
| Microsoft.EntityFrameworkCore.Design | migrations tooling (PrivateAssets) |
| Microsoft.Extensions.Options.DataAnnotations | options validation |
| Microsoft.Extensions.Options.ConfigurationExtensions | options binding from configuration |
| Microsoft.Extensions.Hosting | generic host for the migrator |
| Microsoft.Extensions.Diagnostics.HealthChecks | health check abstractions |
| Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions | health check types |
| Microsoft.AspNetCore.OpenApi | built-in OpenAPI document |
| Microsoft.AspNetCore.Authentication.JwtBearer | JWT bearer authentication for access tokens |
| Microsoft.AspNetCore.Mvc.Testing | WebApplicationFactory for API tests |
| Microsoft.IdentityModel.JsonWebTokens | JWT creation and validation |
| Scalar.AspNetCore | API reference UI |
| Serilog.AspNetCore | structured logging |
| Serilog.Extensions.Hosting | Serilog host integration |
| Serilog.Settings.Configuration | Serilog configuration binding |
| Serilog.Formatting.Compact | compact JSON log formatter |
| Serilog.Sinks.Console | console sink |
| FluentValidation | request validation |
| FluentValidation.DependencyInjectionExtensions | DI registration |
| Konscious.Security.Cryptography.Argon2 | Argon2id password and PIN hashing (pulls BouncyCastle.Cryptography transitively) |
| libphonenumber-csharp | E.164 phone number parsing and validation |
| MailKit | SMTP email transport |
| xunit.v3 | test framework (Microsoft.Testing.Platform) |
| Microsoft.NET.Test.Sdk | test SDK |
| Shouldly | assertion library |
| Microsoft.Extensions.TimeProvider.Testing | FakeTimeProvider for tests |
| Testcontainers.PostgreSql | real PostgreSQL for integration tests |
| Respawn | database reset between tests |

## Consequences

Every dependency has a stated reason and a locked, audited version. Adding a package requires updating this table.

## Alternatives rejected

- Floating versions or `latest`: unauditable supply chain.
- Skipping the audit: known vulnerabilities would ship silently.
