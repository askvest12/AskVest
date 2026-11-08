# Phase 7 verification evidence

Learning and Streaks. Verified on .NET SDK 10.0.114, PostgreSQL 17,
Docker Compose smoke stack (`askvest-smoke` project, API on port 5081).

## Step 1: orientation and baseline

Read: `CLAUDE.md`, all files in `docs/design/`, Phase 0 to 6 evidence,
and `AskVest-Backend-Phase-7-Learning-and-Streaks.md` in full.

Baseline on the Phase 6 tip: full suite green (381 unit, 45
architecture, 373 integration, 18 smoke with env set), audit clean.

## Step 2: design docs, compliance plan, module scaffold

- Learning added to system overview, API standards (new error code
  `learning.quiz_required`, HTTP 422), threat model, compliance design,
  roadmap and backlog.
- `docs/design/compliance.md` records the `learning_required` Real
  blocker contract.
- Content guidelines documented: plain English, under five minutes, no
  advice, no promises, chain- and issuer-neutral wording, banned phrase
  lint, required lessons reviewed by compliance before merge.
- Module scaffold `src/Modules/AskVest.Modules.Learning` plus
  `LearningBoundaryTests` proving Learning references only SharedKernel,
  Platform and Identity contracts, and that nothing outside Learning
  references Learning entities except Compliance through Contracts.
- Commit: `docs(learning): design, content guidelines and module scaffold`.

## Step 3: schema, migration, configuration, content loading

- `learning` schema with `lesson_progress`, `quiz_attempts`,
  `learning_days`; own EF history table; forward-only migration;
  `quiz_attempts` is append-only for the application role.
- `LearningOptions` validated at startup: content path, sample content
  switch and path.
- `LearningContentLoader` loads reviewed JSON once into an immutable
  snapshot; `LearningContentValidator` rejects malformed JSON, duplicate
  ids, invalid blocks, invalid quizzes, missing correct options,
  invalid required lessons and banned phrases. Sample content is
  refused in Production.
- `data/learning/content.json` ships empty (production content stays
  empty until real lessons are authored and reviewed);
  `data/learning-sample/content.json` carries five fictional lessons.
- Commit: `feat(learning): schema, options and reviewed content loading`.

## Step 4: tracks and lesson read API

- `GET /api/v1/learning/tracks`, `GET /api/v1/learning/lessons/{id}`,
  `GET /api/v1/learning/progress`.
- Lesson reads carry body blocks and quiz questions with options but
  never `correctOptionId` or `explanation`.
- Commit: `feat(learning): tracks and lesson read api`.

## Step 5: quiz attempts, completion, progress, streaks

- `POST .../quiz-attempts` grades inside one transaction scope: load
  lesson from snapshot, validate one answer per question, grade, insert
  attempt with lesson version and idempotency key, upsert progress
  (attempts count, best score, `attempted` or `completed`), insert
  today's UTC `learning_days` row ignoring unique conflicts.
- `POST .../complete` marks quiz-less lessons; quiz lessons answer 422
  `learning.quiz_required`; completing an already completed lesson is
  harmless.
- Streaks are pure over `learning_days` and the current UTC date:
  learning activity only, never logins, orders or money.
- Replays return the stored first response with no duplicate effects.
- Commit: `feat(learning): quiz attempts, completion, progress and
  streaks`.

## Step 6: compliance `learning_required` gate

- `ILearningRequirements` contract exposes only incomplete required
  lessons at their current versions; Compliance references Learning
  contracts only.
- Real eligibility gains the `learning_required` blocker naming
  incomplete lesson ids; Practice eligibility is unchanged; a lesson
  completed at an old version does not satisfy a newer version; the
  Real probe flips from 403 to 200 once all current required lessons
  are complete.
- Commit: `feat(compliance): require flagged lessons before real money`.

## Step 7: compose, content packaging, README, CI

- API image copies `data/learning` and `data/learning-sample`, removing
  the sample folder unless `INCLUDE_SAMPLE_CONTENT=true`; smoke
  override enables it.
- Compose carries `Learning__ContentPath`, `Learning__LoadSampleContent`
  and `Learning__SampleContentPath`.
- README documents content location, authoring rules, version bumps,
  required-lesson behavior, local validation and the smoke command.
- CI runs Learning content validation alongside catalog validation.
- OpenAPI shows all five routes with the `Idempotency-Key` header on
  both mutating routes.
- Commit: `chore(learning): package content, compose settings, readme
  and ci`.

## Step 8: smoke journey, drills, audit

### A. Smoke journey (`tests/AskVest.SmokeTests/LearningSmokeTests.cs`)

One journey over real HTTP covering all eight spec steps: anonymous
reads answer 401; tracks list lessons and lesson reads contain no
answers or explanations (raw JSON inspected); a wrong attempt fails
with per-question correctness and explanations while progress shows
`attempted`; a correct attempt passes, completes the lesson and opens
a streak of 1 with today counted; replaying the passing key returns
the identical stored response with attempts count unchanged;
`risk-and-diversification` completes through `/complete` while
`what-is-a-stock` answers 422 `learning.quiz_required`; missing and
unknown answers answer 400 `validation.failed`; a second fully
eligible user sees `learning_required` naming `what-you-own`, the Real
probe answers 403, then after completing required lessons the blocker
clears and the probe answers 200; user A's progress is unaffected by
user B; twenty parallel attempts on `fees-and-costs` produce attempt
counts 2 through 21 (one earlier attempt plus twenty). Result: 19/19
smoke tests pass, 0 skipped, on the main stack and once on a fresh
clone.

### B. Raw HTTP evidence

`docs/verification/phase-7-raw-http.log` records anonymous 401s, track
and lesson reads (no `correctOptionId`/`explanation` keys), a failing
attempt with per-question explanations, a passing attempt, progress
with streak 1, quiz-less completion, the 422 quiz-required refusal,
validation failures and eligibility showing `learning_required`.
Secrets redacted.

### C. Failure drills

1. Database stopped during an attempt: `POST
   /api/v1/learning/lessons/what-is-a-stock/quiz-attempts` answered
   HTTP 503 `service.unavailable`; after restart the user had zero
   `quiz_attempts` rows and zero `lesson_progress` rows for that
   lesson. No partial effects.
2. Burst of twelve parallel attempts with the API killed mid-flight:
   after restart, `learning.quiz_attempts` held exactly 12 rows for
   the lesson and `lesson_progress.attempts_count` equalled 12 with a
   single progress row in `attempted` status. Counts match stored
   attempts; nothing double completed.
3. API started with a deliberately broken content file (duplicate
   lesson ids, banned phrases, missing correct option, required lesson
   without quiz, too few options, empty blocks): startup failed with
   all eight problems listed in one `InvalidOperationException`; the
   running API container stayed healthy throughout (health 200),
   matching rolling-restart behavior where the previous container keeps
   serving.

### D. Editor re-check

`dotnet format --verify-no-changes` clean; `dotnet build -c Debug` and
`dotnet build -c Release` both 0 warnings 0 errors; fresh-clone Debug
build inside `dotnet test` also clean. One new `.editorconfig` entry
disables CA2025 for `LearningSmokeTests.cs` with a one-line reason
(intentional overlapping requests in the twenty-way burst), matching
the existing Practice precedent. The only `#pragma warning` occurrences
are EF-generated migration artifacts, excluded as generated code.

### E. Self-audit

| # | Check | Result |
|---|-------|--------|
| 1 | Clean build Debug and Release | Pass: `dotnet build -c Debug` and `-c Release` 0 warnings 0 errors; Docker image build produced no warnings |
| 2 | All test layers green, smoke fully passed | Pass: unit 428, architecture 52, integration 398, smoke 19/19, 0 skipped on main stack and on fresh clone |
| 3 | Every endpoint covered | Pass: all five routes have integration tests (`LearningApiTests`, `LearningAttemptTests`, `LearningEligibilityTests`, `LearningSchemaTests`) and the smoke journey |
| 4 | Content validation rule by rule | Pass: 20 validator tests plus loader tests cover malformed JSON, duplicate ids, invalid blocks and quizzes, missing correct options, invalid required lessons, banned phrases; real and sample folders pass; `Sample_Content_Is_Refused_In_Production` proves the Production guard; drill 3 shows all problems listed at startup |
| 5 | No answers in lesson reads | Pass: raw response inspected in smoke (property names `correctOptionId`/`explanation` absent) and in `phase-7-raw-http.log` |
| 6 | Streak counts learning only; architecture forbids money/practice/trading | Pass: `StreakTests` prove only `learning_days` rows count; `LearningBoundaryTests` forbid the references and were proven by construction when Compliance needed the Contracts seam |
| 7 | Required lessons count only at current version | Pass: `LearningEligibilityTests` cover blocking, clearing, version bumps and the no-required-content case; smoke journey proves the gate end to end |
| 8 | `quiz_attempts` append-only | Pass: `LearningSchemaTests` asserts UPDATE and DELETE fail for the application role with SQLState `42501` |
| 9 | Concurrency tests green, rerun three times | Pass: `Twenty_Parallel_Attempts_Record_Exactly`, `Twenty_Parallel_Completes_Create_One_Day_Row` and the attempt race tests ran three consecutive times, all green |
| 10 | Logs inspected | Pass: api logs show EF commands with masked parameters; `LearningCommandService` logs lesson id, score and passed only; no answers, emails or personal data in Learning log lines |
| 11 | Error codes and docs in sync | Pass: `learning.quiz_required` is in `ErrorCatalogue`, `docs/design/01-api-standards.md` and reached by smoke (422 on `/complete`) and integration tests; `validation.failed` reached for missing/unknown answers |
| 12 | Hardening, deps, lock files | Pass: `dotnet list package --vulnerable --include-transitive` clean for all 15 projects; no new packages; lock files committed per module |
| 13 | Budget | Partial deviation: Learning module has 38 hand-written + 3 migration `.cs` files vs about 16; new test files 13 vs about 8 (4 unit, 1 architecture, 6 integration incl. helpers, 2 smoke). Same one-type-per-file structural pattern as Phases 2-6; only new contract is `ILearningRequirements` |
| 14 | grep audit | Pass: no `TODO`/`FIXME`, no banned clock/id APIs, no suppressions outside `.editorconfig`, no em dashes in new files (pre-existing em dashes only in phase-0/1 evidence docs) |
| 15 | Editor parity | Pass: format verify clean, zero analyzer warnings at build in both configurations |
| 16 | Content neutrality | Pass: manual read of `data/learning-sample/content.json` recorded in section A; no chain names, provider or issuer names, advice or promises; the file itself states sample lessons are replaced by reviewed real content |
| 17 | Fresh clone README-only | Pass: cloned to `/tmp/askvest-p7-check`, followed README (`.env` from example plus generated secrets on alternate ports and `-p askvest-p7` because compose project names are fixed), stack healthy, smoke 19/19 with 0 skipped, then `down -v` |
| 18 | Unverifiable items | Listed below |

## Unverifiable items

- Real lesson content quality and compliance review: production content
  is intentionally empty; real lessons must be authored and reviewed
  before launch.
- Streak behavior across real days: streak math is unit-tested and the
  UTC day row is proven, but multi-day streaks were not observed over
  real time.
- Remote CI not observed.

## Commits

- `20b8d8f` docs(learning): design, content guidelines and module scaffold
- `bd0fb7a` feat(learning): schema, options and reviewed content loading
- `d09c441` feat(learning): tracks and lesson read api
- `9e628c5` feat(learning): quiz attempts, completion, progress and streaks
- `26db8f1` feat(compliance): require flagged lessons before real money
- `7c7758a` chore(learning): package content, compose settings, readme and ci
- `ef3d1ff` test(learning): end-to-end smoke journey over real http
- this commit: docs(learning): phase 7 verification evidence

## Deviations

- File and test budgets exceeded for the same structural reasons as
  Phases 2-6 (one type per file, EF configurations, shared test
  fixtures).
- `IdentitySmokeTests` was missing the `SmokeStack` collection and ran
  parallel to the serialized suite, tripping per-IP strict rate limits
  once the Learning journey added auth traffic; added the collection
  attribute. `SendJsonWithStatusAsync` in `ComplianceSmokeTests` gained
  the same one-shot 429 retry the other helpers already had.
- Smoke helper `SmokeLearning` reads correct answers from the reviewed
  sample content file because the API correctly never exposes them.

## Stack

`docker compose -f docker-compose.yml -f docker-compose.smoke.yml up
-d --build --wait` then
`ASKVEST_SMOKE_BASE_URL=http://localhost:5081
ASKVEST_SMOKE_MAILPIT_URL=http://localhost:18025
ASKVEST_COMPLIANCE_KYC_WEBHOOK_SECRET=<from .env>
dotnet test --project tests/AskVest.SmokeTests -c Release`.
Stack left up and healthy.
