# 04. State Machines

Rule for every machine: every transition is a method on the aggregate that returns `Result`. An illegal transition returns `business.rule_violated`. Every transition writes an audit event.

## Order

```mermaid
stateDiagram-v2
    [*] --> draft
    draft --> quote_ready
    quote_ready --> expired: quote TTL passed
    quote_ready --> authorized
    authorized --> submitted
    submitted --> executed
    submitted --> failed
    authorized --> cancelled
    draft --> cancelled
    submitted --> needs_reconciliation: outcome unknown
    needs_reconciliation --> executed: confirmed
    needs_reconciliation --> failed: confirmed absent
```

Invariants: submission and execution are separate states; an expired quote never silently becomes a new trade; terminal states are `executed`, `failed`, `cancelled`.

## Deposit

```mermaid
stateDiagram-v2
    [*] --> initiated
    initiated --> awaiting_payment
    awaiting_payment --> confirming
    confirming --> credited
    awaiting_payment --> expired
    confirming --> failed
    credited --> reversed: provider reversal
```

Terminal: `credited` (unless reversed), `failed`, `expired`.

## Withdrawal

```mermaid
stateDiagram-v2
    [*] --> requested
    requested --> under_review
    under_review --> approved
    under_review --> rejected
    approved --> submitted
    submitted --> completed
    submitted --> failed
    requested --> cancelled
    under_review --> cancelled
```

Terminal: `completed`, `failed`, `rejected`, `cancelled`.

## Eligibility

```mermaid
stateDiagram-v2
    [*] --> not_started
    not_started --> in_review
    in_review --> approved
    in_review --> rejected
    in_review --> expired
    approved --> restricted: risk event
    restricted --> approved: review clears
```

## Earn position

```mermaid
stateDiagram-v2
    [*] --> opted_in
    opted_in --> active
    active --> withdrawal_requested
    withdrawal_requested --> active: cancelled
    withdrawal_requested --> closed
```

## Account mode

```mermaid
stateDiagram-v2
    [*] --> practice_only
    practice_only --> real_money_enabled: eligibility approved
```

`real_money_enabled` is terminal in this model; demotion is handled as a restriction flow in Phase 2, not a silent flip.

## KYC verification (Compliance, Phase 2)

```mermaid
stateDiagram-v2
    [*] --> not_started
    not_started --> in_review: verification started
    in_review --> approved: provider approval + screening clear
    in_review --> approved: provider approval + screening hit (account restricted)
    in_review --> rejected: provider rejection
    approved --> expired: approval validity elapsed (computed, no job)
    rejected --> in_review: new attempt while attempts remain
```

`kyc_status` on the compliance profile is `not_started`, `in_review`,
`approved` or `rejected`. Expiry is derived from the stored expiry time
against `TimeProvider`; there is no background job. Screening state is
`not_run`, `clear` or `hit`; a hit restricts the account for Real without
changing the KYC status.

## Identity state machines

- User: `pending_verification` to `active` on email code verification; `active` to `deletion_requested` on deletion request; `deletion_requested` to `anonymized` by the scheduled job; `active` to `disabled` by later admin tooling. `anonymized` is terminal.
- Session: created to `revoked` with a reason. Revocation is final.
- Device: created untrusted to `trusted` on first verified login or device code; `trusted` to `revoked`. Revoking a device revokes its sessions.
- One-time code: issued to `consumed` or `expired`; attempts increment atomically up to the cap. Single use.
- Step-up grant: issued to `consumed` or `expired`; 120 second life, single use.
