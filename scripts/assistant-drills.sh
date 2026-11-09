#!/usr/bin/env bash
# Failure drills for Phase 9 assistant verification.
# Usage: bash scripts/assistant-drills.sh > docs/verification/phase-9-drills.log 2>&1
set -u
cd "$(dirname "$0")/.."
BASE="${ASKVEST_SMOKE_BASE_URL:-http://localhost:5081}"
MAILPIT="${ASKVEST_SMOKE_MAILPIT_URL:-http://localhost:18025}"
COMPOSE="docker compose -f docker-compose.yml -f docker-compose.smoke.yml"
PASS="CorrectHorseSmoke9"
EMAIL="drill-$(date +%s)@smoke.test"
J='-H Content-Type:application/json'

echo "=== Phase 9 assistant failure drills ==="
echo "base: $BASE  at $(date -u +%FT%TZ)"

req() {
  local m="$1" p="$2" body="${3:-}" tok="${4:-}" key="${5:-}"
  local args=(-s -o /tmp/askvest-drill.json -w '%{http_code}' -X "$m" "$BASE$p" $J)
  [ -n "$body" ] && args+=(-d "$body")
  [ -n "$tok" ] && args+=(-H "Authorization: Bearer $tok")
  [ -n "$key" ] && args+=(-H "Idempotency-Key: $key")
  local code; code=$(curl "${args[@]}")
  echo ">>> $m $p${key:+  Idempotency-Key: $key}"
  echo "<<< $code"
  head -c 600 /tmp/askvest-drill.json; echo; echo
}

# --- user setup ---
req POST /api/v1/auth/sign-up "{\"email\":\"$EMAIL\",\"password\":\"$PASS\"}" "" "drill-signup"
sleep 2
MID=$(curl -s "$MAILPIT/api/v1/messages?limit=50" | python3 -c "
import json,sys
for m in json.load(sys.stdin)['messages']:
    if any(t['Address']=='$EMAIL' for t in m['To']) and 'Verify' in m.get('Subject',''):
        print(m['ID']); break
")
VCODE=$(curl -s "$MAILPIT/api/v1/message/$MID" | python3 -c "import json,sys,re; print(re.search(r'code is (\d{6})', json.load(sys.stdin)['Text']).group(1))")
req POST /api/v1/auth/verify-email "{\"email\":\"$EMAIL\",\"code\":\"$VCODE\"}" "" "drill-verify"
LOGIN=$(curl -s -X POST "$BASE/api/v1/auth/login" $J -H "Idempotency-Key: drill-login" -d "{\"email\":\"$EMAIL\",\"password\":\"$PASS\",\"device\":{\"installationId\":\"dev-drill-1\",\"platform\":\"android\",\"deviceName\":\"Drill\",\"appVersion\":\"1.0.0\"}}")
TOKEN=$(echo "$LOGIN" | python3 -c "import json,sys; print(json.load(sys.stdin)['data']['accessToken'])")
DOCS=$(curl -s "$BASE/api/v1/compliance/documents" -H "Authorization: Bearer $TOKEN")
ITEMS=$(echo "$DOCS" | python3 -c "
import json,sys
d=json.load(sys.stdin)['data']['documents']
print(json.dumps([{'type':x['type'],'version':x['version'],'sha256':x['sha256']} for x in d if not x['accepted'] and x['requiredFor'] in ('practice','assistant')]))")
req POST /api/v1/compliance/consents "{\"documents\":$ITEMS}" "$TOKEN" "drill-consent"
req PUT /api/v1/compliance/country '{"country":"NG"}' "$TOKEN" "drill-country"
req POST /api/v1/diagnostics/assistant-scenario '{"scenario":"normal"}' "$TOKEN" "drill-sc-normal"
echo "user ready: $EMAIL"

# --- Drill 1: database stopped mid request ---
echo "=== Drill 1: stop postgres, send message, expect service.unavailable ==="
req POST /api/v1/diagnostics/assistant-scenario '{"scenario":"slow"}' "$TOKEN" "drill-sc-slow"
$COMPOSE stop postgres >/dev/null 2>&1 &
STOPPID=$!
sleep 1
req POST /api/v1/assistant/messages '{"text":"drill one"}' "$TOKEN" "drill-msg-db-1"
wait $STOPPID
$COMPOSE start postgres >/dev/null 2>&1
echo "postgres restarting; waiting for health..."
for i in $(seq 1 30); do
  $COMPOSE exec -T postgres pg_isready -U askvest_admin -d askvest >/dev/null 2>&1 && break
  sleep 2
done
$COMPOSE exec -T postgres psql -U askvest_admin -d askvest -t -c \
  "SELECT count(*) FROM assistant.messages m JOIN assistant.conversations c ON c.id=m.conversation_id JOIN identity.users u ON u.id=c.user_id WHERE u.email='$EMAIL';" \
  | sed 's/^/>>> partial messages stored for drill user: /'
req POST /api/v1/diagnostics/assistant-scenario '{"scenario":"normal"}' "$TOKEN" "drill-sc-normal2"

# --- Drill 2: kill api during slow model call ---
echo "=== Drill 2: slow call in flight, kill api, restart, retry same key ==="
req POST /api/v1/diagnostics/assistant-scenario '{"scenario":"slow"}' "$TOKEN" "drill-sc-slow2"
curl -s -o /dev/null -X POST "$BASE/api/v1/assistant/messages" $J \
  -H "Authorization: Bearer $TOKEN" -H "Idempotency-Key: drill-msg-kill" \
  -d '{"text":"drill two slow"}' &
CURLPID=$!
sleep 3
$COMPOSE kill api >/dev/null 2>&1
wait $CURLPID 2>/dev/null
echo ">>> api killed mid-request; restarting"
$COMPOSE up -d api >/dev/null 2>&1
for i in $(seq 1 40); do
  curl -s -o /dev/null "$BASE/health/live" && break
  sleep 2
done
echo ">>> api healthy again; retrying same idempotency key"
for i in 1 2 3 4 5 6; do
  CODE=$(curl -s -o /tmp/askvest-drill.json -w '%{http_code}' -X POST "$BASE/api/v1/assistant/messages" $J \
    -H "Authorization: Bearer $TOKEN" -H "Idempotency-Key: drill-msg-kill" \
    -d '{"text":"drill two slow"}')
  echo "<<< retry $i: $CODE"
  head -c 300 /tmp/askvest-drill.json; echo
  [ "$CODE" = "200" ] && break
  [ "$CODE" = "503" ] && break
  sleep 10
done
$COMPOSE exec -T postgres psql -U askvest_admin -d askvest -t -c \
  "SELECT count(*) FROM assistant.messages m JOIN assistant.conversations c ON c.id=m.conversation_id JOIN identity.users u ON u.id=c.user_id WHERE u.email='$EMAIL';" \
  | sed 's/^/>>> total messages stored for drill user: /'
req POST /api/v1/diagnostics/assistant-scenario '{"scenario":"normal"}' "$TOKEN" "drill-sc-normal3"

# --- Drill 3: kill switch ---
echo "=== Drill 3: Assistant__Enabled=false, restart, expect assistant.unavailable ==="
cat > /tmp/askvest-killswitch.yml <<'EOF'
services:
  api:
    environment:
      Assistant__Enabled: "false"
EOF
$COMPOSE -f /tmp/askvest-killswitch.yml up -d api >/dev/null 2>&1
for i in $(seq 1 40); do
  curl -s -o /dev/null "$BASE/health/live" && break
  sleep 2
done
req POST /api/v1/assistant/messages '{"text":"kill switch check"}' "$TOKEN" "drill-msg-ks"
req GET /api/v1/system/info "" "$TOKEN"
$COMPOSE up -d api >/dev/null 2>&1
for i in $(seq 1 40); do
  curl -s -o /dev/null "$BASE/health/live" && break
  sleep 2
done
echo ">>> assistant re-enabled; sanity check"
req POST /api/v1/assistant/messages '{"text":"back online?"}' "$TOKEN" "drill-msg-back"
echo "=== drills done ==="
