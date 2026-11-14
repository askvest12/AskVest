# AskVest Backend

AI-assisted investing in tokenized stocks and ETFs for Africans. Modular monolith, .NET 10, ASP.NET Core minimal APIs, EF Core, PostgreSQL 17.

## Standing rules

1. **Nothing is assumed to work.** A feature is done only when an automated test proves it and, for anything exposed over HTTP, a real HTTP call against the running containers proves it too.
2. **Warnings are errors.** No `#pragma warning disable`, no `[SuppressMessage]`. The only allowed suppressions live in `.editorconfig`, each with a one-line reason on the line above it.
3. **Money is `decimal`.** Never `float` or `double`. In JSON, money and quantities are strings.
4. **Every state-changing endpoint is idempotent.** It requires an `Idempotency-Key` or is explicitly exempt with a written reason. Money-moving commands also persist the key under a unique constraint in the same database transaction as their effects.
5. **One response shape.** Every response, including framework errors, uses the envelope in `docs/design/01-api-standards.md`. Clients branch on `error.code`.
6. **Time and identity.** Use `TimeProvider`, UTC only, `Guid.CreateVersion7()` for ids. `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.UtcNow`, `Guid.NewGuid()` and `System.Random` are banned (enforced by `BannedSymbols.txt`).
7. **No secrets in the repo, images or logs.** Never log request bodies, query strings, headers or personal data. Log route templates, not raw paths.
8. **Comments explain why, never what.** One or two direct sentences. No commented-out code. No `TODO` or `FIXME`: record follow-ups in `docs/backlog.md`.
9. **Names are descriptive.** Full words, no abbreviations beyond `Id`, `Utc` and `Kyc`. One type per file. Types are `internal sealed` unless another project needs them.
10. **Keep it simple.** No generic repository, no MediatR, no AutoMapper, no speculative abstraction. Abstractions exist only at external provider boundaries and where a test needs a seam.
11. **Expected failures are `Result` values. Exceptions mean something unexpected happened.** Every async method accepts and forwards a `CancellationToken`.
12. **Database.** Forward-only migrations. Never edit an applied migration. The API connects with a least-privilege role and never runs DDL.
13. **Dependencies.** Central versions, lock files, audit on, every package justified in ADR 0013. No prerelease packages.
14. **Every step ends with** a clean build, passing tests and a Conventional Commit.
15. **Be honest in reports.** If something could not be verified, say so.
16. **No em dashes** in code, comments, docs or commit messages.
17. **Default-deny authorization.** Every endpoint requires an authenticated active account unless it is on the anonymous allow-list in `docs/design/08-identity-module.md` with a written reason. Guarded by an architecture test.
18. **Ownership filtering.** Every query for a user-owned resource filters by owner. Another user's resource answers `resource.not_found`, never `auth.forbidden`.
19. **Secrets never enter idempotency storage.** Endpoints returning tokens, codes or grants carry the returns-secrets tag and are exempt from replay storage; the tag and the idempotency requirement never combine.
20. **Modules follow `docs/design/09-module-blueprint.md`.** One project per module under `src/Modules`, contracts namespace only, own schema and history table.
21. **Route groups `/practice` and `/real` carry the eligibility policies.** Every endpoint under `/api/v1/practice` requires `PracticeEligible` and every endpoint under `/api/v1/real` requires `RealEligible`; nothing may bypass them.
22. **Ledger integrity.** Only the Ledger module touches ledger tables. Ledger writes happen only inside a transaction scope; the scope commits explicitly or rolls back everything, including outbox messages. Amounts, balances and account ids are never logged; outbox payloads carry ids only.
23. **Market data honesty.** Consumers must honor `usableForExecution` and must never execute on `stale`, `halted` or `unavailable` prices. Latest-price request handlers never call the provider; only the poller does. Prices and candles serialize as strings and are never rounded silently.
24. **Catalog integrity.** Only the Catalog module writes catalog tables. Chain-specific logic lives only in a chain family handler. The repository carries no real catalog data; production catalog data stays empty until provider research completes.
25. **Portfolio is read-only.** The portfolio stores no money facts; it replays ledger movements on demand for cost basis and profit. Holdings must equal ledger balances. Watchlist is the only table it owns.
26. **Practice isolation.** Practice code never references the real book; practice value lives only in `Book.Practice`. Practice orders never use a price that is not `usableForExecution`. Practice ledger asset codes start with `P.`, a prefix reserved for practice: the Catalog importer rejects any real token asset code starting with it.
27. **Learning stays educational.** Learning never references money, practice or trading code and touches no ledger, market data or funding contracts. Streaks count learning activity only. Lesson content is plain, chain-agnostic, issuer-neutral, carries no advice or promises, and must pass the banned-phrase lint.
28. **Assistant never advises, trades or moves money.** It has no write tools at all. Tools never take a user id; the authenticated session scopes every read. Never log message text, tool results, amounts or portfolio data. Every model output passes the server-side citation, numeric-grounding, policy and sanitization checks before a user sees it.
29. **Custody holds no keys.** No private key, seed phrase or signing material exists in the database, configuration, logs or process; signing happens only inside a custody provider, KMS, HSM or MPC service behind the custody interface, and an adapter that signs locally is rejected. No user-facing text claims "non-custodial", "self-custody" or "your keys". Funds are never released on uncertainty: holds stay until a definitive provider answer, and `needs_reconciliation` always preserves them. The Wallets module contains no chain-specific code; chains are catalog ids and addresses are validated through the Catalog.
30. **Real orders are quote-bound and grant-authorized.** No real order without a firm quote and a PIN step-up grant bound to that quote's terms hash. Never release held funds on uncertainty; a fill must equal its quote exactly or the order waits in reconciliation. The Trading module contains no chain-specific code, and the assistant gets no write path into it.

## Solution layout

- `src/AskVest.SharedKernel`: `Result`, `Error`, pagination and cursor primitives. Zero package references.
- `src/AskVest.Platform`: PostgreSQL access, role model, migration runner, idempotency store.
- `src/AskVest.Api`: the host. Envelope, error handling, logging, limits, health, OpenAPI, idempotency middleware.
- `src/AskVest.Migrator`: one-shot container applying migrations before the API starts.
- `tests/`: `AskVest.UnitTests`, `AskVest.ArchitectureTests`, `AskVest.IntegrationTests` (real PostgreSQL via Testcontainers), `AskVest.SmokeTests` (real containers over real HTTP).
- `docs/design/`: system design. `docs/adr/`: architecture decision records. `docs/verification/`: evidence.

## Commands

- Build: `dotnet build`
- Test all layers: `dotnet test --solution AskVest.slnx` (smoke tests skip unless `ASKVEST_SMOKE_BASE_URL` is set)
- Run API locally: `dotnet run --project src/AskVest.Api` (requires `ConnectionStrings__Platform`)
- Full stack: `docker compose up -d --build --wait` (probes on: add `-f docker-compose.smoke.yml`)
- Smoke tests: `ASKVEST_SMOKE_BASE_URL=http://localhost:${API_HOST_PORT} dotnet test --project tests/AskVest.SmokeTests`
- Full local verify: `./scripts/verify.sh`

## Definition of Done for any feature

Code complete; unit tests; integration tests against real PostgreSQL; for every endpoint a smoke test over real HTTP against the running containers; negative and abuse cases covered (invalid input, wrong owner, replay, concurrency); zero warnings; docs and OpenAPI updated; evidence recorded.

## Documentation rule

At the start of every phase read `CLAUDE.md` and `docs/design/*` first, and update the docs in the same commit as any change that contradicts them.
