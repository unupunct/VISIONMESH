# Mobile

## Today: the browser camera

VisionMesh turns any phone into a camera with **nothing to install**. Point the phone's own camera
app at the pairing code on the server, open the link, press Start camera.

That page is a complete VisionMesh agent implemented in JavaScript. It claims a pairing code for a
device token, holds open the same WebSocket the Windows and Linux agents use, advertises its
cameras, and pushes JPEG frames behind the same binary frame header. The server cannot tell the
difference and does not need to.

The source is [`web/dashboard/camera.html`](../web/dashboard/camera.html) and
[`web/dashboard/js/camera.js`](../web/dashboard/js/camera.js).

Guides: [Android](../docs/06-android-camera.md) · [iPhone and iPad](../docs/07-ios-camera.md)

### What it can do

- Front or rear camera, three quality profiles
- Live preview with measured frame rate and bitrate
- Battery level and network type reported back to the dashboard
- A screen wake lock, so the display does not switch itself off
- Reconnects on its own after a Wi-Fi drop
- Installable to the home screen, where it opens without browser chrome

### What it cannot do

**Run in the background.** Android and iOS suspend a background tab within seconds. The page has to
stay open with the screen on.

This is a platform rule, not an oversight, and VisionMesh does not try to work around it. The page
says so plainly rather than appearing to work and quietly stopping.

**Work over plain HTTP.** Browsers only allow a page to use a camera in a secure context. On a
`http://192.168.x.x` address the phone will refuse.

The simplest fix is [Tailscale](../docs/11-tailscale.md), which gives the server a name with a real
certificate and no configuration, and makes the cameras work from outside the house at the same
time.

---

## Native apps

There are no native Android or iOS applications in this repository, and this file will not pretend
otherwise.

### Why not yet

The browser camera covers the same ground for most people, immediately, with no store account, no
review process and no signing keys. Building native apps before that existed would have been the
wrong order.

### What a native app would add

One thing, and it is the important one: **background operation**. A native app can hold a
foreground service on Android, or a background audio-style session on iOS, and keep streaming with
the screen off. That is what turns a phone from "a camera while I am watching it" into "a camera".

It would also get: better battery behaviour through hardware-accelerated capture, push
notifications, and a native viewer that does not depend on a browser tab.

### What building one involves

The protocol is fully documented in [Advanced](../docs/17-advanced.md#the-agent-protocol), and a
working reference implementation exists in JavaScript, so the client side is a well-defined piece
of work rather than a research project. The pieces are:

1. `POST /api/pairing/claim` with the pairing code, to get a device token
2. A WebSocket to `/agent/ws` with that token
3. `hello` with the device's cameras, then respond to `start-capture` and `stop-capture`
4. Capture frames, encode JPEG, prefix the 24-byte header, send as binary
5. `telemetry` every five seconds

**Android** would be Kotlin with CameraX, and can be distributed as an APK straight from GitHub
Releases — no store account needed.

**iOS** would be Swift with AVFoundation, and cannot be distributed the same way. Apple requires a
Developer Program membership and either TestFlight or the App Store. Nothing in this repository can
shortcut that, and a release note claiming an iOS binary alongside the Windows and Linux ones would
be false.

### Viewer apps

Less urgent. The dashboard is responsive, works well on a phone, and installs to the home screen as
a progressive web app. A native viewer would mainly add push notifications.

---

## Contributing

If you want to build either app, the protocol will not move under you: the frame header is covered
by a cross-language contract test, and any change to it fails CI.

Start with [`web/dashboard/js/camera.js`](../web/dashboard/js/camera.js). It is a complete,
working, commented agent in about 500 lines.
