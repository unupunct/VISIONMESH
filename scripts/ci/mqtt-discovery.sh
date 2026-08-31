#!/usr/bin/env bash
#
# Checks VisionMesh's MQTT discovery against a real mosquitto broker.
#
# A broker is the whole point here. Everything worth getting wrong in MQTT discovery lives in the
# broker's behaviour rather than in the publishing code: whether retained configuration topics
# actually persist for a subscriber that connects later, whether a subscriber sees the state topic
# at all, whether a command published by Home Assistant reaches the server, and whether the last
# will fires when the server dies. None of that can be checked by asserting on the payloads the
# code builds.
#
# Intended for CI. Everything lives under /tmp and every process is killed on exit.

set -euo pipefail

PORT=18141
BASE="http://127.0.0.1:${PORT}"
DATA=/tmp/vm-mqtt-data
PASSWORD='MqttCheck!2026'
BROKER=127.0.0.1

cleanup() {
    pkill -f "VisionMesh.Server" 2>/dev/null || true
    pkill -f "mosquitto_sub" 2>/dev/null || true
}
trap cleanup EXIT

fail() { echo "::error::$*"; exit 1; }

rm -rf "$DATA"; mkdir -p "$DATA"

# ---- broker ----

mosquitto_pub -h "$BROKER" -t visionmesh/ci/ping -m up -q 1 \
    || fail "No MQTT broker is listening on ${BROKER}:1883."
echo "broker is up"

# Subscribe before the server connects, so the retained-message behaviour is exercised the way
# Home Assistant experiences it rather than by racing the publisher.
mosquitto_sub -h "$BROKER" -t 'homeassistant/#' -t 'visionmesh/#' -v > /tmp/mqtt-messages.txt &
sleep 1

# ---- server ----

VISIONMESH_DATA="$DATA" VISIONMESH_PORT="$PORT" \
    dotnet run --no-build -c Release \
    --project server/VisionMesh.Server/VisionMesh.Server.csproj \
    > /tmp/vm-mqtt-server.log 2>&1 &

for _ in $(seq 1 60); do
    curl -sf "${BASE}/api/setup/status" > /dev/null 2>&1 && break
    sleep 1
done
curl -sf "${BASE}/api/setup/status" > /dev/null || fail "The server never started listening."

curl -sf -X POST "${BASE}/api/setup" -H 'Content-Type: application/json' \
    -d "{\"serverName\":\"MQTT CI\",\"adminUsername\":\"admin\",\"adminPassword\":\"${PASSWORD}\",\"recordingsPath\":\"${DATA}/recordings\",\"retentionDays\":1}" \
    > /dev/null || fail "Setup failed."

TOKEN=$(curl -sf -X POST "${BASE}/api/auth/login" -H 'Content-Type: application/json' \
    -d "{\"username\":\"admin\",\"password\":\"${PASSWORD}\"}" | jq -r .token)
[ -n "$TOKEN" ] && [ "$TOKEN" != "null" ] || fail "Could not sign in."

# A camera has to exist for there to be anything to announce. MQTT carries state only, so an
# RTSP camera that will never connect is enough: what is being checked is the discovery and state
# plumbing, not video.
CAMERA=$(curl -sf -X POST "${BASE}/api/cameras" -H 'Content-Type: application/json' \
    -H "Authorization: Bearer ${TOKEN}" \
    -d '{"name":"Front Door","sourceKind":"Rtsp","rtspUrl":"rtsp://192.0.2.1:554/stream"}' \
    | jq -r .id)
[ -n "$CAMERA" ] && [ "$CAMERA" != "null" ] || fail "Could not add a camera."
echo "camera added: ${CAMERA}"

OBJECT_ID=$(printf '%s' "$CAMERA" | tr -c '[:alnum:]' '_' | tr '[:upper:]' '[:lower:]')
echo "expected MQTT object id: ${OBJECT_ID}"

# ---- turn MQTT on through the API, as a user would ----

curl -sf -X POST "${BASE}/api/homeassistant" -H 'Content-Type: application/json' \
    -H "Authorization: Bearer ${TOKEN}" \
    -d "{\"mqttEnabled\":true,\"mqttHost\":\"${BROKER}\",\"mqttPort\":1883,\"mqttDiscoveryPrefix\":\"homeassistant\"}" \
    > /dev/null || fail "Could not enable MQTT."

echo "waiting for the server to connect and publish"
for _ in $(seq 1 40); do
    grep -q "^visionmesh/status online" /tmp/mqtt-messages.txt 2>/dev/null && break
    sleep 1
done

echo "--- what the server reports about its own MQTT link ---"
curl -sf -H "Authorization: Bearer ${TOKEN}" "${BASE}/api/homeassistant" | jq '{mqtt}' || true

grep -q "^visionmesh/status online" /tmp/mqtt-messages.txt \
    || { cat /tmp/mqtt-messages.txt; fail "The server never announced itself as online."; }
echo "availability: online"

# ---- discovery ----

python3 - "$OBJECT_ID" <<'PY'
import json, sys

object_id = sys.argv[1]
messages = {}
for line in open('/tmp/mqtt-messages.txt', encoding='utf-8', errors='replace'):
    topic, _, payload = line.rstrip('\n').partition(' ')
    if topic:
        messages[topic] = payload

configs = {t: p for t, p in messages.items() if t.startswith('homeassistant/') and t.endswith('/config')}
print(f'discovery configs seen: {len(configs)}')
for topic in sorted(configs):
    print('  ', topic)

if not configs:
    sys.exit('No discovery configuration topics were published at all.')

# Home Assistant reads these as JSON and silently ignores an entity whose config will not parse.
for topic, payload in configs.items():
    try:
        entity = json.loads(payload)
    except json.JSONDecodeError as error:
        sys.exit(f'{topic} is not valid JSON: {error}')

    for required in ('unique_id', 'state_topic', 'device'):
        if required not in entity:
            sys.exit(f'{topic} has no {required}, so Home Assistant cannot use it.')

    # The unique id is what keeps an entity attached to its camera across renames and address
    # changes. Anything derived from something mutable would orphan it.
    if not entity['unique_id'].startswith('visionmesh_'):
        sys.exit(f"{topic} has unique_id {entity['unique_id']!r}, which is not derived from the camera id.")

expected = f'homeassistant/binary_sensor/visionmesh/{object_id}_online/config'
if expected not in configs:
    sys.exit(f'Expected an online binary_sensor at {expected}.')

state_topic = f'visionmesh/{object_id}/state'
if state_topic not in messages:
    sys.exit(f'No state was published on {state_topic}.')

state = json.loads(messages[state_topic])
print('state payload:', json.dumps(state, indent=None))
for field in ('state', 'online', 'recording', 'privacy'):
    if field not in state:
        sys.exit(f'The state payload has no {field!r}; the entity templates read it.')

if state['privacy'] is not False:
    sys.exit(f"Privacy should start off, got {state['privacy']!r}.")

print('discovery and state look right')
PY

# ---- a command coming back from Home Assistant ----

# This is the direction that cannot be checked without a broker: a message published by something
# else has to arrive at the server and change real state.
mosquitto_pub -h "$BROKER" -t "visionmesh/${OBJECT_ID}/set" -m privacy_on -q 1

PRIVACY=false
for _ in $(seq 1 20); do
    PRIVACY=$(curl -sf -H "Authorization: Bearer ${TOKEN}" "${BASE}/api/cameras/${CAMERA}" | jq -r '.privacyMode')
    [ "$PRIVACY" = "true" ] && break
    sleep 1
done
[ "$PRIVACY" = "true" ] || fail "Publishing privacy_on did not turn privacy mode on."
echo "command round trip: privacy_on took effect"

mosquitto_pub -h "$BROKER" -t "visionmesh/${OBJECT_ID}/set" -m privacy_off -q 1
for _ in $(seq 1 20); do
    PRIVACY=$(curl -sf -H "Authorization: Bearer ${TOKEN}" "${BASE}/api/cameras/${CAMERA}" | jq -r '.privacyMode')
    [ "$PRIVACY" = "false" ] && break
    sleep 1
done
[ "$PRIVACY" = "false" ] || fail "Publishing privacy_off did not turn privacy mode off."
echo "command round trip: privacy_off took effect"

# ---- the last will ----

# If the server dies without saying goodbye, the broker has to announce it. Without this, Home
# Assistant keeps showing every entity's last known value forever, which is worse than showing
# them as unavailable: the dashboard looks fine while the cameras are gone.
echo "killing the server without letting it disconnect"
pkill -9 -f "VisionMesh.Server" || true

OFFLINE=no
for _ in $(seq 1 60); do
    if tail -20 /tmp/mqtt-messages.txt | grep -q "^visionmesh/status offline"; then
        OFFLINE=yes
        break
    fi
    sleep 1
done

[ "$OFFLINE" = yes ] || {
    echo "--- last messages seen ---"; tail -20 /tmp/mqtt-messages.txt
    fail "The broker never published the last will, so Home Assistant would show stale state forever."
}
echo "last will fired: entities would go unavailable"

echo "MQTT discovery works against a real broker."
