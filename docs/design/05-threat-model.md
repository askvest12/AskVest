# 05. Threat Model

## Assets

- Customer funds (Practice credits and Real balances)
- Personal data (identity, KYC documents, contact details)
- Signing keys (custody, withdrawals)
- Auth tokens and session material
- Ledger integrity (journal correctness, immutability)

## Trust boundaries

1. Mobile client to API (untrusted input, replayable requests)
2. API to PostgreSQL (least-privilege roles limit blast radius)
3. Provider webhooks into the API (forged or replayed callbacks)
4. API to external providers (LLM, KYC, market data, issuers, custody)
5. Admin/back-office users to sensitive actions

## Threats

| Id | Threat | Mitigation | Phase |
|---|---|---|---|
| T1 | Account takeover (credential stuffing, OTP interception) | Argon2id+pepper, throttling buckets, device verification, step-up grants, alert emails | 1 |
| T2 | Replay or duplicate money movement | idempotency middleware plus unique key per operation; journals carry a unique idempotency key plus content hash, same key and content replays, different content conflicts; inserts tolerate conflicts without aborting the transaction; funding deposits dedupe on provider key plus provider deposit id, funding webhooks dedupe on provider event id, payouts are idempotent by our withdrawal id | 0, 3, 10, 12 |
| T3 | Race condition on balance (double spend) | Read Committed plus deterministic row locks on balance rows sorted by account id; user-kind balances forbidden negative by check constraint; deadlock or lock timeout surfaces `service.unavailable` and the client retries with its key | 3 |
| T4 | Object-level authorization bypass (IDOR) | ownership in every query, not-found answers, cross-user tests per endpoint | 1 onward |
| T5 | Quote tampering or stale quote | server-side signed quotes, expiry, never trust client amounts; Phase 10 adds: a quote is a stored one-time offer with a terms hash, a fresh PIN step-up grant bound to that hash, single-use consumption inside the placement transaction, and a 30 second expiry | 10 |
| T6 | Webhook forgery or replay | HMAC-SHA256 signature over timestamp and raw body, constant-time compare, 5 minute tolerance, provider event-id dedupe; funding webhooks add an unknown-provider 404, a body size cap, stored outcomes with body hash only (never payload), and processing inside one transaction scope so a partial event cannot half-apply | 2, 12 |
| T7 | Provider outage or partial failure | pending states, timeouts, circuit breaker, reconciliation; Phase 10 adds: submission and execution are separate states, the progress job polls by our key, `needs_reconciliation` keeps funds held and nothing auto-releases on uncertainty | 10, 12, 15 |
| T8 | Signing key compromise | Phase 11 makes this concrete: no key material exists in our database, configuration, logs or process; signing happens only inside the custody provider or a KMS, HSM or MPC service behind `ICustodyProvider`; withdrawal destinations need a PIN step-up grant bound to the preview terms, pass address screening at add time and again at placement, sit in cooling-off before use, and reserves shortfalls pause withdrawals; an adapter that signs locally is rejected in review | 11 |
| T9 | Insider or admin abuse | RBAC, four-eyes on sensitive actions, immutable audit log | 15 |
| T10 | Prompt injection or data exfiltration through AI chat | tool allowlist, no tool moves money, retrieved text treated as untrusted, per-user data scoping | 9 |
| T11 | AI giving personalized advice or unsupported claims | policy layer, citations, disclaimers, audit | 9 |
| T12 | SSRF through news or market fetchers | allowlisted hosts, no user-supplied URLs | 5, 9 |
| T13 | PII in logs or backups | log policy now, field encryption, encrypted backups | 0, 16 |
| T14 | Mass assignment or over-posting | DTO-only binding, strict JSON | 0 |
| T15 | SQL injection | EF parameterization, raw SQL APIs banned | 0 |
| T16 | Supply chain compromise | lock files, audit, Dependabot, pinned actions, image scan | 0 |
| T17 | Secrets in repo or images | gitleaks, ignored `.env`, image history check | 0 |
| T18 | Denial of service | rate limits, body and timeout limits, pagination caps, DB timeouts | 0 |
| T19 | Erasure request versus financial retention | 30-day cooling-off, anonymization job, ledger keeps user id | 1, 16 |
| T20 | Clock or timezone bugs | `TimeProvider`, UTC only | 0 |
| T21 | Offering a service in an unsupported country | declared plus KYC-verified country gating, blocked and real-money country lists in configuration, eligibility gate on `/practice` and `/real`, legal review before real money | 2 |
| T22 | Practice and Real confusion | separate route groups enforced by `PracticeEligible`/`RealEligible` authorization policies, separate books, kill switch `RealMoneyEnabled`, tests that block crossover | 2, 3, 6 |
| T23 | Stale or wrong market data driving orders | freshness labels (`live`, `delayed`, `closed`, `halted`, `stale`, `unavailable`), `usableForExecution` false except live or delayed in a regular session, consumers must never execute on stale, halted or unavailable, outlier guard holds suspect ticks, never-backwards timestamps, quote validation rejects non-positive prices, wrong currency, excess digits and future timestamps | 5, 10 |
| T24 | Token rights misrepresented | mandatory claim type, backing, custodian, redemption, dividend, voting, transfer restriction, fee and risk disclosures on every token; listing guard refuses incomplete disclosures; review expiry blocks new buys; disclosures served verbatim in the token detail API | 4 |
| T25 | Practice credit farming through resets | reset cooldown and rolling 30 day limit counted from append-only `practice_events`, reset returns balances to system accounts and regrants only the fixed starting credits, idempotent replay returns the first result | 6 |
| T26 | Replayed or duplicated practice orders | `Idempotency-Key` required on every state-changing endpoint, order key stored unique with the user id inside the fill transaction, ledger journal keys derived from the order id, order and cancel lock the order row so trigger and cancel resolve exactly once | 6 |
| T27 | Gaming a stale or manipulated practice price | orders and stop triggers only use prices with `usableForExecution` true (live or delayed, regular session); halted, closed, stale and unavailable refuse with `practice.market_unavailable`; stop triggers re-read the current price at fill time so gaps show the worse price; outlier guard and never-backwards rules from Market Data apply | 5, 6 |

| T20 | Refresh token theft and replay | rotation, reuse detection, session revocation, alert email | 1 |
| T21 | OTP brute force | keyed hashes, attempt caps, expiry, constant-time compare, resend limits | 1 |
| T22 | Account enumeration via sign-up, login, reset | uniform responses, dummy verification work, identical throttling | 1 |
| T23 | Password hashing denial of service | bounded concurrency, queue then rate limit | 1 |
| T24 | Signing key leakage | keys in secret manager only, key id pinning, rotation window | 1, 16 |
| T25 | Session fixation | server-side sessions, new session per login, revocation | 1 |
| T26 | Secrets captured by idempotency store | returns-secrets tag, replay storage exemption, architecture rule | 0, 1 |
| T27 | Ledger tampering (edited or deleted history, direct balance writes) | `askvest_app` gets insert and select only on journals and entries; balances only change inside the posting transaction; deferred zero-sum trigger re-checks at commit; corrections are linked one-time reversals; scheduled reconciliation detects corruption without repairing it | 3 |
| T28 | Outbox duplicate or lost delivery | at-least-once delivery by design, handlers tolerate duplicates; atomic enqueue inside the business transaction; lease-based claiming so two dispatchers never take the same message; exponential backoff then dead-letter; payloads carry ids only, never amounts or secrets | 3 |
| T29 | Contract-address swap after listing (token contract upgraded or address reused) | once `ever_listed`, chain, canonical address, decimals, issuer, instrument and ledger asset code are immutable; a change requires a new token row and a new listing review; EIP-55 checksum canonicalization rejects lookalike addresses | 4 |
| T30 | Tampered catalog data (hostile data file or compromised import) | data files are reviewed through git like code; import is strict (unknown properties rejected), validates every reference and reports all problems before writing anything; import is all-or-nothing inside one transaction; application role is select-only on catalog tables | 4 |
| T31 | Stale disclosures (issuer terms changed, review expired) | every token carries reviewed-at and review-expires-at enforced by the listing guard and availability rules; an expired review blocks buying (`disclosure_review_expired`) but never selling; exit is always preserved | 4 |
| T32 | Bad provider tick (glitch, fat finger, hostile feed) overwrites a good price | outlier guard: a single jump beyond `MaxTickChangePercent` is held as suspect and never replaces the stored price; a confirming tick accepts it, a tick near the old price discards it; every decision logged at warning with the instrument id; invalid quotes rejected before storage | 5 |
| T33 | Provider outage or rate limiting | poller keeps last stored prices, labels them `stale` past the stale threshold and disables execution; candle requests serve cache with `stale` true or fail closed with `marketdata.unavailable`; retry delay honored, exponential backoff after repeated failures, advisory lock prevents duplicate polling across instances | 5 |
| T34 | Unlicensed market data redistribution | provider research is an open gate: no real adapter ships until the fact sheet records exchange coverage, feed type and explicit display and redistribution rights; the fake provider is refused in Production; a real adapter must declare delay, batch limits, intervals and session reporting before use | 5, 15 |
| T35 | Learning streaks that reward trading or risk taking | streaks count only lesson completion and passed quizzes; nothing else (orders, logins, deposits) writes a learning day; the module cannot reference ledger, practice or trading code (architecture test); no rewards of any kind attach to streaks | 7 |
| T36 | Sharing answers to pass required lessons | quiz reads never reveal correct options or explanations; attempts are append-only evidence rows; required lessons count only when completed at the current version, so content updates force re-learning; question pools and retry cooldowns stay in the backlog deliberately | 7 |
| T37 | Lesson content that promises returns or gives advice | content is plain, chain-agnostic and issuer-neutral; a configurable banned-phrase lint runs at startup and in CI over real and sample folders; required lessons get compliance review before merge; sample content is refused in Production | 7 |
| T38 | Misstated portfolio from stale or wrong prices | market value carries the freshness label and session; unvaluable or unclassified figures set flags and `partial` totals instead of guessing; unsupported currencies return null value with a flag | 8 |
| T39 | Cost basis drift from the ledger | the portfolio stores no money facts; it replays ledger movements on demand so it cannot diverge; weighted average cost with fees in cost; consistency check proves holdings equal ledger balances; replay cap fails closed at 20000 journals | 8 |

| T40 | Hallucinated numbers or unsupported factual claims in chat | numeric grounding requires every figure to trace to tool data or the user's own message; citations must reference results from the current exchange; policy layer and corrective retry, then a safe fallback | 9 |
| T41 | Personal data sent to the model provider | only consented users reach the model; the prompt carries the user's own messages and tool-scoped reads, never credentials, documents or ledger internals; provider research records data-processing and retention terms before selection | 9 |
| T42 | Cost abuse through unbounded model or tool calls | bounded history, tool-call and model-call caps, per-user rate limit, a time budget per request, token usage recorded in audit events | 9 |
| T43 | Conversation retention beyond policy | conversations and messages stored server-side with ids only in audit events; retention job removes expired conversations and deletes conversations of anonymized users; no cross-conversation memory | 9 |
| T44 | Replayed step-up grant places an order without fresh authorization | grants are single-use with a 120 second life, bound to purpose `authorize_order` and the quote terms hash, consumed atomically inside the placement transaction; a rolled-back order does not consume the grant | 10 |
| T45 | Faulty or hostile gateway reports a fill that differs from the quote | a fill must equal quantity, price and totals exactly or nothing posts to the ledger; mismatches move the order to `needs_reconciliation` with an error log | 10 |
| T46 | Stuck funds when submission outcome is unknown | `needs_reconciliation` never auto-releases; only a definitive gateway answer settles or releases, and "not found" is treated as ambiguous | 10 |
| T47 | Cancel versus dispatch race loses or double-spends a hold | cancel is allowed only in `pending_submission`; claim and cancel both lock the order row and check state so exactly one wins | 10 |
| T48 | Limit bypass through parallel orders | per-user transaction-scoped advisory lock serializes the limit check and order insert; unique constraints on quote id and (user, idempotency key) close the remaining races | 10 |
| T24 | Address poisoning or a hijacked session draining funds | withdrawal destinations are stored, screened clear at add and again at placement, blocked when equal to any deposit or internal address, and unusable during cooling-off; placement needs a fresh PIN grant bound to the preview terms hash; the address book caps at 10 and removal is immediate | 11 |
| T25 | Deposit spoofing, fake token contracts and spam deposits | crediting requires the destination to be a stored user deposit address, the token contract to resolve to a known catalog token on that chain, the sender to screen clear, provider finality and the dust minimum; everything else is stored with a reason and never credited; treasury deposits never credit users | 11 |
| T26 | Reserves shortfall (custody holds less than the ledger claims) | the reserves job compares provider holdings to ledger balances per chain and token, skips in-flight items, and pauses withdrawals for that token after two consecutive mismatches; pauses persist and only an operator resumes them | 11, 15 |
| T27 | Fake or hostile custody statuses | statuses are polled by our key only, `not_found` is never definitive, completion requires provider finality, uncertainty keeps the hold in `needs_reconciliation`, and settlement references are stored opaquely | 11 |
| T28 | Deposit reversal fraud or money-back abuse | reversals post `deposit_reversal` for at most the available balance, record any shortfall on the deposit, mark it `reversed` and place a funding restriction that blocks new cash withdrawals until an operator clears it | 12, 15 |
| T29 | Third-party or mule deposits (someone else's money) | credit requires the payer to be provider-verified against the user; unverified, over-limit or restricted deposits are quarantined with a reason for an operator, never credited and never silently refused | 12, 15 |
| T30 | Structuring (many small deposits or withdrawals to dodge limits) | per-transaction and rolling daily deposit caps, rolling 24 hour and 30 day withdrawal caps per currency, all checked under the per-user lock; limits are configuration reviewed before launch | 12 |
| T31 | FX rate manipulation or stale rates | quotes refuse rates older than `FxMaxAgeSeconds`, rates that jump beyond `FxMaxJumpPercent` per pair, disabled pairs and provider errors; the quote freezes rate, spread, fee and terms hash for 30 seconds; conversion exposure is ours and recorded as a business risk | 12 |
| T32 | Payout misdirection (beneficiary swapped after verification) | beneficiary details go to the provider and are never stored by us; adding needs a PIN grant bound to currency and provider reference, the provider's `nameMatch` must be true, and a new beneficiary cools off before use; removal is immediate | 12 |
| T33 | Drain through a hijacked session | cash withdrawals need a fresh PIN grant bound to the preview terms hash; beneficiary changes need their own bound grant; fresh-deposit lock, review thresholds, minimum account age, rolling caps and funding restrictions all sit between a session and money leaving | 12 |
| T34 | Provider settlement balance shortfall | the fiat reserves job compares provider-reported balances to ledger obligations per currency, skips in-flight items, counts only shortfalls and pauses cash withdrawals for that currency after two consecutive mismatches; the pause persists and only an operator resumes it | 12, 15 |
| T35 | Money arrived for nobody (unmatched account reference) | stored as `ignored` with a detail code, acknowledged to the provider, logged for operators; never credited and never lost silently | 12, 15 |
