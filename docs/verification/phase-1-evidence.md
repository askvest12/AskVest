# Phase 1 Verification Evidence

Environment: Linux x86_64, .NET SDK 10.0.114, Docker 29.8.1, Compose v5.5.1,
PostgreSQL 17 (Testcontainers for tests, container for smoke), Mailpit v1.32.0.
All commands run from the repository root unless noted.

Companion artifacts:

- `phase-1-raw-http.log` - redacted curl transcript of the full journey.
- `phase-1-failure-drills.log` - database down, Mailpit down, API restart drills.
- `phase-1-log-hygiene.log` - container log secret scan (reproduce with
  `scripts/phase-1-log-hygiene.sh`).
- `phase-1-db-secrets.log` - post-smoke database secret scan
  (reproduce with `scripts/phase-1-db-secret-scan.sh`).

## Row 1 - Clean build from scratch, zero warnings, zero errors

- `dotnet build AskVest.slnx -c Release` -> Build succeeded, 0 Warning(s),
  0 Error(s). Warnings are errors (`TreatWarningsAsErrors` in
  `Directory.Build.props`); analyzers run at max level.
- Earlier in the phase a from-scratch restore was verified via
  `dotnet restore --locked-mode` on a fresh clone (see row 17).

## Row 2 - Every test layer green, smoke fully passed, none skipped

Final runs on the verification commit:

- `dotnet test --project tests/AskVest.UnitTests -c Release` -> 168 passed,
  0 failed, 0 skipped.
- `dotnet test --project tests/AskVest.IntegrationTests -c Release` ->
  137 passed, 0 failed, 0 skipped (real PostgreSQL 17 via Testcontainers).
- `dotnet test --project tests/AskVest.ArchitectureTests -c Release` ->
  11 passed, 0 failed, 0 skipped.
- `ASKVEST_SMOKE_BASE_URL=http://127.0.0.1:5082 ASKVEST_SMOKE_MAILPIT_URL=http://127.0.0.1:18026 dotnet test --project tests/AskVest.SmokeTests -c Release` ->
  12 passed, 0 failed, 0 skipped against `docker compose -f
  docker-compose.yml -f docker-compose.smoke.yml up -d --build --wait`.
- The smoke suite was executed twice consecutively with identical results.

Rerun instructions are in the final report and `scripts/verify.sh`.

## Row 3 - Every Phase 1 section 6 endpoint has tests and smoke coverage

All 26 catalogue routes are exercised by integration tests against real
PostgreSQL (`tests/AskVest.IntegrationTests/Identity/*ApiTests.cs`).

Smoke coverage over real HTTP against the running containers
(`tests/AskVest.SmokeTests/IdentitySmokeTests.cs`):

- sign-up, verify-email (code via Mailpit API), login device A,
  login device B + verify-device, refresh rotation + reuse/theft,
  change-password, forgot/reset-password, pin set + step-up grant
  (single-use proven), deletion-request + deletion-cancel,
  sessions/devices/security-events/me reads, device revoke - all inside
  `Identity_Journey_Signup_To_Deletion_Cancel`.
- `Protected_Routes_Require_Bearer_Token` hits a sample of protected routes
  without a token.
- `Login_Brute_Force_Hits_Rate_Limit_For_Existing_And_Unknown_Email` and
  `SignUp_For_Existing_Email_Is_Uniform` cover the abuse rows.

Endpoints not called by a dedicated smoke request but covered by the journey
or by design: `resend-verification`, `sessions/{id}/revoke`,
`sessions/revoke-others`, `pin/change`, `pin/reset-request`, `pin/reset`
(integration-tested; the journey covers their shared code paths). The two
phone routes are feature-flagged off in the smoke environment, so smoke
verifies their absence instead (`Phone_Routes_Do_Not_Exist_When_Flag_Is_Off`
integration test plus OpenAPI check); enabling the flag in smoke is listed
under unverified items.

## Row 4 - Architecture guards green, synthetic violations proven

Guards and proofs:

- Default-deny: `AnonymousEndpointGuard.FindViolations` plus
  `AnonymousGuardTests` (3 tests) feed synthetic `RouteEndpoint` objects -
  anonymous-outside-allow-list flagged, unprotected flagged, protected and
  allow-listed pass. Live check: `DefaultDenyTests` (2 tests) scan the real
  endpoint table.
- Idempotency declaration and returns-secrets rule:
  `IdempotencyEndpointGuard.FindViolations` (new this step) enforces
  declaration-or-exemption, forbids `ReturnsSecrets` combined with
  `RequiresIdempotency`, and requires `IdempotencyExempt` on secret-returning
  endpoints. `IdempotencyGuardTests` (4 tests) prove each rule with synthetic
  endpoints; `IdempotencyArchitectureTests` runs the guard over the live
  endpoint table.
- Module boundaries: `ModuleBoundaryTests` (3 tests) assert the Identity
  assembly references only SharedKernel/Platform plus allow-listed packages,
  keeps its public surface inside module namespaces, and that endpoint
  marker attributes stay public for modules.

`dotnet test --project tests/AskVest.ArchitectureTests` -> 11/11 green.

## Row 5 - Section 11 test matrix, at least one test per row

Mapping (I = integration, U = unit, S = smoke, A = architecture):

1. `SignUp_Creates_Pending_User_And_Queues_Verification_Email` (I).
2. `SignUp_Existing_Active_Account_Gets_Notice_And_No_Code` plus resend tests (I).
3. `SignUp_Response_Is_Uniform_Across_Account_States` (I) and
   `SignUp_For_Existing_Email_Is_Uniform` (S).
4. `Concurrent_SignUps_Create_Exactly_One_User` (I, twenty parallel requests).
5. `SignUp_Weak_Passwords_Return_Field_Errors`,
   `SignUp_Password_Containing_Email_Local_Part_Is_Rejected`,
   `Breached_Password_Fails_When_Check_Enabled` (U) (I+U).
6. `SignUp_Unknown_Property_Is_Rejected` (I), `UnknownProperty_IsRejected` (U).
7. `SignUp_Idempotent_Replay_Returns_Identical_Response`,
   `SignUp_Same_Key_Different_Body_Returns_Conflict` (I).
8. `VerifyEmail_Wrong_Code_Returns_Invalid_Code`,
   `VerifyEmail_Expired_Code_Returns_Invalid_Code`,
   `VerifyEmail_Consumed_Code_Returns_Invalid_Code`,
   `VerifyEmail_Exhausted_Attempts_Return_Invalid_Code`,
   `VerifyEmail_Unknown_Email_Returns_Invalid_Code` (I).
9. `Concurrent_Verifications_Produce_Exactly_One_Success` (I).
10. `Resend_Within_Cooldown_Sends_Nothing_But_Stays_Uniform`,
    `Resend_Hourly_Cap_Suppresses_Send_Without_Revealing_State`,
    `Resend_After_Cooldown_Sends_New_Code` (I).
11. `VerifyEmail_Valid_Code_Activates_User_Without_Tokens` (I).
12. `Login_First_Device_Is_AutoTrusted_And_Returns_Tokens` (I).
13. `Login_Wrong_Password_And_Unknown_Email_Are_Uniform`,
    `Login_Verify_Call_Count_Is_Identical_For_Existing_And_Unknown_Email` (I).
14. `Login_Throttles_After_Failure_Limit_With_Retry_After`,
    `Login_Throttles_By_Client_Ip_Key_For_Any_Email`,
    `Lock_Duration_Doubles_On_Repeat_And_Caps_At_Maximum`,
    `Login_Success_Resets_Throttle` (I).
15. `Login_Throttled_Attempt_Does_Not_Evaluate_Password` (I, hasher spy).
16. `Login_Unverified_Email_Returns_Email_Not_Verified` (I).
17. `Login_Second_Device_Requires_Verification_Then_Authenticates`,
    `VerifyDevice_Wrong_Code_Returns_Invalid_Code` (I); device alert email
    asserted via the dev message capture (I).
18. `Eleventh_Session_Evicts_The_Oldest` (I).
19. `Login_Rehashes_Outdated_Argon2_Parameters` (I),
    `NeedsRehash_Detects_Old_Parameters` (U).
20. `Access_Token_Expiry_Is_Enforced`,
    `Access_Token_Without_Session_Is_Rejected`,
    `Tokens_In_Wrong_Places_Are_Rejected`,
    `Refresh_With_Unknown_Token_Returns_Invalid`,
    `Symmetric_Algorithm_Confusion_Is_Rejected`,
    `Unknown_Key_Id_Is_Rejected`, `Wrong_Issuer_Is_Rejected`,
    `Expired_Token_Reports_Expired`, `Tampered_Token_Is_Invalid` (I+U).
21. `Tokens_In_Wrong_Places_Are_Rejected` (I).
22. `Sessions_List_Marks_Current_And_Revokes_Own_Session` plus
    `PendingDeletion` gate tests; session revocation invalidates the access
    token on the next request (I).
23. `Refresh_Rotates_Token_Pair_And_Old_Token_Fails`,
    `Refresh_Idle_And_Absolute_Expiries_Are_Enforced` (I, fake clock).
24. `Parallel_Refresh_Issues_Exactly_One_Pair` (I).
25. `Refresh_Reuse_After_Grace_Revokes_Session_And_Alerts` (I).
26. `Logout_Revokes_Session_And_Refresh_Token` (I).
27. `Idempotency_Scope_Is_Per_User` (I).
28. `Disabled_Account_Token_Is_Rejected`,
    `Pending_Deletion_Restricts_Endpoints_Until_Cancelled` (I).
29. `Change_Password_Requires_Current_Password`,
    `Change_Password_Revokes_Other_Sessions_Keeps_Current` (I).
30. `Forgot_Password_Is_Uniform_For_Unknown_Email`,
    `Forgot_And_Reset_Password_Flow` (I).
31. `Reset_Password_Rejects_Invalid_And_Reused_Code` (I).
32. `Set_Pin_Requires_Password_Rejects_Weak_And_Stores_Hash` (I),
    `Weak_Passwords_Fail`/PIN policy unit tests (U).
33. `Pin_Failures_Lock_With_Doubling_And_Success_Resets` (I).
34. `Reset_Pin_Revokes_Outstanding_Grants`, pin change flow in
    `PinStepUpApiTests` (I).
35. `StepUp_Grant_Binds_User_Session_Purpose_And_Binding`,
    `StepUp_Expired_Grant_And_Concurrent_Consumption` (I).
36. `Phone_Routes_Do_Not_Exist_When_Flag_Is_Off`,
    `Add_And_Verify_Phone`,
    `Verified_Number_Is_Unique_And_Conflict_Looks_Like_Invalid_Code`,
    `Invalid_Phone_And_Code_Rejected`,
    `Phone_Unique_Index_Is_Filtered_To_Verified_Phones` (I).
37. `Me_Returns_Own_Profile_With_Masked_Phone_And_No_Secrets` (I).
38. `Sessions_List_Marks_Current_And_Revokes_Own_Session`,
    `Session_Revocation_Of_Other_Users_Session_Returns_NotFound`,
    `Device_Revocation_Of_Other_Users_Device_Returns_NotFound`,
    `Devices_List_And_Device_Revocation_Kills_Sessions`,
    `Security_Events_Of_Other_User_Are_Not_Exposed` (I).
39. `Security_Events_Are_Own_Only_Coarse_Ip_And_Cursor_Paginated` (I).
40. `Deletion_Request_Requires_Password_And_StepUp_Grant`,
    `Deletion_Request_With_Blocker_Returns_Reasons` (I, fake blocker).
41. `Pending_Deletion_Restricts_Endpoints_Until_Cancelled` (I).
42. `Anonymization_Removes_Personal_Data_And_Is_Idempotent` (I, fake clock).
43. `phase-1-log-hygiene.log` - zero occurrences of email, password, codes,
    access and refresh tokens across 77 captured container log lines.
44. `Database_Stores_No_Plaintext_Secrets` (I) plus
    `phase-1-db-secrets.log` post-smoke scan.
45. `Every_NonGet_Endpoint_Declares_Idempotency_Or_Exemption` (I) and
    `IdempotencyGuardTests` (A).
46. `Account_Endpoints_Require_Authentication` (I) and smoke
    `Protected_Routes_Require_Bearer_Token` (S).
47. `Oversized_Fields_Are_Rejected_With_Validation_Errors`,
    `Login_With_Sql_Injection_Style_Email_Is_Uniform`,
    `SignUp_With_Sql_Injection_Style_Email_Is_Rejected_Or_Uniform` (I).

## Row 6 - Error catalogue and docs in sync; every new code path reachable

- `ErrorCodes` extended with the Phase 1 `auth.*`, `account.*`,
  `rate_limit.*` and `service.*` codes; `ApiErrorMapper_MapsEveryCatalogueCode`
  and `EveryCode_*` unit tests keep catalogue, mapper and
  `docs/design/01-api-standards.md` in sync.
- `ApiResults`/service result paths for every new code are exercised by the
  integration tests listed in row 5 (each error code asserted at least once).
- OpenAPI documents every route with summaries, tags, bearer scheme and
  standard error responses; verified live in the smoke stack (`/openapi/v1.json`
  rendered during Step 16 verification).

## Row 7 - No secrets in captured logs

- `phase-1-log-hygiene.log`: a full sign-up, Mailpit code retrieval,
  verify, login, refresh rotation and wrong-password login were executed
  against the smoke container; the container log window was then grepped for
  the email, password, verification code, both access tokens and both refresh
  tokens - 0 occurrences in 77 lines. Reproduce:
  `scripts/phase-1-log-hygiene.sh`.
- Request logging middleware logs route templates, status and elapsed time
  only; no bodies, headers or query strings are logged.

## Row 8 - No plaintext secrets in the database after the smoke run

- `phase-1-db-secrets.log` (`scripts/phase-1-db-secret-scan.sh` against
  `askvest-smoke-postgres-1`): 40 users all `argon2id$` password hashes, 7 PIN
  hashes all `argon2id$`, all token/code hashes 64-char keyed hex, zero `@`
  markers in secret columns, zero bare 6-digit codes, no plaintext password or
  PIN columns exist.
- `Database_Stores_No_Plaintext_Secrets` (I) plants candidate secrets
  (password, PIN, refresh token, grant, code) and scans every identity column
  for their plaintext forms.

## Row 9 - Security events for every type, immutable to the app role

- `Every_Security_Event_Type_Is_Written_And_Immutable` (I) drives the whole
  lifecycle and asserts every `SecurityEventType` member appears in
  `identity.security_events` (LoginThrottled via a dedicated throttle user,
  AccountAnonymized via the anonymization job).
- The same test connects as `askvest_app` and proves INSERT/UPDATE/DELETE on
  `security_events` are denied; `Security_Events_Are_Append_Only_For_Application_Role`
  (I) and the `GrantSecurityEventAnonymizationColumns` migration scope the only
  permitted UPDATE to the anonymization column set.

## Row 10 - Cross-user access returns not found

- `Session_Revocation_Of_Other_Users_Session_Returns_NotFound`,
  `Device_Revocation_Of_Other_Users_Device_Returns_NotFound`,
  `Security_Events_Of_Other_User_Are_Not_Exposed` (I) - all answer the
  not-found envelope, never forbidden.

## Row 11 - Enumeration resistance by response comparison and hash counting

- `Login_Wrong_Password_And_Unknown_Email_Are_Uniform`,
  `Login_Verify_Call_Count_Is_Identical_For_Existing_And_Unknown_Email` (I,
  hasher spy proves identical Argon2 verify counts),
  `SignUp_Response_Is_Uniform_Across_Account_States`,
  `Forgot_Password_Is_Uniform_For_Unknown_Email`,
  `Resend_Unknown_Email_Is_Uniform`,
  `Verified_Number_Is_Unique_And_Conflict_Looks_Like_Invalid_Code` (I),
  smoke `SignUp_For_Existing_Email_Is_Uniform` and
  `Login_Brute_Force_Hits_Rate_Limit_For_Existing_And_Unknown_Email` (S).

## Row 12 - Hashing parameters, measured duration, concurrency cap

- Parameters (`IdentityOptions` defaults, validated at startup): Argon2id
  memory 19456 KiB, iterations 2, parallelism 1, hash length 32 bytes,
  16-byte salt, peppered with a versioned server secret; `NeedsRehash`
  upgrades outdated parameters on successful login
  (`Login_Rehashes_Outdated_Argon2_Parameters`).
- Measured on this workstation (.NET 10, x86_64): one Argon2id hash at
  production parameters takes roughly 130-340 ms (cold start higher);
  `Production_Parameters_Meet_Duration_Bounds` (U) asserts hash and verify
  stay under 5 s.
- Concurrency cap: `CredentialHashingLimiter` (semaphore, default 4,
  queue timeout) proven by `Concurrency_Limit_Is_Enforced` and
  `Queue_Timeout_Throws_HashingBusy` (U).

## Row 13 - Container hardening re-verified, no secrets in image history

- `docker history askvest-api:latest --no-trunc`: multi-stage build, final
  stage copies only `/app/publish`, runs as non-root `askvest`, exposes 8080.
  No secrets, connection strings or keys appear in any layer; runtime
  configuration arrives via environment at container start.
- Compose keeps `read_only`, `cap_drop: ALL`, `no-new-privileges`, tmpfs
  only; verified again when the smoke stack was rebuilt for Step 17.
- `.env` files are gitignored; only `.env.example` templates are tracked.

## Row 14 - Dependencies audited, ADR 0013 complete

- `dotnet list package --vulnerable --include-transitive` -> no vulnerable
  packages in any of the 9 projects.
- `dotnet list package --deprecated --include-transitive` -> clean after
  upgrading `xunit.v3` 3.2.2 -> 4.0.2 (removes the legacy
  `Microsoft.Testing.Platform.MSBuild` 1.x transitive dependency; all test
  suites re-verified green on the new version).
- `packages.lock.json` files regenerated and committed for every project;
  NuGetAudit stays enabled.
- ADR 0013 now lists every centrally managed package with its purpose,
  including all Phase 1 additions (Argon2, libphonenumber, MailKit, JWT
  bearer, Serilog sinks, test infrastructure).

## Row 15 - Comment and naming audit

- Comments explain why only; no commented-out code; no TODO/FIXME/HACK in
  `src/` or `tests/` (grep clean).
- Names use full words; `Id`, `Utc`, `Kyc` abbreviations only; one type per
  file; module types `internal` unless consumed across the boundary
  (enforced by `Identity_Public_Surface_Stays_Within_Module_Namespaces`).

## Row 16 - Grep audit

- No `#pragma warning disable` or `[SuppressMessage]` in hand-written code.
  The only remaining suppressions live in `.editorconfig`, each with a
  one-line reason: CA2000 for `AccessTokenService` (ECDsaSecurityKey owns the
  ECDsa instance), CA5350 for `BreachPasswordChecker` (HIBP k-anonymity API
  requires SHA-1 prefixes, never used for storage), plus pre-existing
  test/migration scopes from Phase 0. Generated EF migrations carry the
  standard tool-emitted pragma blocks.
- No TODO/FIXME; no `double`/`float` in `src/`; banned APIs enforced by
  `BannedSymbols.txt` (RS0030) - `DateTime.UtcNow`, `Guid.NewGuid()`,
  `System.Random`, `FromSqlRaw` family all rejected at build time.
- No em dashes in code, docs or scripts (the only remaining `—` is inside a
  quoted grep pattern in `phase-0-evidence.md` documenting the Phase 0 check
  itself).
- Phase 1 markdown docs contain no code blocks other than Mermaid where
  applicable.

## Row 17 - Fresh clone follows README alone

- `git clone` of the repository to `/tmp/askvest-fresh-verify`, followed by
  README steps only: `dotnet restore --locked-mode`, `dotnet build` (0
  warnings, 0 errors), Release build artifacts produced.
- The compose stack brings up healthy via `docker compose up -d --build
  --wait` (migrator runs before api; `/health/ready` returns 200).
- Smoke suite executed from the fresh clone against the running stack:
  12/12 passed.

## Row 18 - Design docs and ADRs re-read against implementation

- `docs/design/08-identity-module.md` and `docs/design/09-module-blueprint.md`
  match the shipped structure: one project per module, contracts namespace,
  own schema/history, endpoint marker attributes, default-deny catalog.
- ADRs 0001-0015 reviewed; ADR 0013 updated with the complete package
  register. No contradictions found between docs and code.

## Row 19 - Unverifiable items, listed plainly

- Remote CI: the workflow is manual-only per request; no CI run exists for
  the Phase 1 commits yet.
- Real SMS provider: only the development/forwarding adapter exists; no
  production SMS gateway was configured or tested.
- Production secret manager: secrets come from environment variables; no
  external vault integration exists yet.
- HaveIBeenPwned breach check: implemented fail-open behind
  `PasswordBreachCheckEnabled`; exercised in unit tests with a fake
  transport, not against the live API in CI.
- Argon2 duration varies by host; the recorded figure is from the
  development workstation, not a production benchmark.
- Phone routes were verified with the feature flag on in integration tests
  only; the smoke stack runs with the flag off by design.
