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

## Chain-aware provider contract (Phase 4)

Every external provider that touches on-chain assets is chain-aware. Each adapter declares, in its registration:

- the chains it supports, as CAIP-2 identifiers;
- the token standards it supports per chain (for example `erc20` on `eip155:*`);
- its capabilities: quote, execute, redeem, webhooks, sandbox, and documented rate limits.

The catalog token's free-form **gateway key** names which execution adapter handles that token. Phase 10 resolves the key to an adapter and fails closed when no adapter claims the token's chain and standard. No adapter is assumed to exist yet; the key is stored and required for listing but never exposed by the API.

Chain identity rules live in `00-system-overview.md` under "Catalog chain-agnostic rules": CAIP-2 chain ids, CAIP-19 asset ids computed from chain, standard and canonical address, and a chain family handler (`evm` implemented; `svm`, `move`, `other` reserved) as the only seam that knows address formats.

## Provider fact sheet (fill one column per provider during research)

| Question | Provider A | Provider B | Provider C |
|---|---|---|---|
| Chains supported (CAIP-2) | | | |
| Token standards supported | | | |
| What the holder legally owns | | | |
| What backs the token | | | |
| Custodian and custody model | | | |
| Geographic restrictions | | | |
| Who carries KYC and AML duties | | | |
| Mint and redeem mechanics, windows, minimums | | | |
| Quote and execution model (RFQ, pool, order book) | | | |
| Quote validity and latency | | | |
| Fee structure | | | |
| Self-custody vs omnibus; address allowlisting | | | |
| Settlement finality and confirmations | | | |
| Dividends, splits, corporate actions | | | |
| Closed or halted underlying market behavior | | | |
| Webhooks and signature scheme | | | |
| Sandbox availability | | | |
| Rate limits and support commitments | | | |
| Reporting and statements | | | |
| Licenses and legal agreements needed | | | |

## Open research questions

Provider research is still open; findings land in the fact sheet above and become catalog data files plus at most one adapter, never a redesign.

- Which chains and token standards will AskVest support first, and in what order?
- Which issuers issue tokenized stocks and ETFs for the launch markets, and what does the holder legally own in each case?
- Who is the custodian for each issuer's tokens, and is custody self-custody, omnibus or issuer-managed?
- Which geographic restrictions apply per issuer and per country, and how do they interact with the compliance country lists?
- Who carries KYC and AML duties: AskVest, the issuer, the gateway, or a combination?
- What are the mint and redeem mechanics: windows, minimums, settlement time, and who may redeem?
- What is the quote and execution model (request for quote, liquidity pool, order book), typical latency, and quote validity?
- What is the full fee structure: issuer fees, gateway fees, spread, network fees?
- Does the provider support self-custody withdrawals to arbitrary addresses, or is there an address allowlist?
- What settlement finality and confirmation counts are required per chain?
- How are dividends, splits and other corporate actions handled for token holders?
- What happens to quotes and orders when the underlying market is closed, halted or in a corporate action?
- What webhooks exist and what is the signature scheme?
- Is a sandbox available for integration testing?
- What are the rate limits and support commitments?
- What reporting and statements are available for users and for reconciliation?
- Which licenses and legal agreements are required to offer the tokens in each launch country?
