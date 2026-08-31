# Advanced

For technicians, and for anyone who wants to know how VisionMesh works rather than just how to use
it.

## Advanced mode

Settings → **Show advanced settings** reveals the ffmpeg path, storage limits and default motion
sensitivity. It is off by default so a normal install never presents a path to a binary.

## Command line

### Server

```
VisionMesh.Server [options]

  -p, --port <number>    Port for the dashboard and API (default 8088)
  -d, --data <folder>    Where the database and keys are kept
      --no-api-docs      Do not serve the API documentation at /api/docs
  -h, --help             Show help
```

Environment: `VISIONMESH_PORT`, `VISIONMESH_DATA`.

### Agents

```
VisionMesh.Agent [command] [options]

  (none)     Run the agent
  pair       Pair with a server
  list       Show attached cameras and their formats
  status     Show which server this machine is paired with
  unpair     Forget the server and delete the stored token

  --server <url>   --code <code>   --name <name>
  --config <file>  --verbose
```

## The agent protocol

One WebSocket at `/agent/ws`, authenticated with the device token as a bearer token or a `token`
query parameter.

**Text messages** carry JSON control envelopes. **Binary messages** carry frames.

The split matters: control is small and infrequent, frames are large and constant, so frames never
pay JSON or base64 overhead.

### Handshake

```
agent  → hello       device id, name, kind, platform, version, available cameras
server → welcome     server name, version, ping interval
server → ping        every 15 seconds
agent  → pong
```

Three missed intervals with no traffic at all is treated as a dead link. TCP does not notice
quickly enough on Wi-Fi or on a sleeping phone.

### Capture

```
server → start-capture   slot, camera id, source id, width, height, fps, quality
agent  → capture-started slot
agent  → [binary frames tagged with that slot]
agent  → telemetry       every 5 seconds: fps, dropped frames, battery, network
server → stop-capture    slot
```

The **slot** is a 16-bit number assigned by the server. It exists so the binary frame header stays
fixed-size rather than carrying a string camera id on every frame.

### The frame header

Every binary message begins with 24 bytes, little endian throughout:

| Offset | Size | What |
|---|---|---|
| 0 | 4 | Magic, `VMF1` |
| 4 | 1 | Payload kind: 1 = JPEG |
| 5 | 1 | Flags: 1 = the source produced JPEG itself and it was not re-encoded |
| 6 | 2 | Slot |
| 8 | 4 | Sequence number, per slot |
| 12 | 8 | Capture timestamp, unix milliseconds UTC |
| 20 | 2 | Width, 0 if unknown |
| 22 | 2 | Height, 0 if unknown |

The JPEG follows immediately.

This is a cross-language contract — C# in the agents, JavaScript in the browser camera — so both
implementations are tested against the same fixed byte sequence rather than against each other.
Getting it wrong does not throw anywhere: the server simply fails to match the slot, drops every
frame, and the camera sits silently offline.

## Streaming

### Why MJPEG

Live viewing uses `multipart/x-mixed-replace`. It works in every browser and every webview with no
plugin, no player, no signalling and, for sources that already produce JPEG, no transcoding at all.

It uses more bandwidth than H.264 and carries no audio. That is the trade, taken deliberately for a
first release that works everywhere rather than one that works impressively in some places.

WebRTC is the right long-term answer and is not implemented. The streaming layer is separated from
the dashboard so it can be added behind the same API.

### The frame bus

Each viewer gets a capacity-1 channel in drop-oldest mode.

For live surveillance a stale frame has no value, so a viewer that cannot keep up should skip to
the newest frame rather than fall further behind. Unbounded queueing would turn one slow browser
tab into unbounded server memory.

### Demand-driven capture

A camera runs when somebody is watching it or when it is set to record. Otherwise it is stopped
entirely.

Stopping is delayed by about twenty seconds, so flicking between cameras does not tear down and
rebuild a stream every few seconds.

This is what makes a twenty-camera install viable on a mini PC.

### Where transcoding happens, and where it does not

| Source | Live view | Recording |
|---|---|---|
| USB camera with MJPEG | Forwarded untouched | Re-encoded to H.264 |
| USB camera without MJPEG | Encoded by the agent | Re-encoded to H.264 |
| Phone browser camera | Encoded by the browser | Re-encoded to H.264 |
| RTSP/ONVIF, continuous | Transcoded to MJPEG | **Stream copy, no re-encode** |
| RTSP/ONVIF, motion or manual | Transcoded to MJPEG | Re-encoded to H.264 |

A network camera recording continuously uses **one** connection for both the live view and the
recording, and the recording pays no encoding cost at all.

## The API

Documented live at `/api/docs`. Authenticate with `POST /api/auth/login`, then send the token as a
bearer token, or rely on the session cookie the same call sets.

```
/api/cameras                       list, create
/api/cameras/{id}                  read, update, delete
/api/cameras/{id}/stream.mjpeg     live video
/api/cameras/{id}/snapshot.jpg     still image
/api/cameras/{id}/record           start and stop recording
/api/cameras/{id}/privacy          privacy mode
/api/cameras/{id}/ptz              pan, tilt, zoom
/api/cameras/{id}/test             connection test with measured results
/api/cameras/{id}/diagnose         the full Fix camera check
/api/devices                       machines and phones
/api/pairing                       issue a pairing code
/api/events                        the event log
/api/recordings                    the archive, and the timeline
/api/storage                       disk use, measured
/api/system                        server health
/api/homeassistant/entities        cameras in the shape the integration consumes
/api/ws                            dashboard push updates
/agent/ws                          agents
```

## Realtime

Dashboards receive push updates over a WebSocket rather than polling. Each subscriber gets a
bounded queue and is dropped if it falls behind, so one stalled tab cannot hold server memory.

A slow polling fallback exists only for networks where a WebSocket cannot be established at all.

## The database

SQLite, in WAL mode, for metadata only: devices, cameras, users, sessions, events, recording index,
audit log, settings.

Video is never stored in it. The recordings table holds paths and time ranges, which makes the
filesystem the source of truth and the table an index over it.

Migrations are forward-only and append-only. A shipped migration is never edited, because doing so
gives upgraded and fresh installations different schemas.

## Hardware acceleration

Not implemented. VisionMesh uses whatever ffmpeg does by default.

This is worth being straight about: NVENC, Quick Sync and VAAPI would meaningfully reduce the cost
of transcoding network cameras, and adding them is a natural next step. Claiming support that had
not been tested on real hardware would be worse than not having it.

## Building from source

```bash
dotnet test && dotnet run --project server/VisionMesh.Server
```

The dashboard has no build step. Edit the files and reload.

### Verification scripts

Several components are checked against independent implementations rather than against themselves:

```bash
bash scripts/check-js.sh
```

```bash
node scripts/verify-frame-header.mjs
```

```bash
node scripts/verify-qr.mjs > qr.json && python scripts/verify-qr.py qr.json
```

```bash
dotnet test --filter FullyQualifiedName~EmitJpegSamples && python scripts/verify-jpeg.py
```

The QR encoder is checked by decoding its output with OpenCV, and the JPEG encoder by decoding its
output with libjpeg. Two components written by the same hand agreeing with each other is weaker
evidence than either agreeing with the software everyone else uses.

### The V4L2 layout tests

The Linux agent's struct definitions are asserted against the sizes the kernel encodes into each
ioctl request number, which lets them be verified without Linux hardware. A struct one byte wrong
produces a request the kernel rejects with ENOTTY, and the symptom is "my camera does not work"
with nothing in the logs pointing at a struct definition.
