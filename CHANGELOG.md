# Changelog

All notable changes to VisionMesh are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project uses [semantic versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- MQTT discovery is now checked against a real mosquitto broker in CI. The parts worth getting
  wrong live in the broker rather than in the payloads: the subscriber connects before the server
  does, so retained configuration topics are met the way Home Assistant meets them; a privacy
  command is published from outside and has to change real state; and the server is killed with
  SIGKILL to prove the last will fires, because without it Home Assistant would show every
  entity's last known value indefinitely — a dashboard that looks healthy while the cameras
  are gone.
- The browser camera's capture path now runs in Chromium with a synthetic capture device, which
  gives a real MediaStream over a secure context, so getUserMedia, the canvas draw and toBlob all
  execute as they would on a phone. Frames are read back off the server's stream endpoint,
  counted, and one is decoded for its real dimensions.

### Known

- The per-camera width, height, fps and quality on the dashboard do not reach a browser camera.
  The phone scales to the profile chosen on its own page instead, which is deliberate — the phone
  pays the battery and bandwidth cost — but the dashboard still offers the fields and the server
  still sends the values, so they look effective and are not.

## [1.0.3] - 2026-08-31

The Linux agent works. It never had, and only running it against a real V4L2 device showed that.

### Fixed

- **The Linux agent can now capture at all.** `v4l2_format` was declared with its pixel format
  block starting at offset 4. The kernel's union there contains pointers, so on 64-bit it is eight
  byte aligned and the block starts at offset 8. Every field landed one slot early — width into the
  padding, height into width, the pixel format into height — and the kernel answered EINVAL for
  every format offered. The agent reported "this camera offers no video format VisionMesh can use",
  which sounds like a limited camera and was the agent describing its own broken struct. No Linux
  camera could ever have worked. The size assertions could not catch it, because the struct is 208
  bytes either way, and the offset test that existed asserted the wrong offsets.
- Captured JPEGs are trimmed at their end-of-image marker. A V4L2 buffer is sized for the largest
  frame the format could produce and many drivers return the whole thing with padding behind the
  image: measured here, 3,686,400 bytes carrying a 60,334 byte JPEG. That padding cost bandwidth on
  every frame and pushed frames past the server's message ceiling, which dropped the connection.
- A camera that will not change format is no longer reported as a camera with no usable formats.
  Each refusal is logged with the format and the driver's errno, and the failure asks the camera
  what it is actually delivering, so "another program is using it" stops looking like "this camera
  is unsupported".

### Added

- The Linux agent now runs against a real V4L2 device in CI, using v4l2loopback fed by ffmpeg, so
  every ioctl goes through the kernel as it would against a webcam. The check reads frames back off
  the server's stream endpoint and decodes one for its real dimensions. It also confirms that a
  camera the agent may not open is reported with the reason and the fix, rather than hidden.

- Deleting recordings is now tested. It is the only code in VisionMesh that destroys something a
  user cannot get back, and it had no test: the retention window and its boundary, per-camera
  overrides, "keep forever", the storage cap deleting oldest-first and saying so, and a row whose
  file has already gone. The recordings in these tests are real files, so a test that says a file
  was deleted means it is gone from disk.
- Upgrading an existing database is now tested, including the invariant that migrations are
  append-only. Editing a migration that has already shipped is silent and asymmetric — a fresh
  install gets the edited schema, an existing install has already stamped that version and never
  re-runs it — so each shipped migration is now fingerprinted and the test fails if one changes.

## [1.0.2] - 2026-08-31

Fixes found by running the released 1.0.1 binaries against each other, and by running the Home
Assistant integration inside Home Assistant for the first time.

### Added

- The Home Assistant integration is now loaded into a real Home Assistant and exercised in CI —
  the config flow a user walks through, and the entities that come out of it — alongside hassfest,
  Home Assistant's own validator. It could not be checked on the machine it was written on,
  because Home Assistant core does not install on Windows. A further test reads the C# endpoint
  that builds the camera list and fails if the test payloads and the server have drifted apart,
  which is the usual way a mocked integration test passes while the real thing is broken.

### Fixed

- The Home Assistant config flow no longer accepts an address that cannot work. `http://` became
  `http://http:` because trimming trailing slashes ate the scheme's own; `not a url` was accepted
  because urlparse will call anything without a slash a hostname; and a port above 65535 raised
  out of the flow as a traceback rather than a message on the form. An address accepted here and
  failing later reads as "cannot connect", which sends someone to look at their network instead
  of at what they typed. The address is now rebuilt from its parsed parts, so a path, a query or
  credentials in it cannot survive into the base URL every later request is built from. Found by
  running the integration inside Home Assistant.
- Agents now report the version they actually are. It was a hand-written constant in each agent,
  and it drifted: the 1.0.1 agent introduced itself to the server as 1.0.0, so the Devices page —
  the one place a user looks to decide whether an agent needs updating — showed a version that was
  not the one running. It now comes from the assembly, and a test fails if a literal comes back.
  The browser camera no longer reports a version number at all, because it is served by the server
  and so is always the server's own version. Found by pairing the released agent with the released
  server.

## [1.0.1] - 2026-08-31

Fixes found by running 1.0.0 against a real webcam and a real RTSP stream.

### Fixed

- Recordings are now playable after an interrupted recording. The flags that make each segment
  survive an interruption have to reach the mp4 muxer inside the segment muxer, and the segment
  muxer does not forward a plain `-movflags` to it. Written that way the flags were silently
  ignored, so any recording that was stopped rather than allowed to finish had no moov atom: the
  right size, present in the archive, and playable nowhere. They now go through
  `-segment_format_options`, and the arguments live in one tested place. Found by recording a
  live RTSP stream and killing the recorder.
- Network camera recordings are now ended by asking ffmpeg to quit rather than killing it, so the
  final segment is finalised properly instead of relying on fragmentation to rescue it.
- Recordings are now labelled with the reason they were made. The indexer previously marked every
  segment Continuous, because an ffmpeg segment file carries nothing that says what caused it, so
  a motion clip appeared in the recordings list and on the timeline as continuous footage. The
  engine now records why each recording started and the indexer asks it. Found by recording a
  live camera.

## [1.0.0] - 2026-08-31

First release. Everything below is new.

### Server

- Single-process server for Windows and Linux: API, dashboard, stream gateway, recorder and
  integrations in one thing to install and one thing to restart.
- SQLite metadata store with forward-only migrations. Video is never stored in the database.
- Session authentication with PBKDF2 password hashing, three roles, an audit log, and login
  throttling that backs off rather than locking accounts out.
- Camera credentials encrypted at rest with AES-256-GCM.
- Device pairing with single-use codes that expire in ten minutes. Device tokens are stored only
  as hashes, so a copy of the database cannot be used to impersonate a device.
- mDNS advertisement, so agents and Home Assistant can find the server without an address.
- OpenAPI documentation served at `/api/docs`.

### Cameras and streaming

- Agent protocol over one WebSocket: JSON for control, a fixed 24-byte binary header for frames.
- Demand-driven capture. A camera nobody is watching and nothing is recording is stopped.
- MJPEG live streaming to browsers, with no plugin, no signalling and no transcoding of sources
  that already produce JPEG.
- Windows camera agent using Media Foundation, preferring a camera's native MJPEG output.
- Linux camera agent using V4L2 memory-mapped streaming, with the same MJPEG preference.
- Browser camera: a full agent implemented in the page, turning any phone into a camera with
  nothing to install.
- RTSP cameras by address, and ONVIF cameras found automatically by WS-Discovery, with PTZ where
  the camera supports it.

### Recording

- Continuous, motion, scheduled and manual recording, in ten-minute MP4 segments named by start
  time so the archive is readable from a file manager.
- Network cameras recording continuously are written by copying the camera's own stream — no
  re-encode, full source quality, and one connection to the camera rather than two.
- Motion detection on 1/8-scale luma recovered from each JPEG without fully decoding it, with
  global-brightness rejection and a few seconds of pre-roll kept in memory.
- Retention by age and by total size, with real measured file sizes rather than estimates.

### Dashboard

- First-run wizard, camera wall with per-tile lazy streaming, camera panel with PTZ and
  diagnostics, recording timeline, events, devices, storage, network and settings.
- A **Fix camera** wizard that checks the whole chain and reports the first real fault in plain
  language.
- A built-in help centre written for a non-technical reader, with a technical note per topic.
- No build step: plain HTML, CSS and ES modules, including a QR encoder written in-repo so the
  dashboard works with no internet access.

### Home Assistant

- Custom integration with a config flow, mDNS discovery and re-authentication.
- Camera entities with live video and snapshots; online, motion and recording binary sensors;
  frame rate, bitrate, latency, battery and storage sensors; privacy and recording switches; PTZ
  buttons on cameras that support them.
- Optional MQTT discovery for state. Video deliberately does not travel over MQTT.

### Not in this release

- WebRTC. Live viewing uses MJPEG.
- Native Android and iOS applications. The browser camera covers the same ground.
- Floor plans and drag-and-drop camera groups.

[Unreleased]: https://github.com/unupunct/VISIONMESH/compare/v1.0.3...HEAD
[1.0.3]: https://github.com/unupunct/VISIONMESH/compare/v1.0.2...v1.0.3
[1.0.2]: https://github.com/unupunct/VISIONMESH/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/unupunct/VISIONMESH/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/unupunct/VISIONMESH/releases/tag/v1.0.0
