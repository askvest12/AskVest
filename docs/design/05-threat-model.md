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
| T1 | Account takeover (credential stuffing, OTP interception) | rate limits, lockout, device binding, step-up for money moves | 1 |
| T2 | Replay or duplicate money movement | idempotency middleware plus unique key per operation | 0, 3, 10, 12 |
| T3 | Race condition on balance (double spend) | row locking or serializable plus invariant checks | 3 |
| T4 | Object-level authorization bypass (IDOR) | ownership in every query, cross-user tests per endpoint | 1 onward |
| T5 | Quote tampering or stale quote | server-side signed quotes, expiry, never trust client amounts | 10 |
| T6 | Webhook forgery or replay | HMAC signature, timestamp tolerance, event-id dedupe | 12 |
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
| T19 | Erasure request versus financial retention | anonymize personal data, retain ledger | 1, 16 |
| T20 | Clock or timezone bugs | `TimeProvider`, UTC only | 0 |
| T21 | Offering a service in an unsupported country | country gating, legal review before real money | 2 |
| T22 | Practice and Real confusion | route separation, separate books, tests that block crossover | 2, 3, 6 |
| T23 | Stale or wrong market data driving orders | freshness labels, reject stale prices | 5, 10 |
| T24 | Token rights misrepresented | mandatory rights and risk fields on every asset | 4 |
