# ADR 0017: Credential and code storage

Status: accepted (Phase 1)

## Decision

Passwords use Argon2id with OWASP-minimum parameters, a versioned secret pepper applied before hashing, and parameters stored inside each hash so weaker hashes are rehashed on login. A fixed concurrency cap bounds hashing work. One-time codes are six digits from a bias-free generator, stored only as keyed hashes bound to purpose, owner and code id, compared in constant time.

## Consequences

Database leaks do not reveal passwords or codes. Pepper rotation is supported by the version id. Hashing cannot be used as a cheap denial-of-service lever.
