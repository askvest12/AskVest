# ADR 0001: Modular monolith

- Status: Accepted

## Context

AskVest is a financial product with many bounded contexts (identity, ledger, trading, funding) built by a small team. The data is relational and most operations are transactional. Distribution cost must stay low until scale demands otherwise.

## Decision

One deployable monolith. Modules live under `src/Modules/AskVest.Modules.<Name>`, own one database schema each, talk through `Contracts` interfaces and outbox integration events, and never read each other's tables or use cross-module foreign keys.

## Consequences

Simple deployment, one transaction boundary story, easy local development. Module discipline is enforced by architecture tests and review, not by the runtime.

## Alternatives rejected

- Microservices: operational cost (deployments, distributed tracing, sagas) far exceeds the benefit for a small team at this stage.
- Flat layered project (controllers/services/data): no enforced boundaries; modules would blur together.
