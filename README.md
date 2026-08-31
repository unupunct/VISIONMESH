<div align="center">

<img src="assets/branding/visionmesh-256.png" width="120" height="120" alt="VisionMesh">

# VISIONMESH

**Universal self-hosted camera and surveillance platform**

Turn USB cameras, computers, phones and IP cameras into one unified surveillance system.
No cloud. No account. No subscription. Your footage never leaves your network.

[![Build](https://github.com/unupunct/VISIONMESH/actions/workflows/tests.yml/badge.svg)](https://github.com/unupunct/VISIONMESH/actions/workflows/tests.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)

</div>

---

## What it does

```
                       VISIONMESH SERVER
                     Windows or Linux machine
                              │
        ┌─────────────────────┼─────────────────────┐
        │                     │                     │
        ▼                     ▼                     ▼
   WINDOWS AGENT         LINUX AGENT           IP CAMERAS
   USB / built-in        USB / built-in        RTSP · ONVIF
        │                     │                     │
        └─────────────────────┼─────────────────────┘
                              │
                              ▼
                       STREAM GATEWAY
                              │
                    ┌─────────┴─────────┐
                    ▼                   ▼
                WEB BROWSER         PHONE
              any device            camera or viewer
                    │                   │
                    └─────────┬─────────┘
                              │
                              ▼
                       HOME ASSISTANT
                  cameras · sensors · automations
```

Install the server on one machine. Open the dashboard. Press **Add camera**. That is the whole
product; everything else exists to support that.

---

## Features

**Cameras from anywhere**
- USB and built-in webcams, on the server or on any other computer running the agent
- Phones and tablets, with nothing to install — scan a code and the browser becomes the camera
- RTSP cameras, added by address
- ONVIF cameras, found automatically on the network

**Built for people who are not surveillance engineers**
- A five-step first-run wizard with working defaults
- Every advanced setting has a plain-language explanation behind a "What is this?" link
- A **Fix camera** button that checks the whole chain and says what is actually wrong
- A built-in help centre, written for a non-technical reader first

**Real engineering underneath**
- Cameras that produce MJPEG have their frames forwarded **without being decoded or re-encoded**
- Network cameras recording continuously are written with the stream **copied**, not transcoded,
  from the same connection that serves the live view
- Demand-driven capture: a camera nobody is watching and nothing is recording costs nothing
- Motion detection runs on 1/8-scale luma recovered from each JPEG without fully decoding it

**Private by design**
- No cloud, no telemetry, nothing phones home
- Privacy mode genuinely stops capture and recording, and says so to anyone who opens the camera
- Passwords hashed with PBKDF2, camera credentials encrypted at rest, device tokens stored hashed
- Pairing codes are single-use and expire in minutes

**Home Assistant**
- A proper custom integration: camera entities with live video, not copied RTSP URLs
- Online, motion and recording binary sensors; frame rate, bitrate, latency and storage sensors
- Privacy and recording switches, PTZ buttons on cameras that support them
- Optional MQTT discovery for state — video deliberately does not travel over MQTT

---

## Status

This is an honest account of what has been built and how far it has been verified. Nothing below
is aspirational.

| Component | State | Verified how |
|---|---|---|
| Server (Windows, Linux) | Working | Runs, 62 automated tests, driven through a browser |
| Web dashboard | Working | Every page exercised in a real browser |
| Pairing, devices, camera CRUD | Working | End-to-end tests pair a device and add its camera |
| Live streaming (MJPEG) | Working | End-to-end tests read real frames off the stream endpoint |
| Windows agent — discovery and capture | Working | Ran against a real Logitech BRIO: 720p15 MJPEG forwarded without re-encoding |
| Privacy mode | Working | Verified on real hardware: camera released within a second, stream and snapshot refused |
| Linux agent | Built, not run on Linux hardware | V4L2 struct layouts asserted against kernel ioctl sizes |
| Browser camera (phone) | Built, not run on a phone | Frame header verified as a cross-language contract |
| RTSP cameras | Working | Pulled a live RTSP H.264 stream: transcoded for viewing, stream-copied for recording |
| ONVIF discovery and PTZ | Built, not run against a real camera | ONVIF client is hand-written SOAP |
| Recording and playback | Working | Recorded a webcam and an RTSP stream: valid H.264 MP4, indexed, seekable by range request |
| Motion detection | Working | Fired on a live camera and started a recording; decoder tested against real encoder output |
| Home Assistant integration | Built, not run inside Home Assistant | Python syntax-checked; API it consumes is tested |
| MQTT discovery | Built, not run against a broker | — |

**Deliberately not built yet**

- **WebRTC.** Live viewing uses MJPEG over HTTP. It works in every browser and webview with no
  plugin, no signalling and no transcoding of already-JPEG sources. It costs more bandwidth than
  H.264 and carries no audio. WebRTC is the right long-term answer and is not implemented; the
  streaming layer is separated from the dashboard so it can be added behind the same API.
- **Native Android and iOS apps.** The browser camera covers the same ground today with nothing to
  install. The agent protocol is documented, so a native app is additive rather than a rewrite.
- **Floor plans and drag-and-drop camera groups.** Groups work; the visual editors do not exist.

---

## Quick start

### Linux

```bash
wget https://raw.githubusercontent.com/unupunct/VISIONMESH/main/install.sh
```

Read it before you run it — you should read any script that asks for root.

```bash
chmod +x install.sh
sudo ./install.sh
```

Then open the address it prints, and follow the setup wizard.

### Windows

Download the current release from [Releases](https://github.com/unupunct/VISIONMESH/releases),
unzip it, and run `VisionMesh.Server.exe`. Open `http://localhost:8088`.

To run it in the background:

```powershell
sc.exe create VisionMesh binPath= "C:\VisionMesh\VisionMesh.Server.exe" start= auto
sc.exe start VisionMesh
```

### Build it yourself

```bash
git clone https://github.com/unupunct/VISIONMESH.git
cd VISIONMESH
dotnet test
dotnet run --project server/VisionMesh.Server
```

---

## Adding your first camera

**A phone** — Devices → Add device → point the phone's camera at the code → Start camera.
Nothing to install.

**A webcam on another computer** — install the agent on that machine, then:

```bash
VisionMesh.Agent pair       # asks for the server address and the pairing code
VisionMesh.Agent            # run it
```

**A network camera** — Add camera → IP camera. VisionMesh asks every camera on the network to
identify itself, and lists the ones that answer.

---

## ffmpeg

VisionMesh needs [ffmpeg](https://ffmpeg.org/) for RTSP and ONVIF cameras, and for recording.
USB cameras and phone cameras work without it.

```bash
sudo apt install ffmpeg          # Debian, Ubuntu, Raspberry Pi OS
sudo dnf install ffmpeg          # Fedora
```

On Windows, download a build and either put it on `PATH` or drop `ffmpeg.exe` next to
`VisionMesh.Server.exe`.

It is not bundled: its licensing and per-distribution builds make shipping it inside an installer
a legal and packaging problem. When it is missing, the features that need it are switched off and
labelled, rather than failing the moment somebody presses a button.

---

## Watching from outside your home

**Do not forward a port to this server.** Cameras exposed that way are found and probed within
hours, and are one of the most common sources of leaked home footage.

Use a private network instead. With [Tailscale](https://tailscale.com/) or WireGuard, your phone
joins the same private network and reaches VisionMesh exactly as it does at home, with nothing
exposed to the internet. See [docs/11-tailscale.md](docs/11-tailscale.md).

---

## Home Assistant

Copy `homeassistant/custom_components/visionmesh` into your Home Assistant
`config/custom_components` folder, restart, then add the VisionMesh integration and sign in with a
VisionMesh account.

Your cameras appear as camera entities with live video, plus sensors and switches for automations:

```yaml
automation:
  - alias: "Porch light on motion"
    trigger:
      platform: state
      entity_id: binary_sensor.front_door_motion
      to: "on"
    action:
      - service: light.turn_on
        target: { entity_id: light.porch }
      - service: notify.mobile_app
        data:
          message: "Movement at the front door"
```

Full guide: [docs/10-home-assistant.md](docs/10-home-assistant.md).

---

## Documentation

| | |
|---|---|
| [Getting started](docs/01-getting-started.md) | [Recording](docs/12-recording.md) |
| [Linux server](docs/02-linux-server.md) | [Motion detection](docs/13-motion.md) |
| [Windows server](docs/03-windows-server.md) | [Storage](docs/14-storage.md) |
| [Windows camera](docs/04-windows-camera.md) | [Troubleshooting](docs/15-troubleshooting.md) |
| [Linux camera](docs/05-linux-camera.md) | [Security](docs/16-security.md) |
| [Android camera](docs/06-android-camera.md) | [Advanced](docs/17-advanced.md) |
| [iPhone camera](docs/07-ios-camera.md) | [Architecture](docs/18-architecture.md) |
| [RTSP](docs/08-rtsp.md) | [API reference](docs/api/README.md) |
| [ONVIF](docs/09-onvif.md) | [Home Assistant](docs/10-home-assistant.md) |
| [Tailscale](docs/11-tailscale.md) | |

The API is also documented live at `/api/docs` on a running server.

---

## Platform support

| Platform | Server | Camera agent | Viewer |
|---|---|---|---|
| Windows 10/11 | Supported | Supported | Any browser |
| Linux x64 | Supported | Supported | Any browser |
| Linux arm64 | Supported | Supported | Any browser |
| Android | — | Browser camera | Any browser |
| iOS / iPadOS | — | Browser camera | Any browser |
| RTSP cameras | Source | — | — |
| ONVIF cameras | Source, with PTZ | — | — |

"Browser camera" means a page that turns the device into a camera, with no app to install. It only
runs while the page is open and the screen is on, because that is all a phone permits.

---

## Repository layout

```
server/       The server: core, database, API, streaming, recording, Home Assistant
agents/       Windows and Linux camera agents, and the code they share
web/dashboard The dashboard and the browser camera. Plain HTML, CSS and ES modules; no build step
homeassistant Home Assistant custom integration
docs/         Guides, written for a non-technical reader first
scripts/      Build, verification and packaging scripts
tests/        Automated tests
```

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Bug reports that include what you expected, what happened,
and the output of **Fix camera** are worth ten that do not.

## Security

See [SECURITY.md](SECURITY.md) for how to report a vulnerability, and
[docs/16-security.md](docs/16-security.md) for what VisionMesh does with your footage — and what it
will never do.

## License

MIT. See [LICENSE](LICENSE).
