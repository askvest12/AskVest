# Phase 2 verification evidence

Running log of commands and results for the Compliance and Eligibility phase.

## Step 1. Orientation and editor parity

### Repository verify script

`./scripts/verify.sh` on a clean tree:

- `dotnet restore --locked-mode`: ok
- `dotnet build -c Release`: 0 warnings, 0 errors
- Unit tests: 168 passed
- Architecture tests: 11 passed
- Integration tests: 136 passed, 1 failed:
  `MaintenanceApiTests.Maintenance_Removes_Only_Expired_Rows_And_Keeps_Live_And_Rotated_Tokens`
  expected 1 deleted step-up grant, got 2. The suite shares one Postgres
  container and never resets between tests, so expired grants left by earlier
  tests inflate the sweep count. Rerun alone:
  `dotnet test --project tests/AskVest.IntegrationTests -c Release --filter "FullyQualifiedName~Maintenance_Removes_Only_Expired"`
  passed (1/1). Pre-existing flake, unrelated to Phase 2.
- `dotnet list package --vulnerable --include-transitive`: no vulnerable
  packages in any project.

### Phase 1 pieces confirmed

- Identity contracts present: `IStepUpAuthorizer`, `IDeletionBlocker`.
- `IUserDirectory` and `ICurrentUserContext` were documented in
  `docs/design/08-identity-module.md` but never shipped. Added the smallest
  piece: `Contracts/IUserDirectory.cs` (`UserAccountSnapshot(IsActive,
  EmailVerified)`, `FindAsync` returns null for unknown ids) implemented by
  `Services/UserDirectoryService.cs` over `IdentityDbContext`, registered in
  `AddIdentityModule`. Compliance reads Identity only through this contract.
- Anonymous allow-list: `AnonymousEndpointCatalog` plus
  `AnonymousEndpointGuard` architecture guard (in Api and mirrored in
  ArchitectureTests).
- Returns-secrets tag: `ReturnsSecretsAttribute` plus
  `IdempotencyEndpointGuard` rule that secrets endpoints must be exempt.
- `Diagnostics:EnableProbes` flag gates `/diagnostics/probe`.
- `strict` rate limit policy in `RateLimitingExtensions`.
- Module blueprint: `docs/design/09-module-blueprint.md`.
- Mailpit smoke helpers: `tests/AskVest.SmokeTests/IdentitySmokeTests.cs`.

### Editor parity

- Deleted every `bin`, `obj`, `.vs` folder, then `dotnet restore
  --locked-mode`: ok.
- `dotnet build -c Debug`: 0 warnings, 0 errors.
- `dotnet build -c Release`: 0 warnings, 0 errors (one transient CLR crash
  `0x80131506` on the first attempt; clean on retry).
- `dotnet format AskVest.slnx whitespace --verify-no-changes`: initially
  flagged `PostgresFixture.cs` indentation (fixed by `dotnet format
  whitespace`) and UTF-8 BOM charset on EF migration files (fixed by marking
  `[src/**/Migrations/[0-9]*.cs]`, `*.Designer.cs` and `*ModelSnapshot.cs` as
  `generated_code = true` with `charset = utf-8-bom` in `.editorconfig`;
  `MigrationRunner.cs` in the same folder is hand-written and stays under
  normal rules).
- `dotnet format AskVest.slnx style --verify-no-changes`: initially flagged
  13 import-ordering issues (fixed by `dotnet format style`) and 29
  IDE1006 naming violations: every `private static readonly` field written
  PascalCase instead of `_camelCase`. Renamed them all to `_camelCase`
  (`_entries`, `_safeMethods`, `_coveredMethods`, `_jsonMethods`,
  `_docsPrefixes`, `_invalidEmail`, `_invalidCredentials`, `_notPending`,
  `_invalidCode`, `_known`, `_refreshInvalid`, `_refreshConflict`,
  `_notFound`, `_invalidPhone`, `_pinNotSet`, `_pinInvalid`,
  `_stepUpInvalid`, `_util`, `_deniedPasswords`, `_contextFactories`).
- `dotnet format analyzers --verify-no-changes`: clean.

### Severity table (deliberate violations in scratch files)

| Rule | Build fails | Action |
|---|---|---|
| `csharp_style_namespace_declarations = file_scoped` | yes (IDE0161) | kept at error |
| `csharp_prefer_braces = true` | yes (IDE0011) | kept at error |
| `dotnet_style_require_accessibility_modifiers` | yes (IDE0040) | kept at error |
| `csharp_using_directive_placement = outside_namespace` | yes (IDE0065) | kept at error |
| `dotnet_style_qualification_for_field/property/method/event = false` | no (`this._x` builds clean) | lowered to suggestion |
| `dotnet_naming_rule.*` (all five, severity error) | no (IDE1006 never reported by csc; verified with camelCase class, PascalCase field, camelCase const, lowercase interface) | lowered to suggestion |

Naming rules and qualification rules are evaluated by the IDE and
`dotnet format` only; the compiler never emits them, so at `error` severity
they produced red editor lines that `dotnet build` could not reproduce.
Existing violations were still fixed (renames above) before lowering.

- `.vscode/settings.json` points the editor at `AskVest.slnx`;
  `.vscode/extensions.json` recommends `ms-dotnettools.csdevkit` and
  `ms-dotnettools.csharp`. The installed C# extension version cannot be
  checked from this environment; `global.json` pins SDK `10.0.114` with
  `latestFeature` roll-forward and `.slnx` is supported by current C# Dev
  Kit. README gained a click-by-click editor troubleshooting section.
