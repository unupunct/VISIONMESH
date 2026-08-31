#!/usr/bin/env bash
#
# Drives a real Linux agent against a real VisionMesh server and checks that video from a V4L2
# device comes out of the server's stream endpoint.
#
# The point is the whole chain, on Linux, with the kernel in it: the agent opens /dev/video0
# through its own ioctl interop, negotiates a format, maps buffers, and forwards frames over the
# WebSocket; the server routes them onto the frame bus and out as MJPEG. Anything short of reading
# JPEGs off that endpoint would leave the interop unproven.
#
# Intended for CI. Every path is under /tmp and every process is killed on exit.

set -euo pipefail

PORT=18131
BASE="http://127.0.0.1:${PORT}"
DATA=/tmp/vm-data
CONFIG=/tmp/vm-agent.json
PASSWORD='LinuxAgentCheck!2026'

cleanup() {
    pkill -f "VisionMesh.Agent.Linux" 2>/dev/null || true
    pkill -f "VisionMesh.Server" 2>/dev/null || true
}
trap cleanup EXIT

fail() { echo "::error::$*"; exit 1; }

rm -rf "$DATA" "$CONFIG"
mkdir -p "$DATA"

# ---- server ----

VISIONMESH_DATA="$DATA" VISIONMESH_PORT="$PORT" \
    dotnet run --no-build -c Release \
    --project server/VisionMesh.Server/VisionMesh.Server.csproj \
    > /tmp/vm-server.log 2>&1 &

for _ in $(seq 1 60); do
    curl -sf "${BASE}/api/setup/status" > /dev/null 2>&1 && break
    sleep 1
done
curl -sf "${BASE}/api/setup/status" > /dev/null || fail "The server never started listening."
echo "server is up"

curl -sf -X POST "${BASE}/api/setup" -H 'Content-Type: application/json' \
    -d "{\"serverName\":\"Linux CI\",\"adminUsername\":\"admin\",\"adminPassword\":\"${PASSWORD}\",\"recordingsPath\":\"${DATA}/recordings\",\"retentionDays\":1}" \
    > /dev/null || fail "Setup failed."

TOKEN=$(curl -sf -X POST "${BASE}/api/auth/login" -H 'Content-Type: application/json' \
    -d "{\"username\":\"admin\",\"password\":\"${PASSWORD}\"}" | jq -r .token)
[ -n "$TOKEN" ] && [ "$TOKEN" != "null" ] || fail "Could not sign in."

CODE=$(curl -sf -X POST "${BASE}/api/pairing" -H 'Content-Type: application/json' \
    -H "Authorization: Bearer ${TOKEN}" -d '{"label":"Linux CI agent"}' | jq -r .code)
[ -n "$CODE" ] && [ "$CODE" != "null" ] || fail "Could not create a pairing code."
echo "pairing code issued"

# ---- agent ----

dotnet run --no-build -c Release \
    --project agents/linux/VisionMesh.Agent.Linux/VisionMesh.Agent.Linux.csproj \
    -- pair --config "$CONFIG" --server "$BASE" --code "$CODE" --name "Linux CI agent" \
    || fail "Pairing failed."

dotnet run --no-build -c Release \
    --project agents/linux/VisionMesh.Agent.Linux/VisionMesh.Agent.Linux.csproj \
    -- --config "$CONFIG" --verbose > /tmp/vm-agent.log 2>&1 &

DEVICE=""
for _ in $(seq 1 45); do
    DEVICE=$(curl -sf -H "Authorization: Bearer ${TOKEN}" "${BASE}/api/devices" \
        | jq -r '[.[] | select(.connected == true)][0].id // empty')
    [ -n "$DEVICE" ] && break
    sleep 1
done
[ -n "$DEVICE" ] || fail "The agent never showed up as connected."
echo "agent connected as ${DEVICE}"

SOURCE=$(curl -sf -H "Authorization: Bearer ${TOKEN}" "${BASE}/api/devices/${DEVICE}/cameras" \
    | jq -r '.[0].sourceId // empty')
[ -n "$SOURCE" ] || fail "The agent advertised no cameras to the server."
echo "camera source: ${SOURCE}"

CAMERA=$(curl -sf -X POST "${BASE}/api/cameras" -H 'Content-Type: application/json' \
    -H "Authorization: Bearer ${TOKEN}" \
    -d "$(jq -nc --arg s "$SOURCE" --arg d "$DEVICE" \
        '{name:"Loopback camera", sourceKind:"AgentCamera", deviceId:$d, sourceId:$s, width:1280, height:720, fps:15, quality:80}')" \
    | jq -r .id)
[ -n "$CAMERA" ] && [ "$CAMERA" != "null" ] || fail "Could not add the camera."
echo "camera added: ${CAMERA}"

# ---- the actual question: does video come out? ----

# Capture a few seconds of the MJPEG stream. curl is stopped by its own timeout, which is a
# success here, so the exit code is deliberately ignored and the bytes are what get judged.
curl -s --max-time 20 -D /tmp/stream.headers -H "Authorization: Bearer ${TOKEN}" \
    "${BASE}/api/cameras/${CAMERA}/stream.mjpeg" -o /tmp/stream.bin || true

echo "--- stream response headers ---"
head -5 /tmp/stream.headers 2>/dev/null || echo "(none)"

SIZE=$(stat -c%s /tmp/stream.bin 2>/dev/null || echo 0)
echo "captured ${SIZE} bytes from the stream endpoint"
if [ "$SIZE" -le 20000 ]; then
    echo "--- agent log ---"; tail -40 /tmp/vm-agent.log 2>/dev/null || true
    echo "--- server log, capture and streaming only ---"
    grep -iE "captur|stream|supervis|camera|v4l2|frame" /tmp/vm-server.log 2>/dev/null | tail -40 || true
    fail "The stream produced ${SIZE} bytes, so no real video arrived."
fi

# Bytes are not frames. Count actual JPEG start-of-image markers, and confirm the payload is a
# multipart MJPEG stream rather than an error page that happens to be long.
python3 - <<'PY'
import re, sys

data = open('/tmp/stream.bin', 'rb').read()

if b'multipart' not in data[:400].lower() and b'--' not in data[:400]:
    sys.exit(f'Not a multipart stream. First bytes: {data[:200]!r}')

frames = data.count(b'\xff\xd8\xff')
print(f'JPEG start-of-image markers: {frames}')
if frames < 5:
    sys.exit(f'Only {frames} JPEG frames in {len(data)} bytes.')

start = data.find(b'\xff\xd8\xff')
end = data.find(b'\xff\xd9', start)
if end == -1:
    sys.exit('No complete JPEG in the stream.')

jpeg = data[start:end + 2]

# Read the frame's real dimensions out of its SOF marker, so this proves a decodable image at the
# size that was asked for rather than just plausible-looking bytes.
i, width, height = 2, None, None
while i < len(jpeg) - 1:
    if jpeg[i] != 0xFF:
        i += 1
        continue
    marker = jpeg[i + 1]
    if marker in (0xD8, 0x01) or 0xD0 <= marker <= 0xD7:
        i += 2
        continue
    length = int.from_bytes(jpeg[i + 2:i + 4], 'big')
    if marker in (0xC0, 0xC1, 0xC2):
        height = int.from_bytes(jpeg[i + 5:i + 7], 'big')
        width = int.from_bytes(jpeg[i + 7:i + 9], 'big')
        break
    i += 2 + length

if not width or not height:
    sys.exit('Could not read the frame size: the JPEG has no SOF marker.')

print(f'first frame: {width}x{height}, {len(jpeg)} bytes')
if (width, height) != (1280, 720):
    sys.exit(f'Expected 1280x720, got {width}x{height}.')
PY

echo "Linux agent captured real frames from a real V4L2 device."
