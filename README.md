# AskVest Backend

Phase 1: hardened .NET 10 backend with PostgreSQL, EF Core migrations, response envelope, idempotency, OpenAPI/Scalar, Serilog, strict JSON rules, containerized deployment, and the Identity module (sign-up, verification, login, sessions, refresh rotation, devices, password and PIN management, step-up authorization, security history, phone verification, and account deletion with anonymization).

## Prerequisites

- .NET SDK 10.0.x (see `global.json`)
- Docker with Compose v2
- Git
- OpenSSL (for generating development secrets)

## Setup

```bash
git clone git@github.com:askvest12/AskVest.git
cd AskVest
dotnet restore AskVest.slnx --locked-mode
cp .env.example .env
```

Fill in `.env`. The database passwords can be any strong value; the Identity
secrets must be generated once for development:

```bash
# ES256 signing key pair material (PKCS8 private key, base64)
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -outform DER | base64 -w0

# Password pepper and one-time-code hash secret (64 hex chars each)
openssl rand -hex 32
openssl rand -hex 32

# KYC webhook signing secret (64 hex chars)
openssl rand -hex 32
```

Set the results in `.env`:

- `ASKVEST_IDENTITY_SIGNING_KEY` - the base64 PKCS8 key from the first command
- `ASKVEST_IDENTITY_PASSWORD_PEPPER` - first `openssl rand -hex 32` output
- `ASKVEST_IDENTITY_CODE_SECRET` - second `openssl rand -hex 32` output
- `ASKVEST_IDENTITY_SIGNING_KEY_PREVIOUS` - leave empty unless rotating keys
- `ASKVEST_COMPLIANCE_KYC_WEBHOOK_SECRET` - the third `openssl rand -hex 32` output

Never commit real secret values; `.env` is git-ignored and `.env.example`
contains names only.

## Run locally

Production-like stack (real SMTP required via `ASKVEST_SMTP_*`):

```bash
docker compose up -d --build --wait
```

Development stack with Mailpit (captures all outgoing email locally):

```bash
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build --wait
```

- API: `http://localhost:5080` (`/health/live`, `/scalar/v1` for docs)
- Mailpit UI: `http://localhost:18025` - read verification codes here
- Mailpit SMTP: `localhost:11025`

The migrator runs automatically before the API starts.

## Identity smoke flows

1. `POST /api/v1/auth/sign-up` with `{ "email": "you@example.com", "password": "..." }` and an `Idempotency-Key` header.
2. Open Mailpit at `http://localhost:18025`, copy the 6-digit code from the verification email.
3. `POST /api/v1/auth/verify-email` with `{ "email": ..., "code": ... }`.
4. `POST /api/v1/auth/login` with `{ "email": ..., "password": ..., "device": { "installationId": "...", "platform": "android", "deviceName": "...", "appVersion": "1.0.0" } }` - the first device is trusted automatically; later devices get an email challenge via `POST /api/v1/auth/verify-device`.
5. Authenticated calls use `Authorization: Bearer <accessToken>`; rotate with `POST /api/v1/auth/refresh`.

## Test

```bash
./scripts/verify.sh                 # restore, build, all tests, vulnerability audit
dotnet test --project tests/AskVest.UnitTests
dotnet test --project tests/AskVest.IntegrationTests   # requires Docker (Testcontainers)
```

Smoke tests against the running stack:

```bash
ASKVEST_SMOKE_BASE_URL=http://localhost:5081 dotnet test --project tests/AskVest.SmokeTests
```

The smoke stack (`docker-compose.smoke.yml`) runs the API in Development mode
with Mailpit and probe endpoints enabled. It also enables real money for the
`NG` test country with the fake KYC and screening providers, so the full
Practice and Real eligibility journey can run over HTTP.

## Compliance smoke flows

The smoke suite walks a verified user through documents, consents, country,
KYC start and a signed provider webhook, then checks the gated
`/api/v1/practice` and `/api/v1/real` probe endpoints.

KYC webhooks are received at `POST /api/v1/webhooks/kyc/{provider}` and are
signed with HMAC-SHA256 over `{timestamp}.{rawBody}`. To call it by hand:

```bash
BODY='{"eventId":"evt-1","providerReference":"<ref>","outcome":"approved","verifiedCountry":"NG","fullName":"Test User","dateOfBirth":"1990-01-01"}'
TS=$(date +%s)
SIG=$(printf '%s' "$TS.$BODY" | openssl dgst -sha256 -hmac "$ASKVEST_COMPLIANCE_KYC_WEBHOOK_SECRET" | awk '{print $2}')
curl -X POST http://localhost:5081/api/v1/webhooks/kyc/fake \
  -H "X-Webhook-Timestamp: $TS" -H "X-Webhook-Signature: $SIG" \
  -H 'Content-Type: application/json' -d "$BODY"
```

The `providerReference` comes from `POST /api/v1/compliance/kyc/start`. A name
containing `SMOKE-SANCTIONED` triggers the fake screening hit path.

## Ledger smoke flows

With the smoke stack running (`docker compose -f docker-compose.yml -f
docker-compose.smoke.yml up -d --build --wait`), the ledger journey runs over
real HTTP:

```bash
ASKVEST_SMOKE_BASE_URL=http://localhost:5081 dotnet test --project tests/AskVest.SmokeTests
```

It signs up a practice-eligible user, posts diagnostic credits and debits via
`POST /api/v1/practice/diagnostics/ledger-post` (header `Idempotency-Key`,
body `{"kind":"credit","asset":"USD","amount":"100"}`), reads
`GET /api/v1/practice/balances` and `GET /api/v1/practice/ledger/entries`,
proves replay and the `ledger.insufficient_funds` envelope, then walks a
second user through KYC approval to prove real-book posting stays separate.

The API logs one reconciliation line per cycle:

- `Ledger reconciliation completed with zero mismatches.` - healthy.
- `Ledger reconciliation mismatch for {Target} with delta {Delta}` at error
  level - one account's stored balance differs from the sum of its entries.
  The job never auto-corrects; investigate the journal history for that
  account.
- `Ledger reconciliation non-zero net for book {Book} asset {Asset}` at error
  level - a book and asset no longer nets to zero, meaning a journal was
  written outside the ledger service or rows were tampered with.
- `Ledger reconciliation cycle failed.` at error level - the job could not
  reach the database; it retries on the next interval
  (`Ledger__ReconciliationIntervalMinutes`, default 60).

## Catalog data

The instrument catalog (chains, issuers, instruments, tokens and their
disclosures) lives in `data/catalog/` as three reviewed JSON files:
`chains.json`, `issuers.json`, `instruments.json`. The Migrator imports them
idempotently every time it runs, inside the same transaction as the Ledger
asset registration for each token. The API only reads the catalog; the
application database role has SELECT-only access to the `catalog` schema.

**Production data is intentionally empty.** No real issuer, chain or token
has been approved yet, so `data/catalog/` ships with empty lists. Do not add
real entries until the provider and chain research in
`docs/design/06-chain-and-provider-research.md` is complete.

Fictional sample data for development and smoke tests lives in
`data/catalog-sample/` (two EVM testnets, fictional issuers, every token
status covered). It is only loaded when `Catalog__LoadSampleData=true` and
the environment is not Production; the importer refuses it in Production and
the production Migrator image does not even contain the sample files.

### Validate catalog data locally

The unit tests parse and validate both folders on every run:

```bash
dotnet test --project tests/AskVest.UnitTests --filter "FullyQualifiedName~Catalog"
```

To check the files against the database rules (listing guard, transitions,
immutability) without writing anything, run the Migrator's validate-only path
through the integration suite, or import into a scratch database with
`Catalog__DataPath` pointed at the folder.

### Add or change a token

1. Edit the JSON in `data/catalog/` (or `data/catalog-sample/` for fictional
   data). Tokens sit inside their instrument's `tokens` array.
2. Keep `version` bumped and every mandatory disclosure field populated.
   A token may only enter `listed` when the listing guard passes: approved
   issuer, enabled chain with a supported family, complete disclosures, at
   least one allowed country, gateway key, decimals at most 18, current
   review dates, active instrument.
3. Run the catalog unit tests above, then restart the stack so the Migrator
   re-imports. Reimports are idempotent; removing a token from the files
   delists it (rows are never deleted, and `delisted` is terminal).
4. Never put the gateway key or reviewer label anywhere the API can reach;
   both are stored but never serialized.

### Catalog smoke test

With the smoke stack running (it loads the sample data automatically):

```bash
docker compose -f docker-compose.yml -f docker-compose.smoke.yml up -d --build --wait
ASKVEST_SMOKE_BASE_URL=http://localhost:5081 dotnet test --project tests/AskVest.SmokeTests
```

The catalog journey browses instruments, searches and pages, opens token
disclosures, and checks buy/sell availability blockers per user country.

## Market data

The MarketData module keeps a latest price per active catalog instrument and
serves read-only prices and candles. It is off by default
(`MarketData__Enabled=false` in `docker-compose.yml`) because the only
provider shipped so far is the deterministic fake, which is forbidden when
`ASPNETCORE_ENVIRONMENT=Production` - startup fails fast if it is selected
there. The smoke stack enables it with the fake provider.

### How prices and labels work

A hosted poller (`PricePoller`) asks the configured `IMarketDataProvider` for
quotes in batches every `MarketData__PollIntervalSeconds`, validates them
(positive price, quote currency, sane timestamp, digit limit), guards against
outlier jumps, and upserts `marketdata.latest_prices`. A single PostgreSQL
advisory lock makes sure only one API instance polls per cycle, and a
per-cycle request budget derived from `MarketData__RequestsPerMinute` keeps
the provider under its rate limit. Bad ticks never overwrite a trusted price:
they are stored as suspects and only confirmed by a repeated tick.

`GET /api/v1/marketdata/prices?instrumentIds=a,b,c` reads only the stored
rows - it never calls the provider - and returns one item per requested id in
request order. Each item carries the price as a string, currency, `asOf`,
`ageSeconds`, session, a freshness `label`, `usableForExecution`, the source
display name and optional `previousClose`. Unknown, delisted or unpriced ids
answer `unavailable` instead of failing the batch.

Freshness is computed at read time. Effective age is
`now - asOf - providerDelay` (never below zero) and the first matching rule
wins: no price or unmapped instrument `unavailable`, session `halted`
`halted`, session `closed` `closed`, effective age above
`MarketData__StaleThresholdSeconds` `stale`, real-time feed within
`MarketData__LiveThresholdSeconds` `live`, otherwise `delayed`.
`usableForExecution` is true only for `live` or `delayed` in a `regular`
session; Phase 10 order placement must refuse everything else.

`GET /api/v1/marketdata/instruments/{id}/candles?interval=&from=&to=` serves
ascending OHLCV candles (decimals as strings) with `adjusted` and a `partial`
marker on the still-forming last candle. Closed candles are cached
permanently in `marketdata.candles`; the open candle refreshes only after its
interval's `OpenCacheSeconds`. Identical concurrent requests share one
provider call through a single-flight gate. On provider failure the endpoint
serves cached rows with `stale=true`, or answers 503 `marketdata.unavailable`
when nothing is cached.

### Market data smoke test

The smoke stack runs the fake provider in `regular` session with a 5 second
poll interval, 5 second live and 15 second stale thresholds, and the
diagnostics probe enabled. The smoke journey fetches sample prices, drives
the fake through delayed, halted, closed, outlier, outage and unknown-symbol
scenarios, and exercises the candle cache and error paths over real HTTP:

```bash
docker compose -f docker-compose.yml -f docker-compose.smoke.yml up -d --build --wait
ASKVEST_SMOKE_BASE_URL=http://localhost:5081 dotnet test --project tests/AskVest.SmokeTests
```

### Adding a real provider later

Implement `IMarketDataProvider` (`ProviderKey`, `MarketDataCapabilities`,
`GetLatestQuotesAsync`, `GetCandlesAsync`) in a new adapter inside the module,
register it in `ProviderRegistry`, and set `MarketData__Provider` plus its
credentials through the secret store. The research inputs and licensing
questions live in `docs/design/06-provider-abstractions.md`; quotes must
arrive as decimal text, timestamps in UTC, and prices already expressed in
the instrument's quote currency (sub-unit amounts are normalized by the
adapter, not the module).

## Practice mode

The Practice module is a simulated brokerage for eligible users. It issues a
one-time grant of `Practice__StartingCredits` (default 10000 USD) into the
real double-entry ledger's Practice book and trades only practice assets,
whose codes are the catalog symbol prefixed with `P.` (the Catalog importer
refuses `P.` codes on real tokens). Every balance and order lives in the
`practice` schema; practice value can never reach the real book.

Orders are placed under `/api/v1/practice` behind the `PracticeEligible`
policy and every state-changing call requires an `Idempotency-Key`. A market
buy takes a USD amount, a market sell takes a quantity or the sell-all flag,
and both fill immediately at the latest market price that is usable for
execution (`usableForExecution` from the MarketData freshness labels).
`stop_loss` orders are sell-only: creation moves the shares from available to
held in the ledger, the hosted evaluator triggers them when the latest usable
price falls to or below the stop price, and the fill uses the current price so
gaps and slippage stay visible. Stale, closed, halted or unavailable prices
never trigger. Cancelling a stop reverses the hold. Open stops are capped by
`Practice__MaxOpenStopOrders`.

Reset returns the account to a clean slate: open stops are cancelled and
their holds released, every practice balance is swept back, starting credits
are reissued and reset history is kept. Resets are rate limited by
`Practice__ResetCooldownHours` and `Practice__MaxResetsPer30Days`; a blocked
reset answers `practice.reset_not_allowed`.

All tunables are `Practice__*` environment variables (see
`docker-compose.yml`). The smoke override sets a 50 basis point fee, a 2
second stop evaluation interval and a reset cooldown of 0.001 hours (about
4 seconds) so the whole journey is observable quickly.

### Practice smoke test

The practice journey runs inside the smoke suite against the smoke stack
(sample catalog, fake market data). It covers account start idempotency,
buy/sell/sell-all and history, order replay, stop hold/cancel/trigger,
market-unavailable and limit errors, a 20-way parallel buy burst, reset and
cross-user isolation:

```bash
docker compose -f docker-compose.yml -f docker-compose.smoke.yml up -d --build --wait
ASKVEST_SMOKE_BASE_URL=http://localhost:5081 dotnet test --project tests/AskVest.SmokeTests
```

## Learning and streaks

The Learning module serves reviewed, file-based lessons under
`/api/v1/learning` (tracks, lesson bodies, graded quizzes, progress and
streaks). Content is data, not database rows: `data/learning/content.json`
holds ordered tracks, each with ordered lessons containing body blocks and
an optional quiz. The API loads and validates the file once at startup into
an immutable snapshot; a malformed file or a banned phrase stops startup and
lists every problem. Read responses never include correct options or
explanations; only graded quiz attempts do. Streaks count consecutive UTC
days with learning activity only - never logins, orders or money movement.

**Production content is intentionally empty** (`{"tracks":[]}`) until real
lessons are authored and reviewed. Fictional sample content (two tracks,
five lessons, quizzes and one `requiredForReal` lesson) lives in
`data/learning-sample/` and loads only when
`Learning__LoadSampleContent=true` outside Production; the production API
image does not even contain the sample files.

Lessons flagged `requiredForReal` gate Real eligibility: while any is
incomplete at its current version, Real answers `compliance.not_eligible`
with a `learning_required` blocker naming the lesson ids. Practice
eligibility is unaffected.

### Add or change a lesson

1. Edit `data/learning/content.json` (or `data/learning-sample/content.json`
   for fictional content). Each lesson needs a unique slug `id`, title,
   summary, `estimatedMinutes`, a `version`, ordered `blocks`
   (`heading`, `paragraph`, `bullets`, `callout`) and an optional `quiz`
   with a `passMarkPercent` and questions carrying two to five options,
   exactly one `correctOptionId` and an `explanation`.
2. Write plainly: under five minutes of reading, no advice, no promises, and
   no chain, provider or issuer names. The startup validator rejects banned
   phrases such as "guaranteed", "risk-free", "cannot lose" and "sure
   profit". Content guidelines live in `docs/design/compliance.md`.
3. Bump `version` whenever the meaning of a `requiredForReal` lesson
   changes: users who completed an older version must pass it again before
   the `learning_required` blocker clears.
4. Validate locally before committing:

```bash
dotnet test --project tests/AskVest.UnitTests --filter "FullyQualifiedName~Learning"
```

### Learning smoke test

The smoke stack loads the sample content (`Learning__LoadSampleContent=true`
in `docker-compose.smoke.yml`, sample files baked into the image via the
`INCLUDE_SAMPLE_CONTENT` build arg). The learning journey covers anonymous
401s, track and lesson reads without answers, quiz grading and replay,
invalid answers, completion rules, progress, streaks, ownership isolation
and the `learning_required` Real-eligibility gate:

```bash
docker compose -f docker-compose.yml -f docker-compose.smoke.yml up -d --build --wait
ASKVEST_SMOKE_BASE_URL=http://localhost:5081 dotnet test --project tests/AskVest.SmokeTests
```

## Portfolio and watchlist

`GET /api/v1/practice/portfolio` and `GET /api/v1/real/portfolio` return the
caller's portfolio for that book. The portfolio stores no money facts: on
every request it replays the user's ledger movements (net per journal and
asset, in posted order) through a weighted-average-cost engine, so holdings
always equal ledger balances. Practice assets (`P.` prefix) resolve to
instruments through the Practice contract; real token assets resolve through
the Catalog contract, and one instrument aggregates across tokens and chains
with a per-token breakdown carrying the opaque chain id.

The response carries cash lines (fiat and stablecoin assets; only assets in
`Portfolio__CashEquivalentAssets`, USD and USDC by default, count toward the
total at one to one), holdings (quantity, cost basis, average cost, latest
price with its market data label and session, market value, unrealized and
realized profit, income, flags, token breakdown) and totals. Only display
figures are rounded, half up, to `Portfolio__DisplayDecimals` (2); quantities
and prices are exact. A replay is capped at `Portfolio__MaxJournalsPerReplay`
(20000 journals); beyond that the endpoint answers `service.unavailable`,
which is the signal to materialize positions later.

Flags mean: `has_unclassified_movements` (a position asset moved by a
non-trade reason such as a token deposit, so its cost basis is unknown),
`has_inconsistency` (replay tried to sell more than is held, or replay
quantity disagrees with the ledger balance), `price_unavailable` (no usable
latest price), `valuation_unsupported_currency` (instrument not quoted in
USD; value and unrealized profit are null until FX exists). Stale, halted
and closed prices still value the holding but carry their label. When any
holding cannot be valued, totals carry `partial: true`.

`GET /api/v1/watchlist` lists the caller's instruments in added order with
catalog fields and the latest labelled price (delisted or unpriced entries
show label `unavailable`). `PUT /api/v1/watchlist/{instrumentId}` adds and
`DELETE /api/v1/watchlist/{instrumentId}` removes; both are naturally
idempotent set operations and need no `Idempotency-Key`. Adds require an
active instrument (`resource.not_found` otherwise) and are capped at
`Portfolio__WatchlistMaximum` (50) enforced under a per-user lock
(`portfolio.watchlist_full`, 422).

### Portfolio smoke test

The smoke journey buys a sample stock at two prices, sells part, checks the
weighted average cost and realized profit, places a stop, resets the
practice account and confirms the portfolio empties, exercises the
watchlist including the halted-price label change, and verifies real-book
two-chain aggregation plus the eligibility gates:

```bash
docker compose -f docker-compose.yml -f docker-compose.smoke.yml up -d --build --wait
ASKVEST_SMOKE_BASE_URL=http://localhost:5081 dotnet test --project tests/AskVest.SmokeTests
```

## AI research assistant

`POST /api/v1/assistant/messages` answers research and education questions
about the catalog, latest prices, price history, the caller's own practice
and real portfolio, their watchlist and learning lessons. It can explain and
cite; it cannot give advice, predict prices, promise returns or place,
modify or cancel orders, move money or reveal its instructions. Every
answer carries server-built citations, fact cards, an optional action
proposal (for example `open_instrument`, never an execution) and a
disclosure line. Numeric claims are checked against the trusted tool
results that produced them; fabricated citations, ungrounded numbers,
markup and instruction-override attempts are rejected or rewritten to a
safe fallback, and every run writes a content-free audit event.

The assistant requires an active account with practice eligibility and an
accepted `ai_processing` consent document; without it requests answer
`compliance.not_eligible` or `auth.forbidden` like any other gated
endpoint. `Assistant__Enabled=false` is the kill switch: every call then
answers `assistant.unavailable` while the rest of the API is unaffected.
Messages are limited to `Assistant__MaxMessageCharacters` (2000),
`Assistant__MessagesPerHour` (20) and `Assistant__MessagesPerDay` (100) per
user, with `Assistant__MessageTimeBudgetSeconds` bounding each turn.
Conversations and messages are deleted after
`Assistant__ConversationRetentionDays` (90); audit events are kept.

The only model adapter shipped is the deterministic fake
(`Assistant__Provider=fake`), which is refused at startup in Production.
The real provider, its secret and its evaluation are a launch gate: the
versioned evaluation set in
`src/Modules/AskVest.Modules.Assistant/Content/evaluation-set.json` must
pass against the real model before the assistant may be enabled in
production. The set is provider agnostic; `{AAPL_ID}` and `{HOSTILE_ID}`
placeholders are substituted by the integration harness.

### Assistant smoke test

The smoke override enables the fake model, turns the diagnostics probes on
and lowers the hourly limit so bounding is cheap to observe:

```bash
docker compose -f docker-compose.yml -f docker-compose.smoke.yml up -d --build --wait
ASKVEST_SMOKE_BASE_URL=http://localhost:5081 dotnet test --project tests/AskVest.SmokeTests --filter "FullyQualifiedName~Assistant"
```

`POST /api/v1/diagnostics/assistant-scenario` (probe flag only, never in
Production, a no-op for non-fake models) switches the fake between
`normal`, `error`, `injection_obey`, `fabricated_citation`,
`ungrounded_number`, `advice_phrase`, `markup_output`, `malformed_output`,
`loop_tools` and `slow`.

## Real trading

The Trading module runs the real order engine behind `/api/v1/real` (the
`RealEligible` policy applies to every route). The flow is:

1. `POST /api/v1/real/quotes` asks the configured issuer gateway for a firm
   quote. The response shows every term the user will get - side, token,
   quantity, price, gross, our fee, gateway fee, total, settlement asset,
   expiry and a `termsHash` - and the quote is stored with a deterministic
   hash of those terms.
2. `POST /api/v1/auth/step-up` issues a short lived grant for
   `authorize_order` bound to that `termsHash` and the user's PIN.
3. `POST /api/v1/real/orders` (idempotency key required) rechecks
   eligibility, catalog availability and limits, consumes the grant, holds
   the cash or tokens in the real ledger and enqueues gateway submission in
   the same transaction. One quote produces at most one order; replaying
   the same key returns the same order.
4. An outbox worker submits to the gateway using the order id as the
   gateway idempotency reference, and a progress job polls fills. Order
   states are `pending_submission`, `submitting`, `submitted`,
   `executed`, `failed`, `cancelled` and `needs_reconciliation`.

A fill settles only when quantity, price, gross, gateway fee and the
gateway side total match the quote exactly; anything else goes to
`needs_reconciliation` with the hold preserved for operator review.
Definitive rejections fail the order and release the hold. While an order
is non-final its held funds stay held and it counts toward the open order
cap. `POST /api/v1/real/orders/{id}/cancel` cancels a
`pending_submission` order and releases the hold.

No real issuer gateway ships yet: routes are configured under
`Trading__Gateway__Routes__<gatewayKey>` and only the deterministic fake
(`Provider=fake`, refused in Production) exists. The fake gateway scenarios
(`normal`, `quote_error`, `rejected`, `slow_submit`, `submit_timeout_lost`,
`submit_timeout_filled`, `delayed_fill`, `fill_mismatch`,
`status_unavailable`) are switchable through
`POST /api/v1/diagnostics/trading-scenario` when probes are enabled.

### Real trading smoke test

The smoke override wires both sample gateway keys to the fake provider,
shrinks job intervals and limits, and enables the probes:

```bash
docker compose -f docker-compose.yml -f docker-compose.smoke.yml up -d --build --wait
ASKVEST_SMOKE_BASE_URL=http://localhost:5081 dotnet test --project tests/AskVest.SmokeTests --filter "FullyQualifiedName~Trading"
```

The journey signs up a user, completes documents, consents, country, KYC
approval and the learning gate, funds the ledger through the diagnostics
post, takes a quote, binds a step-up grant to the terms hash, places the
order, watches it execute, verifies the balances, sells back, and drills
the rejected and mismatched fill scenarios.

## Wallets and custody

The Wallets module runs deposits and withdrawals behind `/api/v1/real`
(the `RealEligible` policy applies to every route). Custody and address
screening sit behind provider interfaces; only the deterministic fakes
exist in this phase and both are refused in Production.

Deposits:

1. `GET /api/v1/real/wallets/{chainId}` returns the user's deposit
   instruction for a chain, creating it through the custody provider on
   first use. Chains without a custody route fail closed.
2. A background job polls each routed chain for deposits, deduplicates by
   provider deposit id, resolves the destination wallet address and the
   token contract, screens the sender address and credits the real ledger
   exactly once. `GET /api/v1/real/deposits` lists the feed with states
   `pending`, `credited`, `quarantined` and `ignored`.

Withdrawals:

1. `POST /api/v1/real/withdrawal-addresses` adds a destination after a
   `change_withdrawal_address` step-up grant bound to the canonical
   address. Screening hits, internal addresses, own deposit addresses and
   cooling-off addresses are refused.
2. `POST /api/v1/real/withdrawals/previews` prices a withdrawal: gross
   quantity, flat fee, net quantity, USD value limits, expiry and a
   `termsHash` over every term.
3. `POST /api/v1/real/withdrawals` consumes an `authorize_withdrawal`
   grant bound to that `termsHash`, holds the gross quantity in the real
   ledger and enqueues custody submission in the same transaction. The
   platform withdrawal id is the provider idempotency reference.
4. A submit worker calls the custody provider outside any database
   transaction; a progress job polls status and settles or releases the
   hold. States are `previewed`, `requested`, `submitting`, `submitted`,
   `completed`, `failed`, `cancelled` and `needs_reconciliation`.
   Ambiguous provider outcomes never guess: they keep the hold and move
   to `needs_reconciliation` for operator review.

Rolling 24 hour limits, per-withdrawal and per-user caps, the address
book maximum and preview expiry all apply at placement. A reserves job
compares custody holdings against ledger clearing balances per chain and
token; consecutive mismatches pause withdrawals for that scope until an
operator resumes.

The fake custody scenarios (`normal`, `rejected`, `submit_timeout_lost`,
`submit_timeout_filled`, `delayed`, `status_unavailable`,
`holdings_mismatch`) and the fake screening scenario (`normal`, `error`)
are switchable through `POST /api/v1/diagnostics/wallets-custody-scenario`
and `POST /api/v1/diagnostics/wallets-screening-scenario` when probes are
enabled. `POST /api/v1/diagnostics/wallets-deposit` injects a deposit for
testing.

### Wallets smoke test

The smoke override routes both sample chains to the fake custody provider
and shrinks every job interval:

```bash
docker compose -f docker-compose.yml -f docker-compose.smoke.yml up -d --build --wait
ASKVEST_SMOKE_BASE_URL=http://localhost:5081 dotnet test --project tests/AskVest.SmokeTests --filter "FullyQualifiedName~Wallets"
```

The journey signs up a real-eligible user, fetches the deposit
instruction, injects a deposit through the probe, watches it credit,
adds a withdrawal address behind a step-up grant, previews and places a
withdrawal bound to the terms hash, replays the placement idempotently,
verifies cross-user isolation and watches the withdrawal complete with
the hold released. A second test drills the rejected custody scenario
and confirms the hold returns to the user.

## Rotating signing keys

Access tokens are signed with ES256. To rotate:

1. Generate a new key pair as above.
2. Move the current `ASKVEST_IDENTITY_SIGNING_KEY` value into
   `ASKVEST_IDENTITY_SIGNING_KEY_PREVIOUS` and put the new key in
   `ASKVEST_IDENTITY_SIGNING_KEY`.
3. Restart the API. Tokens signed by the previous key keep validating until
   they expire (10 minutes); issue a fresh pair after the swap.

To change the password pepper or code secret, bump
`Identity__Secrets__PasswordPepperVersion` / regenerate the secret and expect
existing password hashes and one-time codes to be invalidated - users must
reset their password. Plan this as a maintenance event.

## Layout

- `src/AskVest.SharedKernel` - Result/Error primitives, paging, endpoint markers
- `src/AskVest.Platform` - EF Core context, migrations, readiness, idempotency storage
- `src/AskVest.Modules.Identity` - identity module: auth flows, sessions, devices, PIN, step-up, phone, deletion, messaging
- `src/AskVest.Modules.Compliance` - compliance module: documents, consents, country, eligibility, KYC webhook, screening
- `src/AskVest.Modules.Ledger` - ledger module: double-entry posting, balances, history, probes, reconciliation
- `src/AskVest.Modules.Catalog` - catalog module: chains, issuers, instruments, tokens, disclosures, availability, read API
- `src/AskVest.Modules.MarketData` - market data module: provider seam, fake provider, price poller, freshness labels, prices and candles API
- `src/AskVest.Modules.Practice` - practice module: virtual account, market and stop-loss orders, reset, positions, order history
- `src/AskVest.Api` - HTTP host: envelope, middleware, health, OpenAPI
- `src/AskVest.Migrator` - forward-only migration runner
- `tests/` - unit, architecture, integration (Testcontainers), smoke
- `docs/` - design docs and ADRs
- `docker/` - images and postgres init

## Troubleshooting

- Port conflicts: set `ASKVEST_API_PORT` / `ASKVEST_POSTGRES_PORT` in `.env`.
- `ConnectionStrings:Platform is required`: set the connection string env var or appsettings.
- Containers unhealthy: `docker compose logs api postgres migrator`.
- Emails not arriving in dev: check `http://localhost:18025` and `docker compose logs api | grep -i smtp`.

### Editor shows red errors or warnings that the build does not

The command-line build is the source of truth (`dotnet build` must show 0
warnings). If VS Code disagrees:

1. Install the recommended extensions: `ms-dotnettools.csdevkit` and
   `ms-dotnettools.csharp` (see `.vscode/extensions.json`).
2. Reload the window: `Ctrl+Shift+P` then "Developer: Reload Window".
3. Restart the C# language server: `Ctrl+Shift+P` then "C#: Restart Language
   Server".
4. Confirm the editor uses the pinned SDK: `.vscode/settings.json` points at
   `AskVest.slnx`; `global.json` pins SDK `10.0.114` with `latestFeature`
   roll-forward, so any 10.0.1xx SDK works.
5. Delete stale build output and restore:
   `find . -type d \( -name bin -o -name obj \) -exec rm -rf {} +` then
   `dotnet restore AskVest.slnx --locked-mode`.
6. If red lines persist, run `dotnet build` and `dotnet format AskVest.slnx
   --verify-no-changes`; both must be clean. `.editorconfig` is the shared
   rule set; rules that the compiler cannot enforce are set to suggestion so
   they never show as errors.
