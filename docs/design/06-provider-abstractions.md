# 06. Provider Abstractions

No provider is confirmed. Each external dependency gets one interface in a `Contracts` namespace plus a deterministic fake for development and tests. Real adapters are chosen per country and issuer in later phases.

| Interface | Boundary | Fake behavior |
|---|---|---|
| `IKycProvider` | identity verification | scripted pass/fail per test |
| `ISanctionsScreeningProvider` | sanctions/PEP checks | deterministic hit/miss lists |
| `IMarketDataProvider` | prices, candles | fixed series, controllable staleness |
| `IIssuerGateway` | quote, execute, redeem tokenized securities | in-memory order book |
| `ICustodyProvider` | deposit addresses, deposit reporting, withdrawal submission and status, holdings, internal addresses | deterministic fake; Privy adapter behind `Custody:Routes` (ADR 0022) |
| `IAddressScreeningProvider` | address risk screening | deterministic fake |
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

## Market data provider contract (Phase 5)

`IMarketDataProvider` is the only seam to a market data vendor. It fetches latest quotes for a batch of provider symbols and candles for one symbol, interval and range. It is provider-agnostic: no vendor, exchange list, feed type or licensing model is assumed.

Each provider declares its capabilities at registration:

- real-time or delayed, with the delay in minutes when delayed;
- supported candle intervals and a maximum batch size;
- whether it reports the market session (`pre`, `regular`, `post`, `closed`, `halted`, `unknown`).

Rules every quote and candle must satisfy:

- timestamps in UTC; prices and volumes parsed from the provider's text as `decimal`, never binary floating point;
- sub-currency units normalized to the instrument's catalog quote currency before returning (for example pence versus pounds);
- candles are requested split and dividend adjusted, and the adjusted flag is returned;
- provider errors map to exactly four outcomes: transient failure, rate limited (with the retry delay), authentication failure, and symbol not found; anything else is a transient failure;
- a call never blocks past the configured timeout.

Latest prices are fetched only by the poller, which batches within the provider's declared maximum and the configured requests-per-minute budget. Request handlers never call the provider for latest prices; they read the database. Candle requests may call the provider only to fill the cache, under single-flight. Symbol mapping derives the provider symbol from the instrument's symbol, listing exchange and country, with optional per-instrument overrides in configuration. Redistribution and display rights are a research question recorded in the fact sheet below, not an adapter concern.

## Market data adapter contract

Any real adapter added later must satisfy, in addition to the general adapter rules above:

1. Return UTC timestamps and parse prices and volumes from text as decimals; never return `double` or `float`.
2. Normalize sub-currency units to the instrument's quote currency.
3. Request split and dividend adjusted candles and return the adjusted flag.
4. Declare real-time versus delayed, maximum batch size, supported intervals and whether it reports the session.
5. Map provider errors to the four outcomes above; unknown errors are transient failures.
6. Respect the configured timeout on every call.
7. Pass the production guard: only the deterministic fake exists in this phase, and the fake is refused in Production.

## Market data fact sheet (fill one column per provider during research)

| Question | Provider A | Provider B | Provider C |
|---|---|---|---|
| Which exchanges and instrument types are covered | | | |
| Real-time versus delayed feeds, and the cost of each | | | |
| Is end-user display and redistribution licensed; per-user or per-instrument fees | | | |
| Historical depth and supported candle intervals | | | |
| Split and dividend adjustment behavior | | | |
| Pre-market and post-market data availability | | | |
| Trading halt and market status feeds | | | |
| ETF net asset value data | | | |
| REST versus streaming | | | |
| Rate limits, batch size and request quotas | | | |
| Sandbox availability | | | |
| Timestamp semantics (exchange time, receipt time, delay) and currency conventions | | | |
| Support and uptime commitments | | | |
| On-chain or issuer reference prices for tokens | | | |

## Assistant language model contract (Phase 9)

`ILanguageModelClient` is the only seam to a language model. It sends a system prompt, a bounded message history, tool definitions and returns either final text or tool calls plus token usage. The Assistant module never references a provider SDK; the deterministic fake ships for development and tests, and a real adapter is chosen only after evaluation.

Hard rules for every adapter and for the module:

- The model client performs the single outbound network call the module makes; nothing else in Assistant opens a socket.
- Tool definitions are fixed server-side; the model cannot invent tools.
- The model may be wrong, hallucinate or be prompt-injected; every output passes server-side citation, numeric-grounding and policy checks before a user sees it.
- Evaluation against the checked-in evaluation set is mandatory before a real provider may serve production traffic.

## Language model provider research (fill one column per provider)

No provider is confirmed. Record findings before selecting one.

| Question | Provider A | Provider B | Provider C |
|---|---|---|---|
| Tool calling support and limits | | | |
| Data processing and retention terms | | | |
| Regions where prompts may be processed | | | |
| Token pricing and cost controls | | | |
| Rate limits and quotas | | | |
| Context size versus our bounded history | | | |
| Streaming support (deferred; Phase 9 ships non-streaming) | | | |
| Content moderation or safety filters | | | |
| Whether prompts are used for provider training | | | |
| Determinism or seed controls for evaluation | | | |

## Issuer gateway contract (Phase 10)

`IIssuerGateway` is the only seam to an issuer or venue. One interface, resolved per token through the token's gateway key and the configured route table; the module contains no chain-specific code and treats settlement references as opaque text (a chain id plus a reference). The deterministic fake is the only built-in route and is refused in Production.

The interface surface:

- **Firm quote** for a token reference (catalog token id, chain id, canonical address, decimals), side and amount or quantity, returning price, quantity, a fee breakdown, the settlement asset, an expiry and the gateway quote id.
- **Submit** with our order id as the idempotency key plus the gateway quote id, side and quantities, returning an acknowledgement with a gateway reference or a definitive rejection with a reason code.
- **Status** by our key returning `pending`, `filled` (with fill details and the settlement reference) or `rejected`, or `not_found`. "Not found" is never treated as definitive.

The gateway declares the settlement asset and the tokens it supports; the route configuration maps a gateway key to a provider and a settlement asset (default `USD`).

Rules every adapter must satisfy:

1. Submission is idempotent by our order id; a repeated submission cannot create two orders.
2. Quotes are firm with an explicit expiry.
3. Fills are all or nothing; an adapter reports `filled` only after the issuer's finality rule (for example required chain confirmations) is satisfied.
4. Status by our key exposes only definitive states; `not_found` is ambiguous.
5. All timestamps are UTC; decimals are parsed from text, never binary floating point.
6. Errors map to exactly five outcomes: transient, rate limited (with retry delay), rejected (with reason code), not found, authentication failure.
7. Every call respects the configured timeout.
8. Fees are reported as a breakdown, never a single opaque number.
9. The fake gateway is forbidden in Production; a real adapter adds its credentials to the secret store, never to this configuration.

The fake gateway quotes from the Market Data reference price plus a configured spread and fills after a configured delay. Scenarios, set by configuration or by `POST /api/v1/diagnostics/gateway-scenario` behind the probe flag: `normal`, `delayed_fill`, `rejected`, `quote_error`, `short_quote_expiry`, `slow_submit`, `submit_timeout_filled`, `submit_timeout_lost`, `status_unavailable`, `fill_mismatch`.

## Custody provider contract (Phase 11)

`ICustodyProvider` is the only seam to a custody or key management provider. Keys never enter our process: signing happens inside the provider or a KMS, HSM or MPC service, and no adapter that signs locally may be approved. The module contains no chain-specific code; chains are catalog ids and token references are chain id plus canonical contract address.

The interface surface:

- **Deposit instruction**: get or create the deposit address (and optional memo) for a user reference and chain.
- **Deposit listing**: list deposits for a chain since a cursor. Each deposit carries a stable provider deposit id, the destination address, the token reference (chain id plus canonical contract address), the quantity, the sender address, a settlement reference, an explicit finality state (`pending` or `final`) and an observed time.
- **Withdrawal submission**: submit with our withdrawal id as the idempotency key, returning an acknowledgement or a definitive rejection with a reason code.
- **Withdrawal status** by our key returning `pending`, `completed` (with the settlement reference), `failed` or `not_found`. `completed` only after the provider's finality rule.
- **Holdings**: custody balances per chain and token, used by the reserves check.
- **Internal addresses**: whether an address belongs to us (deposit or treasury), used to refuse withdrawals to ourselves.

The provider declares its supported chains, address model, memo support and finality rule.

## Address screening contract (Phase 11)

`IAddressScreeningProvider` screens a chain id and address and returns `clear`, `hit` or `error` with a reference. Every call fails closed: a provider error is never treated as clear, at address-book add time, at withdrawal placement and for every deposit source.

## Custody and screening adapter contract

Every real custody or screening adapter must satisfy:

1. Keys and signing material never exist in our database, configuration, logs or process memory.
2. Withdrawal submission is idempotent by our key; a repeated submission cannot create two withdrawals.
3. Deposits are reported with stable ids and an explicit finality state; `final` only after the chain's finality rule.
4. Withdrawals report `completed` only after finality; status by our key exposes only definitive states and `not_found` is ambiguous.
5. All timestamps are UTC; decimals are parsed from text, never binary floating point.
6. Errors map to exactly five outcomes: transient, rate limited (with retry delay), rejected (with reason code), not found, authentication failure.
7. Every call respects the configured timeout.
8. Where the provider supports policy (address allowlists, limits, approvals), the adapter mirrors our rules there as defense in depth.
9. The adapter never returns secrets and never logs addresses, amounts or credentials.
10. The fake providers are forbidden in Production; real adapters add credentials to the secret store, never to configuration.

## Custody and screening research (fill one column per provider during research)

Custody provider A is Privy, selected by ADR 0022. Cells marked "open" need answers from Privy or our own legal review before launch.

| Question | Provider A (Privy) | Provider B | Provider C |
|---|---|---|---|
| Custody model: per-user deposit addresses or omnibus | Per-user wallets via `external_id`; one `ethereum` chain-type wallet covers all eip155 chains | | |
| Who controls the keys; MPC, HSM or KMS; multi-signature | Privy-held key shares with a policy engine and owner key quorum option; open | | |
| Recovery and emergency release procedures | open | | |
| What happens if the provider fails or AskVest is down | User wallets stay at Privy; our ledger is unaffected; withdrawals stall in submitted state until status resolves | | |
| Insurance and audit coverage | open | | |
| Supported chains and token standards | EVM (all eip155 chains configured), Solana, Tron, Bitcoin; ERC-20 transfers implemented | | |
| Deposit detection method and finality rule per chain | Wallet transaction history polling; final on `confirmed`/`finalized` status | | |
| Memo and tag support | No (`SupportsMemo` false; EVM chains do not need memos) | | |
| Fee and gas model | Gas sponsorship available; treasury wallet must be funded for withdrawals; open | | |
| Policy engine: address allowlists, limits, approvals | Policy engine and quorum approvals documented; mirroring our withdrawal rules is a launch blocker | | |
| Idempotency and status endpoints | `privy-idempotency-key` header on writes; `reference_id` on transactions queried by `/v1/transactions` | | |
| Sandbox, rate limits, support commitments | Sandbox environment and public pricing confirmed; rate limits open | | |
| Treatment of issuer transfer restrictions | open | | |
| Address screening coverage (chains, chains of risk) | open (separate screening provider decision) | | |
| Licenses and legal agreements required | open | | |

## Funding provider contract (Phase 12)

`IFundingProvider` is the only seam to a payment or payout provider. The module contains no vendor, country or rail specific code: currencies, rails, countries, limits and fees are configuration, and the provider declares what it supports. The deterministic fake is the only built-in route and is refused in Production.

The interface surface:

- **Funding instruction**: get or create the instruction for a user reference and currency. Rails are account-transfer style only: the user pays from their own bank to the provider-issued details; there are no cards and no hosted payment pages.
- **Deposit listing**: list deposits since a cursor for the polling fallback. Each deposit carries a stable provider deposit id, the account reference, the currency, the amount, the provider fee, a payer-verified flag, a state (`pending`, `completed`, `failed`, `reversed`) and an observed time.
- **Beneficiary**: add a beneficiary for a user reference and currency with a details map validated against the provider's declared schema for that currency, returning a beneficiary reference, a masked display and a `nameMatch` flag. Bank or mobile money details are passed through and never returned in full.
- **Payout**: request a payout with our withdrawal id as the idempotency key, beneficiary reference, currency and amount, returning an acknowledgement or a definitive rejection.
- **Payout status** by our key returning `pending`, `completed`, `failed` or `not_found`. `completed` only after the provider's final settlement.
- **Settlement balance**: the provider's reported balance per currency, used by the fiat reserves check.
- **Webhook verify and parse**: signature check over timestamp and raw body, then normalization into the same event shapes the polling path produces.

The provider declares supported currencies, the required beneficiary fields per currency and its limits.

## FX rate provider contract (Phase 12)

`IFxRateProvider` returns the mid rate for a currency pair with an as-of time and a source label. We are the counterparty for conversions: the Funding module applies the configured spread and fee itself and never trusts a provider-side converted amount. The deterministic fake is the only built-in provider and is refused in Production.

## Funding and FX adapter contract

Every real funding or FX adapter must satisfy, in addition to the general adapter rules above:

1. Payouts are idempotent by our withdrawal id; a repeated request cannot create two payouts.
2. Deposit ids are stable across listing pages; `completed` only after final settlement, never on an optimistic state.
3. The payer-verified flag is reported explicitly; an absent flag is treated as unverified.
4. Beneficiary name match is reported explicitly; bank details are never returned in full.
5. Payout status by our key exposes only definitive states; `not_found` is ambiguous.
6. Webhooks carry a documented signature scheme over timestamp and raw body plus a unique event id; retries and replay are safe because of event-id dedupe.
7. All timestamps are UTC; decimals are parsed from text, never binary floating point.
8. Errors map to exactly five outcomes: transient, rate limited (with retry delay), rejected (with reason code), not found, authentication failure.
9. Every call respects the configured timeout.
10. A settlement report or balance endpoint exists for the reserves check.
11. The fake providers are forbidden in Production; real adapters add credentials to the secret store, never to configuration.

## Funding and FX research (fill one column per provider during research)

Payment rails per country, FX coverage and licensing are open research. Record findings before selecting providers; these are launch blockers.

| Question | Provider A | Provider B | Provider C |
|---|---|---|---|
| Payment rails per country: bank transfer, virtual accounts, mobile money | | | |
| Settlement time and finality rule per rail | | | |
| Reversal, chargeback and return rules | | | |
| Fee structure per rail and per payout | | | |
| Payer name verification against the account holder | | | |
| Beneficiary name enquiry and match reporting | | | |
| Payout rails and speed per country | | | |
| Whether the provider relies on our identity checks or runs its own KYC | | | |
| Webhook signature scheme, retries and replay behavior | | | |
| Idempotency support on payout endpoints | | | |
| Sandbox availability | | | |
| Rate limits and quotas | | | |
| Settlement reports and balance endpoints for reserves | | | |
| Limits per user tier | | | |
| Licenses required in each launch country | | | |
| Capital controls and currency convertibility, including local currency restrictions | | | |
| FX provider: pair coverage, rate frequency, firm quote availability | | | |
| FX liquidity, hedging options and exposure handling | | | |
| Data residency for payment and identity data | | | |

## Exit plan requirements (any real custody provider)

If AskVest is down or gone, users' assets must remain recoverable by an independent party. A real provider must satisfy:

- Assets are held in accounts the provider can release to an independent operator under a documented procedure.
- A periodic export of user ownership (ledger balances by user and token) is kept outside our infrastructure.
- Named operators and a tested runbook exist for the release procedure.
- The legal arrangements covering this exit are in place before launch.

Phase 11 builds the reserves check and the export-friendly data model; the legal and operational pieces remain launch blockers.

## Gateway fact sheet (fill one column per provider during research)

| Question | Provider A | Provider B | Provider C |
|---|---|---|---|
| Which chains and token standards are supported | | | |
| What the holder legally owns | | | |
| Quote model (RFQ, pool, order book), firm quote validity and latency | | | |
| Fee breakdown: issuer, gateway, spread, network | | | |
| Minimums and maximums per order and per user | | | |
| Settlement asset and whether conversion is supported | | | |
| Settlement finality rule and required confirmations per chain | | | |
| Idempotency support on submit and status endpoints | | | |
| Who holds the tokens: self-custody, omnibus or issuer-managed | | | |
| Operating hours, cutoffs and closed-market behavior | | | |
| Sandbox availability | | | |
| Rate limits and quotas | | | |
| Error semantics and retry guidance | | | |
| Reconciliation and reporting support | | | |
| Licensing and legal agreements required | | | |

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
