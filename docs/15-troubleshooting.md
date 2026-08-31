# When something is wrong

## Start here

Open the camera and press **Fix camera**.

It checks the whole chain — device connected, camera present, network reachable, credentials
accepted, video arriving, disk writable — and tells you the **first thing that is actually wrong**,
in plain language, with what to do about it.

That answers most of what follows, and it is what to paste into a bug report.

---

## A camera says Offline

**If it is on another computer:** that computer is switched off, asleep, or the agent is not
running. Check **Devices** — if the device shows as offline, the problem is there, not with the
camera.

A laptop that sleeps is not a camera. Set it never to sleep.

**If it is a network camera:** the camera is off, its address changed, or its password is wrong.
Fix camera distinguishes between these — it pings the camera, checks the stored credentials can be
decrypted, and tries to open the stream.

**If it is a phone:** the page was closed, the screen locked, or the browser put it to sleep. This
is normal and unavoidable; see [Android](06-android-camera.md) or [iPhone](07-ios-camera.md).

---

## The picture freezes or breaks up

Almost always the network.

- Wi-Fi cameras far from the access point are the usual culprit. Move the camera, move the access
  point, or wire it.
- Lower the picture size or frame rate in the camera's settings. Frame rate helps more than
  resolution for a congested link.
- For an RTSP camera, change **Connection type** to **TCP**. UDP is lower latency but loses
  packets, and lost packets in video look like tearing.

If **Test connection** reports a measured frame rate far below what you asked for, that confirms
it: the frames are not arriving.

---

## Everything is slow, the server is busy

Check processor use on the **Network** page.

The expensive things, in order:

1. **Network cameras being watched.** An RTSP camera sends H.264, which has to be transcoded for
   the browser. That is the single biggest cost.
2. **Cameras that cannot produce MJPEG.** The agent encodes every frame itself. Run
   `VisionMesh.Agent list` — it says which format was chosen.
3. **Motion detection**, but only slightly.

What helps:

- Point network cameras at their **substream** rather than the main stream
- Lower frame rates. 10 fps is plenty for most surveillance
- Close dashboard tabs you are not watching. A camera nobody is watching and nothing is recording
  is stopped entirely

---

## A camera vanished after a reboot

If it was a USB camera, the operating system may have given it a different device path. VisionMesh
identifies cameras by device path, which is stable for most cameras but not all.

Remove the camera in the dashboard and add it again. The recordings are kept.

---

## Recording is not happening

1. Is ffmpeg installed? Settings → Advanced says.
2. Is the camera set to record? Its settings say.
3. Is the folder writable? **Test this folder** on the Settings page.
4. Is there disk space? The **Storage** page.

Fix camera checks all four.

---

## Motion never triggers, or triggers constantly

See [Motion detection](13-motion.md). The short version: it has to be tuned to the scene, and
aiming the camera slightly downward helps more than any setting.

---

## I cannot reach the dashboard

**From the same machine:** is the server running?

```bash
sudo systemctl status visionmesh
```

**From another device:** are you using the right address? Ask the server:

```bash
curl -s http://localhost:8088/api/setup/status
```

Or open the **Network** page from a machine that can reach it, which lists every address.

Then check:

- Both devices are on the same network, not a guest Wi-Fi. Guest networks usually block devices
  from talking to each other, which breaks this completely.
- The firewall allows the port. On Windows this is the usual cause.

---

## I forgot the administrator password

There is no password reset, by design: a surveillance server with a reset backdoor is a
surveillance server anyone with local access can take over.

If you have another administrator account, use it to change the password.

If you do not, you need file access to the server. Stop it, move `visionmesh.db` aside, and start
it again — you will get a fresh setup wizard. Your recordings are untouched, but you lose cameras,
users and settings, and you will need to re-add cameras.

Keep `secret.key`: without it, saved camera passwords cannot be decrypted.

---

## Reading the logs

**Linux**

```bash
sudo journalctl -u visionmesh -f
```

**Windows** — the console window if running directly, or Event Viewer → Windows Logs →
Application if running as a service.

Camera passwords and authenticated URLs are redacted before they reach a log, so logs are safe to
share. What you paste from elsewhere is your responsibility.

---

## Reporting a bug

Include:

- The **Fix camera** output
- Your VisionMesh version, at the bottom of the sidebar
- What operating system the server runs on
- What kind of camera, and how it is connected
- Whether ffmpeg is installed

That is nearly everything a maintainer would otherwise have to ask for.

[Open an issue](https://github.com/unupunct/VISIONMESH/issues)

---

## Advanced

### Checking the API directly

```bash
curl -s http://localhost:8088/healthz
```

```bash
curl -s http://localhost:8088/api/system/capabilities -H "Authorization: Bearer TOKEN"
```

Get a token by signing in:

```bash
curl -s -X POST http://localhost:8088/api/auth/login -H "Content-Type: application/json" -d '{"username":"admin","password":"..."}'
```

The full API is documented at `/api/docs` on a running server.

### Watching a stream from the command line

```bash
curl -s "http://localhost:8088/api/cameras/CAMERA_ID/stream.mjpeg?token=STREAM_TOKEN" --output -
```

Get a stream token from `POST /api/cameras/{id}/stream-token`. Frames arriving means the server
side is fine and the problem is in the browser.

### Checking a camera without VisionMesh

For an RTSP camera, take VisionMesh out of the picture entirely:

```bash
ffplay "rtsp://user:pass@192.168.1.50:554/stream1"
```

If that fails, the problem is between ffmpeg and the camera, and no VisionMesh setting will fix
it.

For a USB camera on Linux:

```bash
v4l2-ctl --device /dev/video0 --list-formats-ext
```
