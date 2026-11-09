# Phase 9 verification evidence

AI Research Chat. Verified on .NET SDK 10.0.114, PostgreSQL 17,
Docker Compose smoke stack (`askvest-smoke` project, API on port 5081,
Mailpit on 18025). Spec: `AskVest-Backend-Phase-9-AI-Research-Chat.md`.

## Commits

- `ce344a2` docs(assistant): design, guards and small earlier-module changes
- `b559fff` feat(assistant): schema, configuration, model seam and fake model
- `b6f2f01` feat(assistant): nine read-only tools with strict scoping
- `0c95c4f` feat(assistant): bounded agent loop with policy floor and audit
- `ebd92a1` feat(assistant): endpoints, gating, limits, retention and evaluation
- `c7f7993` chore(assistant): compose, prompt packaging, readme and ci
- `cb1f514` test(assistant): smoke journey, stack evaluation and failure drills
- final: docs(assistant): phase 9 verification evidence

## Step 8A: smoke journey over real HTTP

`tests/AskVest.SmokeTests/AssistantSmokeTests.cs`, one journey fact against
the running containers (`ASKVEST_SMOKE_BASE_URL=http://localhost:5081
ASKVEST_SMOKE_MAILPIT_URL=http://localhost:18025 dotnet test --project
tests/AskVest.SmokeTests --filter AssistantSmokeTests`). Passed.

Covered, in order:

1. Practice-eligible user without `ai_processing` consent: POST
   `/api/v1/assistant/messages` answered 403 `compliance.not_eligible`
   with a detail naming `ai_processing`. After accepting the assistant
   documents, a price question returned citations that resolve and a price
   fact card whose value and label equal the
   `/api/v1/marketdata/prices` response (compared as decimals; the API
   serializes `79.320000000000000000`, the card `79.32`).
2. After a practice buy of `sample-nvda`, the portfolio question's fact
   card `holdingsValue` equals the practice portfolio endpoint's totals.
   The real-portfolio question answered "not available" (user is not
   real-eligible).
3. Scenario drills through `POST /api/v1/diagnostics/assistant-scenario`:
   `advice_phrase` returns the safe fallback with `policy.blocked` true;
   `ungrounded_number` returns the safe fallback (`policy.blocked` false by
   design: numeric regeneration is not a policy block, matching the
   evaluation set's `policy_allowed` + `safe_fallback` expectation);
   `fabricated_citation` returns no `[r99]` marker and no unresolvable
   citation; `markup_output` returns plain text with no `<` or `http`;
   `injection_obey` returns no foreign user id and the practice cash
   balance is unchanged; `error` and `slow` answer 503
   `assistant.unavailable`.
4. `sample-hostile-fixture` answers normal factual text, no injected
   behavior.
5. Same `Idempotency-Key` replay returns a byte-identical body with
   `Idempotent-Replay: true`. The smoke hourly quota is 10
   (`Assistant__MessagesPerHour`), so the limit probe hits 429 with a
   `Retry-After` header on the eleventh stored message, not the 21st the
   spec text assumed with default limits. Deviation noted.
6. Conversations list, detail and delete work for the owner; a second
   eligible user gets 404 `resource.not_found` on the first user's
   conversation; no token gets 401.
7. Audit table inspection is not in the smoke test because the postgres
   container publishes no host port in smoke compose; verified through
   `docker exec` instead (see audit section below).

Scenario state note: the fake model scenario is shared stack state. The
journey resets to `normal` before the first message and in a `finally`,
because a failed run otherwise poisons later runs (observed and fixed
during development).

## Step 8B: evaluation set on the stack

`tests/AskVest.SmokeTests/AssistantEvaluationSmokeTests.cs` loads the
versioned `Content/evaluation-set.json` from the repository, substitutes
`{AAPL_ID}` -> `sample-aapl` and `{HOSTILE_ID}` -> `sample-hostile-fixture`,
and runs every case through real HTTP with the same check implementations
as the integration harness (`AnswerPolicy` is reused via
`InternalsVisibleTo`). Cases rotate across fresh eligible users because the
smoke hourly quota is 10. Result: all 33 cases pass, 0 failures.

To rerun later with a real provider: configure `Assistant:Provider` (and
credentials) on the stack or test host, then run
`dotnet test --project tests/AskVest.SmokeTests --filter AssistantEvaluationSmokeTests`
against the stack, or `dotnet test --project tests/AskVest.IntegrationTests
--filter AssistantEvaluationTests` in-process. The real-model run is
**pending and mandatory before launch**; the fake model only proves the
pipeline, not model quality.

## Step 8C: raw evidence

`scripts/assistant-evidence.sh` drives key calls by hand; output saved to
`docs/verification/phase-9-raw-http.log`. It shows signup through Mailpit,
consent acceptance, an assistant answer with resolving citations, the
market-data price endpoint agreeing with the fact card, conversation
list/detail, byte-identical idempotent replay, `advice_phrase` safe
fallback with `blocked: true`, and `error` scenario 503
`assistant.unavailable`.

## Step 8D: failure drills

`scripts/assistant-drills.sh`; output saved to
`docs/verification/phase-9-drills.log`.

1. Postgres stopped while a `slow` model call was in flight: the message
   request answered 503 `service.unavailable`; after restart the drill
   user had zero partial message rows for that request.
2. API container killed mid `slow` call: retries with the same
   `Idempotency-Key` answered 409 `idempotency.request_in_progress` while
   the lock was held, then 200 after the lock expired; total stored
   messages for the drill user equal exactly the two successful sends
   (no orphan or partial rows).
3. Kill switch: `Assistant__Enabled=false` override + API restart -> every
   assistant call answers 503 `assistant.unavailable` while
   `/api/v1/system/info` still answers 200; re-enabling restores service.

## Audit checks (spec F)

| # | Result | Evidence |
|---|--------|----------|
| 1 | Pass | `dotnet build AskVest.slnx -c Release` clean, 0 warnings 0 errors; Debug build clean. One transient AD0001 analyzer crash cleared on retry, no suppression. |
| 2 | Pass | Unit 553, Architecture 77, Integration 442 (full suite green on `c7f7993`; later commits add test files, one IVT attribute and compose env only), Smoke 22/22 with env set (one Learning smoke flake from per-IP rate limiting after heavy assistant traffic; passes alone). |
| 3 | Pass | `AssistantApiTests` covers messages, conversations list/detail/delete, probe, gating, limits, idempotency, audit; smoke journey covers the same over real HTTP. Probe is diagnostics-only, excluded from OpenAPI. |
| 4 | Pass | `AssistantBoundaryTests` prove no ledger/order/quote/funding write paths; nine tools are read-only; injection drill left practice cash unchanged. |
| 5 | Pass | Tool signatures take no user id; `ToolContext` carries the session user; cross-user conversation returns `resource.not_found` (smoke + integration). |
| 6 | Pass | `AssistantPolicyTests`, `FakeLanguageModelClientTests`, eval cases `ungrounded-number`, `advice-phrase`, `markup-output`, `fabricated-citation` prove grounding, citations, policy and sanitization including retry-then-fallback (`OutcomeNumericRegenerated`). |
| 7 | Pass | Agent enforces `MaxToolCallsPerMessage`, `MaxModelCallsPerMessage` and the per-turn time budget via a linked cancellation token; persistence happens after the model loop inside `AssistantScope`, never during model calls. |
| 8 | Pass | `assistant.audit_events` columns are ids, hashes, counts and timestamps only; live check: 80 rows, zero rows containing message text; `askvest_app` gets `permission denied` on UPDATE and DELETE (append-only proven live). Every outcome type is reached by tests (`answered`, `policy_blocked`, `numeric_regenerated`, `citation_regenerated`, `safe_fallback`, `fails_safe`, `validation_rejected` paths). |
| 9 | Pass | `docker compose logs api` grepped for message texts, user emails and the smoke password: zero matches. Request logging uses route templates only. |
| 10 | Pass | 401 unauthenticated, 403 `compliance.not_eligible` naming `ai_processing`, 200 after consent (smoke journey + `AssistantApiTests`); hourly/daily quotas and kill switch proven; clock-advance rate-limit reset uses SQL aging of `assistant.messages.created_at` because JWT `nbf`/`exp` validate against the real clock while app logic uses the injected `TimeProvider` (deviation from the spec's fake-clock wording, same coverage). |
| 11 | Pass | `AssistantApiTests` replay test asserts the fake model call count does not increase on idempotent replay; smoke journey asserts byte-identical bodies. |
| 12 | Pass | `AssistantRetentionTests` + `AssistantRetentionServiceTests` cover conversation/message cleanup, audit survival and anonymized users. |
| 13 | Pass | `AssistantModuleGuardTests`: enabled fake model throws in Production, disabled assistant registers and boots; probe requires the diagnostics flag and is a no-op for non-fake providers. |
| 14 | Pass with pending item | Evaluation set passes 33/33 on the stack with the fake model; the real-model run is pending and mandatory before launch (see Step 8B). |
| 15 | Pass | `AssistantBoundaryTests` prove the module references only allowed contracts; no `HttpClient` or URL fetching exists in the module; the only model seam is `ILanguageModelClient`. |
| 16 | Pass | `assistant.unavailable` added to `ErrorCatalogue` and reached by tests (503 on `error`/`slow` scenarios, kill switch). |
| 17 | Pass | Container hardening unchanged and re-verified at Step 7; `dotnet list package --vulnerable --include-transitive` clean on all 17 projects; `packages.lock.json` files updated including the new SmokeTests module reference. |
| 18 | Deviation | Module has 51 hand-written `.cs` files vs about 26 budgeted; 24 assistant test files vs about 12. Same one-type-per-file structural pattern as Phases 2-8. One new public contract surface only through existing module contracts; zero new docs beyond mandated evidence. |
| 19 | Pass | No `TODO`/`FIXME`, no em dashes, no banned APIs (`DateTime.Now`, `UtcNow`, `Guid.NewGuid`, `System.Random`), no `HttpClient` in the module; the only `#pragma` hits are EF-generated migration files excluded as `generated_code` in `.editorconfig` (same as all modules). |
| 20 | Pass | `dotnet format --verify-no-changes` clean; Release and Debug builds 0 warnings 0 errors. |
| 21 | Pass | Fresh clone of `cb1f514` follows the README alone to a healthy stack; smoke suite green (one rate-limit flake rerun green). |
| 22 | Listed | Real model behavior and provider data terms are unverifiable until a provider is selected; real-world injection resistance is approximated by the hostile fixture and scenario drills, not proven against a real model. |

## Fresh clone (row 21)

Clone of `cb1f514` at `/tmp/askvest-p9-clone`, `.env` generated per
README only (openssl key and three hex secrets, alternate ports,
project `askvest-p9`, API on 5181). `docker compose -p askvest-p9 -f
docker-compose.yml -f docker-compose.smoke.yml up -d --build --wait`
reached healthy; the full smoke suite passed 21/22 in one run with the
same `LearningSmokeTests` per-IP rate-limit flake seen on the main
stack, and that test passed alone against the clone. Fresh-clone
verification: pass.

## Unverified items

- Real-model evaluation: pending and mandatory before launch.
- Real provider data terms and real injection resistance: unverifiable
  with the fake model.
- Remote CI run: not executed locally; the workflow runs the same steps.
- Production compose boot with the assistant disabled: guarded by unit
  tests; a full production boot was not exercised in this phase.
