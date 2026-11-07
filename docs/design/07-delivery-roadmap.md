# 07. Delivery Roadmap

Phases 0 to 17. Each phase ends with the Definition of Done in `CLAUDE.md`.

| Phase | Scope | Depends on |
|---|---|---|
| 0 | Design, hardened foundation, containers, CI (this phase) | none |
| 1 | Identity and access: users, credentials, sessions, devices, PIN (done) | 0 |
| 2 | Compliance: country eligibility, KYC, consents, Practice/Real mode switch (provider interfaces and fakes only; real adapters when a provider is chosen; manual review queue stays in 15) | 1 |
| 3 | Ledger: accounts, journals, balances, outbox, Practice and Real books | 2 |
| 4 | Catalog: companies, tokens, issuers, rights and risk disclosures | 3 |
| 5 | Market data: prices, candles, freshness labels | 4 |
| 6 | Practice: virtual credits, simulated orders | 3, 4, 5 |
| 7 | Learning: lessons, quizzes, streaks | 1 |
| 8 | Portfolio: holdings, cost basis, watchlist | 3, 4, 6 |
| 9 | Assistant: AI chat, tool calls, citations, audit | 1, 4, 8 |
| 10 | Trading: quotes, real orders, order state machine | 2, 3, 4, 5, 8 |
| 11 | Wallets: custody abstraction, addresses, signing requests | 1, 2 |
| 12 | Funding: deposits, conversion, withdrawals, webhooks | 3, 10, 11 |
| 13 | Notifications: channels, alerts, preferences | 1 |
| 14 | Earn: opt-in positions, yield sources | 3, 10 |
| 15 | Back office: admin, audit log, reconciliation, feature flags | 1, 3 |
| 16 | Hardening for launch: field encryption, backup encryption, erasure flow | all product phases |
| 17 | Observability and scale: OpenTelemetry, performance, launch | all |

Post-launch track: price alerts, Stop Loss execution, Borrow, gifting, copy investing, family tools.

```mermaid
flowchart TD
    P0[0 Foundation] --> P1[1 Identity]
    P1 --> P2[2 Compliance]
    P2 --> P3[3 Ledger]
    P3 --> P4[4 Catalog]
    P4 --> P5[5 MarketData]
    P3 --> P6[6 Practice]
    P5 --> P6
    P1 --> P7[7 Learning]
    P3 --> P8[8 Portfolio]
    P4 --> P8
    P6 --> P8
    P1 --> P9[9 Assistant]
    P4 --> P9
    P8 --> P9
    P2 --> P10[10 Trading]
    P3 --> P10
    P5 --> P10
    P8 --> P10
    P1 --> P11[11 Wallets]
    P2 --> P11
    P3 --> P12[12 Funding]
    P10 --> P12
    P11 --> P12
    P1 --> P13[13 Notifications]
    P3 --> P14[14 Earn]
    P10 --> P14
    P1 --> P15[15 BackOffice]
    P3 --> P15
    P16[16 Hardening] --> P17[17 Observability]
```
