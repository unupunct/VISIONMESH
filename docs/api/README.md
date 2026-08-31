# API reference

VisionMesh serves interactive documentation at **`/api/docs`** on any running server. That is
generated from the code and is always current, so it is the authoritative reference.

This page covers what the generated documentation cannot: how authentication works, and how the
streaming endpoints behave.

## Authentication

```bash
curl -s -X POST http://localhost:8088/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"your-password"}'
```

```json
{
  "token": "s0m3-0paqu3-t0k3n",
  "expiresUtc": "2026-09-30T12:00:00Z",
  "user": { "id": "usr_...", "username": "admin", "role": "Administrator" }
}
```

Send it as a bearer token:

```bash
curl -s http://localhost:8088/api/cameras -H "Authorization: Bearer $TOKEN"
```

The same call also sets an `HttpOnly` session cookie. That is what lets an `<img>` tag stream a
camera in a browser without a credential ever appearing in a URL.

Tokens last 30 days. They are revoked immediately when the account's password changes, its role is
reduced, or it is disabled or deleted.

## Roles

| Role | Can |
|---|---|
| Viewer | Read cameras, events, recordings, storage; open streams and snapshots |
| Operator | Also record, pause, set privacy mode, move cameras, run diagnostics |
| Administrator | Everything, including users, devices, settings and integrations |

A request that needs a higher role returns `403` with the role it needed:

```json
{ "error": "This action needs the Operator role.", "code": "forbidden", "required": "Operator" }
```

## Errors

Every error carries a message written for a person, and often a machine-readable `code`.

```json
{ "error": "This camera is in privacy mode. Turn privacy mode off to view it.", "code": "privacy_mode" }
```

| Code | Meaning |
|---|---|
| `unauthenticated` | No valid session |
| `forbidden` | Signed in, but the role is too low |
| `privacy_mode` | The camera is deliberately not capturing |
| `device_offline` | The machine hosting the camera is not connected |
| `ffmpeg_missing` | The action needs ffmpeg, which is not installed |
| `no_frame` | The camera did not produce a picture in time |
| `camera_auth` | The camera rejected its own credentials |
| `camera_unreachable` | The camera could not be contacted |
| `file_missing` | The recording is no longer on disk |

## Streaming

### Live video

```
GET /api/cameras/{id}/stream.mjpeg
```

Returns `multipart/x-mixed-replace; boundary=visionmeshframe`, indefinitely, one JPEG per part.
Close the connection to stop it. Opening it starts the camera; closing the last one stops it after
a short grace period.

Authorised by the session cookie, a bearer token, or a stream token.

### Stream tokens

For clients that cannot set an `Authorization` header on a media request:

```bash
curl -s -X POST http://localhost:8088/api/cameras/$ID/stream-token -H "Authorization: Bearer $TOKEN"
```

```json
{ "token": "sh0rt-l1v3d", "expiresUtc": "2026-08-31T12:02:00Z" }
```

Valid for **two minutes** and for **that camera only**. Pass it as `?token=`.

Session tokens are deliberately not accepted in a query string: they would end up in browser
history, proxy logs and screenshots.

### Snapshots

```
GET /api/cameras/{id}/snapshot.jpg
```

Returns the most recent frame. If the camera is not running, it is started and the request waits
up to ten seconds for a first frame, then returns `503` with `no_frame`.

## Realtime updates

```
GET /api/ws          (WebSocket)
```

Authorised by the session cookie, since a browser cannot set headers on a WebSocket.

Messages are JSON with a `type`:

| Type | When |
|---|---|
| `camera.state` | A camera came online, went offline, or changed state |
| `camera.health` | Measured statistics, every few seconds |
| `camera.recording` | Recording started or stopped |
| `camera.added`, `camera.removed` | The camera list changed |
| `device.state` | A machine connected or disconnected |
| `event` | Something was written to the event log |
| `storage.warning` | Disk space is running out |
| `system.changed` | Server-level state changed |

Subscribers that fall behind are dropped rather than buffered, so a stalled browser tab cannot hold
server memory.

## Measured values are nullable

Anything VisionMesh measures rather than knows is `null` until it has been measured.

```json
{
  "fps": null,
  "bitrateBps": null,
  "framesReceived": 0
}
```

`fps: null` means "not enough frames have arrived to measure this yet". It does not mean zero, and
a client should show a dash rather than a number. This is consistent throughout: storage
projections, latency, dropped frames and battery all behave the same way.

## A worked example

```bash
BASE=http://localhost:8088

TOKEN=$(curl -s -X POST $BASE/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"your-password"}' | jq -r .token)

AUTH="Authorization: Bearer $TOKEN"

curl -s $BASE/api/cameras -H "$AUTH" | jq '.[] | {id, name, state}'
```

```bash
CAM=$(curl -s $BASE/api/cameras -H "$AUTH" | jq -r '.[0].id')

curl -s -X POST $BASE/api/cameras/$CAM/test -H "$AUTH" | jq
```

```json
{
  "ok": true,
  "framesReceived": 14,
  "timeToFirstFrameMs": 412,
  "measuredFps": 14.8,
  "measuredBitrateBps": 2810000,
  "resolution": "1280x720",
  "latencyMs": 31,
  "error": null
}
```

```bash
curl -s -X POST $BASE/api/cameras/$CAM/diagnose -H "$AUTH" | jq '{healthy, summary, recommendedAction}'
```

## The agent protocol

Agents use a different endpoint and a different contract. See
[Advanced](../17-advanced.md#the-agent-protocol).
