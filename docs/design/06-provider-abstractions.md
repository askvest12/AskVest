# 06. Provider Abstractions

No provider is confirmed. Each external dependency gets one interface in a `Contracts` namespace plus a deterministic fake for development and tests. Real adapters are chosen per country and issuer in later phases.

| Interface | Boundary | Fake behavior |
|---|---|---|
| `IKycProvider` | identity verification | scripted pass/fail per test |
| `ISanctionsScreeningProvider` | sanctions/PEP checks | deterministic hit/miss lists |
| `IMarketDataProvider` | prices, candles | fixed series, controllable staleness |
| `IIssuerGateway` | quote, execute, redeem tokenized securities | in-memory order book |
| `ICustodyProvider` | wallets, addresses, signing | local key simulation, never real keys |
| `IFundingProvider` | deposits, conversions, withdrawals, webhooks | scripted webhook sequences |
| `IFxRateProvider` | FX rates | fixed rate table |
| `INotificationChannel` | push, SMS, email | records sent messages |
| `ILanguageModelClient` | AI chat completions and tool calls | scripted responses |

Rules for every adapter:

- Every call has a timeout and a documented retry policy.
- Every mutating call carries an idempotency key.
- Contract tests run against both the fake and the real adapter once chosen, so swapping providers cannot silently change semantics.
- Adapters live at module boundaries only; domain code never references a provider SDK type.
- Webhook-facing providers must support signature verification and event-id dedupe (T6).
