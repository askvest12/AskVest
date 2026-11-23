# ADR 0022: Custody and ramp provider selection

Status: accepted (post Phase 12 research)

## Context

Phase 11 shipped the custody seam (`ICustodyProvider`) with a deterministic
fake and an adapter contract; Phase 12 shipped funding with provider seams
(`IFundingProvider`, `IFxRateProvider`) and fakes only. Real adapters were
explicitly deferred "when research picks a vendor". Two vendors were
evaluated against the platform documentation:

- Privy (privy.io): embedded and server-side wallets, policy engine,
  per-wallet transaction history, balances, transaction `reference_id`
  reconciliation, fiat deposits and payouts orchestrated through Bridge,
  crypto deposit addresses with conversion, KYC/KYB entities. REST API,
  app-secret auth, sandbox environment, public pricing.
- Yara (developers.yara.cash): Wallets as a Service (per-chain deposit
  addresses crediting a merchant USD balance), transfer quotes and payouts
  to whitelisted bank or crypto recipients (NGN, GHS, USD confirmed),
  customer KYC/KYB (BVN based), USD virtual accounts over ACH and wire.
  Ramps (NGN/GHS fiat on and off ramp) and Liquidity are announced but not
  live.

Building our own wallet infrastructure was also considered and rejected:
rule 30 forbids key material anywhere in our process, so "our own" would
still mean integrating an MPC or custody vendor directly, at a cost of
months of integration and audit work for capabilities Privy already
bundles.

## Decision

1. **Custody and wallet infrastructure: Privy.** A `PrivyCustodyProvider`
   implements `ICustodyProvider` behind the existing `Custody:Routes`
   configuration. It provisions one Privy wallet per user per chain family
   (`external_id` = user reference), serves EVM chains first, funds
   withdrawals from a configured treasury wallet, and reconciles
   withdrawals through Privy's transaction `reference_id`.
2. **African fiat ramps: Yara, later.** Yara's NGN and GHS bank payout
   recipients are live today and its local-currency ramps are the missing
   on-ramp for Phase 12 funding. It will be evaluated as an
   `IFundingProvider` or payout adapter when its ramps launch; nothing in
   this phase depends on it.
3. **Privy's fiat flows (Bridge) are a separate later decision.** USD and
   EUR virtual accounts and crypto-to-fiat payouts may serve the USD
   corridor, but Nigerian local-currency coverage through Bridge is
   unverified and is not assumed.

## Consequences

- The Wallets module keeps no chain-specific logic; the adapter translates
  CAIP-2 chain ids to Privy chain types and history names in one place.
- Per-user deposit addresses are real on-chain addresses. The module's
  `wallet_addresses` rows cache them; the adapter re-derives the same
  wallet through `external_id` lookups, so restarts cannot fork identity.
- Withdrawals are ERC-20 `transfer` calls from the treasury wallet on the
  withdrawal's chain. Non-EVM families return a definitive rejection until
  adapters for them exist.
- Deposit discovery is polling only, per the Phase 11 contract: the
  adapter scans provider wallets round-robin within a bounded batch per
  call. Webhooks remain a backlog item.
- Privy credentials (`AppId`, `AppSecret`) are configuration values loaded
  from the secret store in real deployments, matching the Phase 12
  precedent for provider secrets; they are never logged.
- Launch blockers remain: Privy entity verification and policies mirroring
  our withdrawal rules, gas sponsorship or funded gas for the treasury
  wallet, sandbox contract tests, the exit plan legal arrangements, and an
  independent custody review. Real custody must not be enabled until these
  are cleared.
