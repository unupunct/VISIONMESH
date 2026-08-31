# Architecture

How VisionMesh is put together, and why.

## One process

```
┌─────────────────────────────────────────────────────────────┐
│  VisionMesh.Server                                          │
│                                                             │
│  ┌───────────┐  ┌────────────┐  ┌──────────┐  ┌──────────┐  │
│  │ Dashboard │  │    API     │  │  Stream  │  │ Recorder │  │
│  │  static   │  │  REST + WS │  │ gateway  │  │          │  │
│  └───────────┘  └────────────┘  └──────────┘  └──────────┘  │
│         │              │              │             │       │
│         └──────────────┴──────┬───────┴─────────────┘       │
│                               │                             │
│                     ┌─────────┴─────────┐                   │
│                     │  Frame bus        │  in-memory        │
│                     │  Camera registry  │  SQLite metadata  │
│                     └───────────────────┘                   │
└─────────────────────────────────────────────────────────────┘
```

One process, deliberately. For a self-hosted product there is one thing to install, one thing to
restart, one log to read, and no inter-service networking to misconfigure on someone's home
network.

## Projects

| Project | What it owns |
|---|---|
| `VisionMesh.Core` | Domain model, wire contracts, cryptography helpers. No dependencies. |
| `VisionMesh.Database` | SQLite access, migrations, repositories |
| `VisionMesh.Streaming` | Frame bus, agent connections, MJPEG output, ONVIF, ffmpeg sources |
| `VisionMesh.Recording` | Recording engine, motion detection, storage management |
| `VisionMesh.HomeAssistant` | MQTT discovery, Home Assistant connection testing |
| `VisionMesh.Api` | HTTP endpoints, authentication, realtime hub |
| `VisionMesh.Server` | Host, mDNS, service integration, static files |
| `Agent.Core` | Agent-side protocol client, JPEG encoder |
| `Agent.Windows` | Media Foundation capture |
| `Agent.Linux` | V4L2 capture |

Dependencies point one way: Core knows about nothing, and the host knows about everything.

## The path a frame takes

```
Camera
  │
  ├─ USB, via an agent ──────► JPEG over WebSocket ──┐
  │                                                  │
  ├─ Phone browser ──────────► JPEG over WebSocket ──┤
  │                                                  │
  └─ RTSP/ONVIF ─► ffmpeg ──► JPEG over a pipe ──────┤
                      │                              │
                      │ (-c copy, same process)      ▼
                      ▼                        ┌───────────┐
                 Recording                     │ Frame bus │
                 on disk                       └─────┬─────┘
                                                     │
                                    ┌────────────────┼────────────────┐
                                    ▼                ▼                ▼
                              MJPEG to a       Recorder          Motion
                              browser          (re-encode)       detector
```

Everything inside the server speaks JPEG. That single decision is what lets one frame bus serve
browsers, recorders and the motion detector without any of them knowing where the frame came from.

## Decisions worth explaining

### Why JPEG as the internal format

Because it is what most cameras already produce. Nearly every USB webcam can emit MJPEG, and a
phone browser produces JPEG naturally. Choosing anything else would mean decoding those sources for
no reason.

It costs bandwidth compared with H.264, and it means network cameras have to be transcoded for the
live view. That is the trade.

### Why the frame bus drops frames

Each viewer has a capacity-1 channel in drop-oldest mode.

A late surveillance frame is worthless — nobody wants to see what happened four seconds ago —
while unbounded buffering turns one slow consumer into unbounded memory. Dropping is not a
degradation here; it is the correct behaviour.

### Why capture is demand-driven

A camera runs only when watched or recording. An idle twenty-camera install costs nothing, which
is what makes it viable on a mini PC rather than requiring a server.

The twenty-second stop delay exists so flicking between cameras does not restart streams
constantly.

### Why network camera recording is a stream copy

The same ffmpeg process that produces the MJPEG live view writes a second output with `-c copy`.

That gives full source quality, near-zero processor cost, and — most importantly — **one**
connection to the camera. Many cameras allow only one or two, and a design that opened a second
one for recording would fail on exactly the cameras people own.

Motion and manual recording cannot use it, because starting and stopping a stream copy means
restarting the connection.

### Why the recording index is a scan, not a notification

ffmpeg writes segment files on its own schedule and does not report when one is finished. Rather
than guess, VisionMesh scans and treats a file untouched for fifteen seconds as complete.

The side effect is the valuable part: the filesystem becomes the source of truth. Lose the
database and the archive is still there, still readable in time order, and a later scan re-indexes
it.

### Why there is a DC-only JPEG decoder

Motion detection wants a small, cheap, approximate picture. The DC coefficient of each 8×8 JPEG
block *is* the average brightness of those 64 pixels — exactly the downscale a detector would
compute anyway — so it can be read out by decoding one coefficient per block.

It also keeps a third-party image decoder out of the path that parses bytes arriving from cameras
on the network, which is the most exposed surface in the product.

### Why there is a JPEG encoder as well

The Linux agent has no System.Drawing, and cameras that cannot produce MJPEG have to be encoded
somewhere. Every managed imaging library carries either native binaries, a restrictive licence, or
a history of decoder vulnerabilities.

An encoder is the safe half of the problem: it only ever reads pixel buffers the agent produced
itself.

### Why sessions are opaque tokens

Immediate revocation. A password change or a role reduction drops every session for that account at
once, which a JWT cannot do without a revocation list that defeats the point of being stateless.

### Why the dashboard has no build step

A self-hosted product should be auditable and modifiable by the person running it. Requiring a
JavaScript toolchain to change a label is a barrier that buys nothing here.

The cost is that nothing catches a stray bracket before the browser does, so CI parses every module
instead.

## Testing

Fifty automated tests, weighted toward the things that fail *silently*:

- **End-to-end**: a real server on a real port, a real agent over a real WebSocket, reading real
  frames off the MJPEG endpoint. Pairing, camera creation, streaming, snapshots, health, privacy
  mode and disconnection.
- **Wire contracts**: the frame header is checked in both C# and JavaScript against the same fixed
  byte sequence, not against each other.
- **Struct layouts**: V4L2 structs are asserted against the sizes the kernel encodes into each
  ioctl number, verifiable without Linux hardware.
- **Parsers**: the JPEG decoder is tested against images from a real encoder, and fuzzed with
  hundreds of randomly corrupted frames.
- **Encoders**: the JPEG and QR encoders are verified by decoding their output with libjpeg and
  OpenCV respectively.

The pattern is deliberate. A wrong frame header does not throw — the server drops every frame and
the camera sits offline with no error anywhere. Those are the failures worth a test.

## What is not here

**WebRTC.** Live viewing is MJPEG. The streaming layer is separate from the dashboard so WebRTC can
be added behind the same API.

**Hardware acceleration.** ffmpeg's defaults are used. NVENC, Quick Sync and VAAPI would
meaningfully reduce network-camera transcoding cost and are a natural next step.

**Native mobile apps.** The browser camera covers the same ground with nothing to install. The
protocol is documented, so a native client is additive.

**Clustering.** One server. A home does not need two, and the complexity would show up everywhere.
