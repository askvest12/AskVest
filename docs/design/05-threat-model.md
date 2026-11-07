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
| T2 | Replay or duplicate money movement | idempotency middleware plus unique key per operation; journals carry a unique idempotency key plus content hash, same key and content replays, different content conflicts; inserts tolerate conflicts without aborting the transaction | 0, 3, 10, 12 |
| T3 | Race condition on balance (double spend) | Read Committed plus deterministic row locks on balance rows sorted by account id; user-kind balances forbidden negative by check constraint; deadlock or lock timeout surfaces `service.unavailable` and the client retries with its key | 3 |
| T4 | Object-level authorization bypass (IDOR) | ownership in every query, not-found answers, cross-user tests per endpoint | 1 onward |
| T5 | Quote tampering or stale quote | server-side signed quotes, expiry, never trust client amounts | 10 |
| T6 | Webhook forgery or replay | HMAC-SHA256 signature over timestamp and raw body, constant-time compare, 5 minute tolerance, provider event-id dedupe (Phase 2 KYC webhook; funding webhooks in 12 reuse the same contract) | 2, 12 |
| T7 | Provider outage or partial failure | pending states, timeouts, circuit breaker, reconciliation | 10, 12, 15 |
| T8 | Signing key compromise | KMS or HSM, no raw keys in DB or env, rotation, signing limits | 11 |
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
| T23 | Stale or wrong market data driving orders | freshness labels, reject stale prices | 5, 10 |
| T24 | Token rights misrepresented | mandatory claim type, backing, custodian, redemption, dividend, voting, transfer restriction, fee and risk disclosures on every token; listing guard refuses incomplete disclosures; review expiry blocks new buys; disclosures served verbatim in the token detail API | 4 |

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
