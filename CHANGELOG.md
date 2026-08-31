# Changelog

All notable changes to VisionMesh are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project uses [semantic versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/unupunct/VISIONMESH/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/unupunct/VISIONMESH/releases/tag/v1.0.0
