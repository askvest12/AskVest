#!/usr/bin/env bash
# Phase 1 audit row 7: prove no secrets reach application logs.
set -u
API="${ASKVEST_SMOKE_BASE_URL:-http://127.0.0.1:5082}"
MAIL="${ASKVEST_SMOKE_MAILPIT_URL:-http://127.0.0.1:18026}"
CONTAINER="${ASKVEST_SMOKE_API_CONTAINER:-askvest-smoke-api-1}"
OUT="docs/verification/phase-1-log-hygiene.log"

EMAIL="loghygiene-$(date +%s)-$$@example.com"
PASSWORD='LogHygiene!Pass#42'
DEVICE="loghygiene-dev-$$"

SINCE=$(date -u +%Y-%m-%dT%H:%M:%SZ)
sleep 1

post() {
  curl -sS -X POST "$API$1" -H 'Content-Type: application/json' \
    -H "Idempotency-Key: loghygiene-$(date +%s%N)-$RANDOM" -d "$2"
}

json_field() { # json field
  echo "$1" | grep -o "\"$2\":\"[^\"]*\"" | head -1 | cut -d'"' -f4
}

latest_code() { # -> newest code addressed to $EMAIL
  local ids id body
  ids=$(curl -sS "$MAIL/api/v1/messages?limit=10")
  for id in $(echo "$ids" | grep -o '"ID":"[^"]*"' | sed 's/"ID":"//;s/"//'); do
    body=$(curl -sS "$MAIL/api/v1/message/$id")
    if echo "$body" | grep -q "$EMAIL"; then
      echo "$body" | grep -oE 'code is [0-9]{6}' | head -1 | grep -oE '[0-9]{6}'
      return 0
    fi
  done
  return 1
}

DEVICE_JSON="\"device\":{\"installationId\":\"$DEVICE\",\"platform\":\"android\",\"deviceName\":\"LogHygiene\"}"

{
  echo "==================================================================="
  echo "AskVest Phase 1 - log hygiene evidence (audit row 7)"
  echo "Captured: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "Target:   $API   Container: $CONTAINER"
  echo "Secrets used: unique email, password, 6-digit codes, JWT pair,"
  echo "refresh token. Values are NOT reproduced in this file."
  echo "==================================================================="
  echo
  echo "### Flow executed"
  echo "sign-up: $(post /api/v1/auth/sign-up "{\"email\":\"$EMAIL\",\"password\":\"$PASSWORD\"}" | head -c 200)"
  VCODE=$(latest_code)
  echo "verify code retrieved from Mailpit: <redacted> (${#VCODE} digits)"
  echo "verify-email: $(post /api/v1/auth/verify-email "{\"email\":\"$EMAIL\",\"code\":\"$VCODE\"}" | head -c 200)"
  LOGIN=$(post /api/v1/auth/login "{\"email\":\"$EMAIL\",\"password\":\"$PASSWORD\",$DEVICE_JSON}")
  echo "login result field: $(json_field "$LOGIN" result)"
  AT=$(json_field "$LOGIN" accessToken)
  RT=$(json_field "$LOGIN" refreshToken)
  REFRESH=$(post /api/v1/auth/refresh "{\"refreshToken\":\"$RT\",\"installationId\":\"$DEVICE\"}")
  echo "refresh result field: $(json_field "$REFRESH" result)"
  AT2=$(json_field "$REFRESH" accessToken)
  RT2=$(json_field "$REFRESH" refreshToken)
  echo "wrong-password login: $(post /api/v1/auth/login "{\"email\":\"$EMAIL\",\"password\":\"WrongPass999\",$DEVICE_JSON}" | head -c 200)"
  echo
  echo "### Grepping container logs since $SINCE for each secret"
  sleep 2
  LOGS=$(docker logs "$CONTAINER" --since "$SINCE" 2>&1)
  TOTAL=$(echo "$LOGS" | wc -l)
  echo "log lines captured: $TOTAL"
  FAIL=0
  check() { # label value
    if [ -z "$2" ]; then echo "$1 SKIPPED (empty)"; FAIL=1; return; fi
    local n
    n=$(echo "$LOGS" | grep -cF "$2" || true)
    echo "$1 occurrences in logs: $n"
    [ "$n" -eq 0 ] || FAIL=1
  }
  check "email address      " "$EMAIL"
  check "account password   " "$PASSWORD"
  check "verify code        " "$VCODE"
  check "access token (1)   " "$AT"
  check "refresh token (1)  " "$RT"
  check "access token (2)   " "$AT2"
  check "refresh token (2)  " "$RT2"
  echo
  if [ "$FAIL" -eq 0 ]; then
    echo "RESULT: PASS - no secret material appears in application logs."
  else
    echo "RESULT: FAIL - see counts above (empty secret or leak)."
  fi
  echo "==================================================================="
} | tee "$OUT"
