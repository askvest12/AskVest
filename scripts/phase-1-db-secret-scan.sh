#!/usr/bin/env bash
# Phase 1 audit row 8: prove no plaintext secrets persist in the identity schema.
# Runs against the smoke PostgreSQL container after the smoke suite has run.
set -u
OUT="docs/verification/phase-1-db-secrets.log"
PSQL="docker exec askvest-smoke-postgres-1 psql -U askvest_admin -d askvest -tA"

{
  echo "==================================================================="
  echo "AskVest Phase 1 - database secret scan (audit row 8)"
  echo "Captured: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "Target:   askvest-smoke-postgres-1 / askvest, schema identity"
  echo "==================================================================="
  echo

  echo "### Row counts per table"
  for t in users sessions devices refresh_tokens one_time_codes step_up_grants throttle_buckets security_events; do
    echo -n "identity.$t: "
    $PSQL -c "SELECT count(*) FROM identity.$t"
  done
  echo

  echo "### password_hash format (must be argon2id$... for every user)"
  $PSQL -c "SELECT CASE WHEN password_hash LIKE 'argon2id\$%' THEN 'argon2id' ELSE 'NON-STANDARD' END AS format, count(*) FROM identity.users GROUP BY 1"
  echo

  echo "### pin_hash format (argon2id or NULL)"
  $PSQL -c "SELECT CASE WHEN pin_hash IS NULL THEN 'null' WHEN pin_hash LIKE 'argon2id\$%' THEN 'argon2id' ELSE 'NON-STANDARD' END AS format, count(*) FROM identity.users GROUP BY 1"
  echo

  echo "### token/code hash formats (keyed hashes are 64-char hex)"
  echo -n "refresh_tokens.token_hash non-hex64: "
  $PSQL -c "SELECT count(*) FROM identity.refresh_tokens WHERE token_hash !~ '^[0-9a-f]{64}$'"
  echo -n "step_up_grants.token_hash non-hex64: "
  $PSQL -c "SELECT count(*) FROM identity.step_up_grants WHERE token_hash !~ '^[0-9a-f]{64}$'"
  echo -n "one_time_codes.code_hash non-hex64: "
  $PSQL -c "SELECT count(*) FROM identity.one_time_codes WHERE code_hash !~ '^[0-9a-f]{64}$'"
  echo

  echo "### plaintext markers in secret columns (all must be 0)"
  echo -n "hash columns containing '@' (email leakage): "
  $PSQL -c "SELECT (SELECT count(*) FROM identity.users WHERE password_hash LIKE '%@%' OR pin_hash LIKE '%@%') + (SELECT count(*) FROM identity.refresh_tokens WHERE token_hash LIKE '%@%') + (SELECT count(*) FROM identity.step_up_grants WHERE token_hash LIKE '%@%') + (SELECT count(*) FROM identity.one_time_codes WHERE code_hash LIKE '%@%')"
  echo -n "one_time_codes holding a bare 6-digit code: "
  $PSQL -c "SELECT count(*) FROM identity.one_time_codes WHERE code_hash ~ '^[0-9]{6}$'"
  echo -n "refresh_tokens holding a bare token-shaped value (< 64 hex): "
  $PSQL -c "SELECT count(*) FROM identity.refresh_tokens WHERE length(token_hash) < 64"
  echo

  echo "### users table: no plaintext password or PIN column exists"
  $PSQL -c "SELECT column_name FROM information_schema.columns WHERE table_schema='identity' AND table_name='users' AND column_name IN ('password','pin','plaintext_password')"
  echo "(empty above = no plaintext columns)"
  echo
  echo "==================================================================="
} | tee "$OUT"
