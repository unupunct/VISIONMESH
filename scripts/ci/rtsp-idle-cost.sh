#!/usr/bin/env bash
#
# Measures what a network camera costs when it is only recording.
#
# This exists because of a report rather than a theory. Someone ran three cameras with recording
# and watched a processor that normally idled at 2% sit at 100%, then moved to Frigate, which did
# the same three cameras for 18%. The cause was that the MJPEG transcode was built into the ffmpeg
# command line unconditionally, so every recording camera decoded and re-encoded every frame for
# nobody.
#
# Recording copies the camera's own stream and needs no decode at all. This measures that claim
# instead of asserting it: real RTSP in, real recording out, real processor time read from /proc.
#
# Intended for CI. Everything lives under /tmp and every process is killed on exit.

set -euo pipefail

PORT=18161
BASE="http://127.0.0.1:${PORT}"
DATA=/tmp/vm-cost-data
PASSWORD='CostCheck!2026'
RTSP=rtsp://127.0.0.1:8554/camera

# A recording camera must stay under this share of one core. The transcode it replaced used most
# of a core on its own, so the gap between pass and fail here is not subtle.
BUDGET_PERCENT=25

cleanup() {
    pkill -f "VisionMesh.Server" 2>/dev/null || true
    [ -f /tmp/publisher.pid ] && kill "$(cat /tmp/publisher.pid)" 2>/dev/null || true
    pkill -f mediamtx 2>/dev/null || true
    pkill -f ffmpeg 2>/dev/null || true
}
trap cleanup EXIT

fail() { echo "::error::$*"; exit 1; }

rm -rf "$DATA"; mkdir -p "$DATA"

# ---- a camera to point at ----

# mediamtx is a real RTSP server rather than something improvised out of ffmpeg, which also makes
# the thing VisionMesh talks to an independent implementation.
MEDIAMTX_VERSION=v1.20.1
curl -sfL -o /tmp/mediamtx.tar.gz     "https://github.com/bluenviron/mediamtx/releases/download/${MEDIAMTX_VERSION}/mediamtx_${MEDIAMTX_VERSION}_linux_amd64.tar.gz"     || fail "Could not download mediamtx."
tar -xzf /tmp/mediamtx.tar.gz -C /tmp mediamtx
chmod +x /tmp/mediamtx

# With no configuration mediamtx accepts no paths at all, and refuses a publisher with
# "path 'camera' is not configured".
cat > /tmp/mediamtx.yml <<'YAML'
logLevel: info
rtspAddress: 127.0.0.1:8554
rtmp: no
hls: no
webrtc: no
srt: no
api: no
paths:
  all_others:
YAML

/tmp/mediamtx /tmp/mediamtx.yml > /tmp/mediamtx.log 2>&1 &

# Probe the port rather than matching a log phrase, which changes between versions.
for _ in $(seq 1 30); do
    timeout 1 bash -c 'cat < /dev/null > /dev/tcp/127.0.0.1/8554' 2>/dev/null && break
    sleep 1
done
timeout 1 bash -c 'cat < /dev/null > /dev/tcp/127.0.0.1/8554' 2>/dev/null     || { cat /tmp/mediamtx.log; fail "Nothing is listening on 8554."; }
echo "mediamtx listening on 8554"

# A real H.264 stream, published the way a camera would.
(
    while true; do
        ffmpeg -nostdin -hide_banner -loglevel error             -re -f lavfi -i testsrc2=size=1280x720:rate=15             -c:v libx264 -preset ultrafast -tune zerolatency -g 30 -pix_fmt yuv420p             -f rtsp -rtsp_transport tcp "$RTSP" >> /tmp/publisher.log 2>&1 || true
        sleep 1
    done
) &
echo $! > /tmp/publisher.pid

# Confirm the stream by reading it, which does not depend on anyone's log wording.
PUBLISHED=no
for _ in $(seq 1 30); do
    if ffprobe -v error -rtsp_transport tcp -i "$RTSP"         -show_entries stream=codec_name,width,height -of csv=p=0 > /tmp/probe.txt 2>&1; then
        PUBLISHED=yes
        break
    fi
    sleep 2
done
[ "$PUBLISHED" = yes ]     || { tail -20 /tmp/mediamtx.log; tail -20 /tmp/publisher.log; fail "Nothing is publishing to ${RTSP}."; }
echo "publishing to ${RTSP}: $(cat /tmp/probe.txt)"

# ---- server ----

VISIONMESH_DATA="$DATA" VISIONMESH_PORT="$PORT" \
    dotnet run --no-build -c Release \
    --project server/VisionMesh.Server/VisionMesh.Server.csproj \
    > /tmp/vm-cost-server.log 2>&1 &

for _ in $(seq 1 60); do
    curl -sf "${BASE}/api/setup/status" > /dev/null 2>&1 && break
    sleep 1
done
curl -sf "${BASE}/api/setup/status" > /dev/null || fail "The server never started listening."

curl -sf -X POST "${BASE}/api/setup" -H 'Content-Type: application/json' \
    -d "{\"serverName\":\"Cost CI\",\"adminUsername\":\"admin\",\"adminPassword\":\"${PASSWORD}\",\"recordingsPath\":\"${DATA}/recordings\",\"retentionDays\":1}" \
    > /dev/null || fail "Setup failed."

TOKEN=$(curl -sf -X POST "${BASE}/api/auth/login" -H 'Content-Type: application/json' \
    -d "{\"username\":\"admin\",\"password\":\"${PASSWORD}\"}" | jq -r .token)
[ -n "$TOKEN" ] && [ "$TOKEN" != "null" ] || fail "Could not sign in."

CAMERA=$(curl -sf -X POST "${BASE}/api/cameras" -H 'Content-Type: application/json' \
    -H "Authorization: Bearer ${TOKEN}" \
    -d "{\"name\":\"Cost camera\",\"sourceKind\":\"Rtsp\",\"rtspUrl\":\"${RTSP}\",\"transport\":\"Tcp\",\"width\":1280,\"height\":720,\"fps\":15,\"quality\":75}" \
    | jq -r .id)
[ -n "$CAMERA" ] && [ "$CAMERA" != "null" ] || fail "Could not add the camera."

curl -sf -X PATCH "${BASE}/api/cameras/${CAMERA}" -H 'Content-Type: application/json' \
    -H "Authorization: Bearer ${TOKEN}" -d '{"recordingMode":"Continuous"}' > /dev/null \
    || fail "Could not turn recording on."
echo "camera ${CAMERA} added and recording continuously, with nobody watching"

# ---- find VisionMesh's own ffmpeg ----

VM_FFMPEG=""
for _ in $(seq 1 60); do
    VM_FFMPEG=$(pgrep -f "ffmpeg.*${RTSP}.*segment" | head -1 || true)
    [ -n "$VM_FFMPEG" ] && break
    sleep 1
done
[ -n "$VM_FFMPEG" ] || {
    echo "--- server log ---"; tail -30 /tmp/vm-cost-server.log
    fail "VisionMesh never started an ffmpeg for the camera."
}
echo "VisionMesh ffmpeg is pid ${VM_FFMPEG}"

echo "--- its command line ---"
tr '\0' ' ' < "/proc/${VM_FFMPEG}/cmdline"; echo

# The assertion the measurement rests on: nothing is being decoded.
if tr '\0' ' ' < "/proc/${VM_FFMPEG}/cmdline" | grep -q image2pipe; then
    fail "A recording-only camera is still running the MJPEG transcode."
fi
echo "no transcode in the command line"

# ---- measure ----

measure_cpu() {
    local pid=$1 seconds=$2
    local clk; clk=$(getconf CLK_TCK)

    local before after
    before=$(awk '{print $14 + $15}' "/proc/${pid}/stat")
    sleep "$seconds"
    [ -d "/proc/${pid}" ] || { echo "gone"; return; }
    after=$(awk '{print $14 + $15}' "/proc/${pid}/stat")

    awk -v b="$before" -v a="$after" -v s="$seconds" -v c="$clk" \
        'BEGIN { printf "%.1f", ((a - b) / c) * 100 / s }'
}

echo "measuring for 20 seconds with nobody watching"
IDLE_CPU=$(measure_cpu "$VM_FFMPEG" 20)
echo "recording only: ${IDLE_CPU}% of one core"

[ "$IDLE_CPU" != "gone" ] || fail "The ffmpeg process died during the measurement."

awk -v v="$IDLE_CPU" -v b="$BUDGET_PERCENT" 'BEGIN { exit !(v < b) }' \
    || fail "A recording-only camera used ${IDLE_CPU}% of a core, over the ${BUDGET_PERCENT}% budget."

RECORDED=$(find "${DATA}/recordings" -name '*.mp4' -newermt '-60 seconds' | wc -l)
[ "$RECORDED" -gt 0 ] || fail "Nothing was recorded, so the low processor use proves nothing."
echo "and it is genuinely recording: ${RECORDED} segment(s) on disk"

# ---- and it still works when somebody watches ----

curl -s --max-time 40 -H "Authorization: Bearer ${TOKEN}" \
    "${BASE}/api/cameras/${CAMERA}/stream.mjpeg" -o /tmp/cost-stream.bin &
STREAM_READER=$!

# The supervisor restarts ffmpeg with the live output when a viewer arrives, so wait for the
# new process before measuring it.
# Switching a recording camera back to transcoding restarts ffmpeg, and the old process is
# asked to quit politely first so its segment is finalised, so this is not instant.
WATCHED_FFMPEG=""
SWITCH_START=$(date +%s)
for _ in $(seq 1 25); do
    WATCHED_FFMPEG=$(pgrep -f "ffmpeg.*image2pipe" | head -1 || true)
    [ -n "$WATCHED_FFMPEG" ] && break
    sleep 1
done
echo "took $(( $(date +%s) - SWITCH_START ))s to start transcoding after the viewer arrived"

if [ -n "$WATCHED_FFMPEG" ]; then
    WATCHED_CPU=$(measure_cpu "$WATCHED_FFMPEG" 15)
    echo "with one viewer:  ${WATCHED_CPU}% of one core"
    echo "the transcode costs ${WATCHED_CPU}% against ${IDLE_CPU}% idle, and that difference"
    echo "used to be paid around the clock by every recording camera."
else
    echo "::warning::Could not find the transcoding ffmpeg to measure it."
fi

wait "$STREAM_READER" 2>/dev/null || true

FRAMES=$(python3 - <<'PY'
data = open('/tmp/cost-stream.bin', 'rb').read()
print(data.count(b'\xff\xd8\xff'))
PY
)
echo "frames received once a viewer connected: ${FRAMES}"
[ "$FRAMES" -gt 5 ] || fail "Opening the live view produced ${FRAMES} frames; turning the transcode back on is broken."

echo "A recording camera costs ${IDLE_CPU}% of a core and still streams on demand."
