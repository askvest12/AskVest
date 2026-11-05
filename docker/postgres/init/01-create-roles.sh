#!/usr/bin/env bash
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v owner_password="$ASKVEST_OWNER_PASSWORD" \
  -v app_password="$ASKVEST_APP_PASSWORD" <<'SQL'
CREATE ROLE askvest_owner LOGIN PASSWORD :'owner_password';
CREATE ROLE askvest_app LOGIN PASSWORD :'app_password'
  NOSUPERUSER NOCREATEDB NOCREATEROLE;

ALTER ROLE askvest_app SET statement_timeout = '15s';
ALTER ROLE askvest_app SET lock_timeout = '5s';
ALTER ROLE askvest_app SET idle_in_transaction_session_timeout = '30s';

REVOKE ALL ON DATABASE :"DBNAME" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"DBNAME" TO askvest_owner, askvest_app;
GRANT CREATE ON DATABASE :"DBNAME" TO askvest_owner;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
SQL
