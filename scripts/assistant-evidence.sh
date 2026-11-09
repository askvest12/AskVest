#!/usr/bin/env bash
# Raw HTTP evidence for Phase 9 assistant verification.
# Usage: bash scripts/assistant-evidence.sh > docs/verification/phase-9-raw-http.log 2>&1
set -u
BASE="${ASKVEST_SMOKE_BASE_URL:-http://localhost:5081}"
MAILPIT="${ASKVEST_SMOKE_MAILPIT_URL:-http://localhost:18025}"
PASS="CorrectHorseSmoke9"
EMAIL="evidence-$(date +%s)@smoke.test"
J='-H Content-Type:application/json'

echo "=== Phase 9 assistant raw HTTP evidence ==="
echo "base: $BASE  mailpit: $MAILPIT  at $(date -u +%FT%TZ)"

req() { # method path [json] [token] [key]
  local m="$1" p="$2" body="${3:-}" tok="${4:-}" key="${5:-}"
  local args=(-s -o /tmp/askvest-resp.json -w '%{http_code}' -X "$m" "$BASE$p" $J)
  [ -n "$body" ] && args+=(-d "$body")
  [ -n "$tok" ] && args+=(-H "Authorization: Bearer $tok")
  [ -n "$key" ] && args+=(-H "Idempotency-Key: $key")
  local code; code=$(curl "${args[@]}")
  echo ">>> $m $p${key:+  Idempotency-Key: $key}"
  [ -n "$body" ] && echo ">>> body: $body"
  echo "<<< $code"
  head -c 1200 /tmp/askvest-resp.json; echo; echo
}

req POST /api/v1/auth/sign-up "{\"email\":\"$EMAIL\",\"password\":\"$PASS\"}" "" "ev-signup-1"
sleep 2
CODE=$(curl -s "$MAILPIT/api/v1/messages?limit=50" | python3 -c "
import json,sys
msgs=json.load(sys.stdin)['messages']
for m in msgs:
    if any(t['Address']=='$EMAIL' for t in m['To']) and 'Verify' in m.get('Subject',''):
        print(m['ID']); break
")
BODY=$(curl -s "$MAILPIT/api/v1/message/$CODE" | python3 -c "import json,sys,re; print(re.search(r'code is (\d{6})', json.load(sys.stdin)['Text']).group(1))")
echo ">>> verify code for $EMAIL: $BODY"
req POST /api/v1/auth/verify-email "{\"email\":\"$EMAIL\",\"code\":\"$BODY\"}" "" "ev-verify-1"
LOGIN=$(curl -s -X POST "$BASE/api/v1/auth/login" $J -H "Idempotency-Key: ev-login-1" -d "{\"email\":\"$EMAIL\",\"password\":\"$PASS\",\"device\":{\"installationId\":\"dev-evidence-1\",\"platform\":\"android\",\"deviceName\":\"Evidence\",\"appVersion\":\"1.0.0\"}}")
TOKEN=$(echo "$LOGIN" | python3 -c "import json,sys; print(json.load(sys.stdin)['data']['accessToken'])")
echo ">>> login ok, token acquired"

# documents + accept practice and assistant docs
DOCS=$(curl -s "$BASE/api/v1/compliance/documents" -H "Authorization: Bearer $TOKEN")
ITEMS=$(echo "$DOCS" | python3 -c "
import json,sys
d=json.load(sys.stdin)['data']['documents']
print(json.dumps([{'type':x['type'],'version':x['version'],'sha256':x['sha256']} for x in d if not x['accepted'] and x['requiredFor'] in ('practice','assistant')]))")
req POST /api/v1/compliance/consents "{\"documents\":$ITEMS}" "$TOKEN" "ev-consent-1"
req PUT /api/v1/compliance/country '{"country":"NG"}' "$TOKEN" "ev-country-1"

# unauthenticated and consent-gate probes happen before assistant consent? consent already accepted above; do unauth check
req POST /api/v1/assistant/messages '{"text":"hello"}' "" "ev-unauth-1"

req POST /api/v1/assistant/messages '{"text":"What is a tokenized stock?"}' "$TOKEN" "ev-msg-1"
req POST /api/v1/assistant/messages '{"text":"What is the latest price for sample-aapl?"}' "$TOKEN" "ev-msg-2"
req GET "/api/v1/marketdata/prices?instrumentIds=sample-aapl" "" "$TOKEN"
req GET /api/v1/assistant/conversations "" "$TOKEN"
CID=$(python3 -c "import json; print(json.load(open('/tmp/askvest-resp.json'))['data']['items'][0]['id'])" 2>/dev/null || true)
[ -n "$CID" ] && req GET "/api/v1/assistant/conversations/$CID" "" "$TOKEN"
# replay identical key
req POST /api/v1/assistant/messages '{"text":"What is a tokenized stock?"}' "$TOKEN" "ev-msg-1"
# scenario probes
req POST /api/v1/diagnostics/assistant-scenario '{"scenario":"advice_phrase"}' "$TOKEN" "ev-sc-1"
req POST /api/v1/assistant/messages '{"text":"probe"}' "$TOKEN" "ev-sc-msg-1"
req POST /api/v1/diagnostics/assistant-scenario '{"scenario":"error"}' "$TOKEN" "ev-sc-2"
req POST /api/v1/assistant/messages '{"text":"probe"}' "$TOKEN" "ev-sc-msg-2"
req POST /api/v1/diagnostics/assistant-scenario '{"scenario":"normal"}' "$TOKEN" "ev-sc-3"
echo "=== done ==="
