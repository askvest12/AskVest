# Backlog

- Ledger: fee, spread, FX and trade journal patterns land with trading (Phase 10); adjustment journals need an audited caller before use.
- Ledger: outbox handler metrics and dead-letter inspection UI (Phase 15).
- Social login, passkeys, TOTP authenticator apps.
- Email change flow (high-risk takeover path; needs its own design with codes to old and new addresses).
- Field-level encryption of personal data (Phase 16).
- Device attestation, CAPTCHA, IP reputation scoring.
- Notification preferences and push channels (Phase 13).
- Admin and support tooling, disabled-status workflow (Phase 15).
- Real SMS provider integration; phone verification stays feature-flagged until then.
- Session validation cache with measurements (Phase 17).
- Catalog: real issuers, chains and tokens land as data files after provider research; production data stays empty until then.
- Catalog: a second chain family handler (for example `svm` or `move`) ships with its data files when research chooses one; no schema change needed.
- Catalog: automated issuer sync waits for research; imports are manual reviewed data files for now.
- Catalog: admin create, edit, list and delist endpoints reuse the importer and listing guard (Phase 15).
- Portfolio: cross-chain aggregation of tokens per instrument (Phase 8).
