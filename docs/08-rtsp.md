# RTSP cameras

RTSP is how most security cameras hand out their video. If VisionMesh cannot find your camera
automatically, adding it by address always works.

## What you need

- A camera that provides an RTSP stream, which nearly all IP cameras do
- Its address, username and password
- **ffmpeg installed on the server**

Without ffmpeg the RTSP option is switched off and labelled, rather than failing when pressed.

```bash
sudo apt install ffmpeg
```

## Step 1 — Find the camera's address

The RTSP address is usually in the manual, in the camera's own web page under Network or Stream,
or in the app that came with it. It looks like:

```
rtsp://192.168.1.50:554/stream1
```

Some common patterns, by manufacturer:

| Make | Typical address |
|---|---|
| Hikvision | `rtsp://user:pass@ADDRESS:554/Streaming/Channels/101` |
| Dahua | `rtsp://user:pass@ADDRESS:554/cam/realmonitor?channel=1&subtype=0` |
| Reolink | `rtsp://user:pass@ADDRESS:554/h264Preview_01_main` |
| Amcrest | `rtsp://user:pass@ADDRESS:554/cam/realmonitor?channel=1&subtype=0` |
| TP-Link Tapo | `rtsp://user:pass@ADDRESS:554/stream1` |
| Ubiquiti | `rtsp://ADDRESS:7447/STREAM_ID` |

These are starting points, not guarantees. Check your camera's own documentation.

Enter the address **without** the username and password. VisionMesh asks for those separately and
encrypts them, rather than storing them inside a URL.

## Step 2 — Add it

**Add camera** → **RTSP stream**. Enter the address, the username and the password.

Leave **Connection type** on automatic unless you have a reason not to.

## Step 3 — Name it and choose quality

Give it a name and pick a picture size and frame rate.

Ask for less than the camera's maximum unless you need the detail. A 4K camera at 4K uses roughly
nine times the network and disk of the same camera at 720p, and for a doorway that is usually
nine times nothing.

## Step 4 — Check it works

**Expected result:** a live picture within a few seconds.

If not, open the camera and press **Test connection**. It reports how long the first frame took,
the measured frame rate and bitrate, and the exact error if there was one.

## Common problems

**"The camera rejected the username or password."** Check them in the camera's own web page. Many
cameras need a separate account for streaming, distinct from the admin login used for settings.

**"No video arrived from this camera."** Usually a wrong path. The address before the first `/` is
almost always right; everything after it is manufacturer-specific and easy to get wrong. Try the
patterns above, or look at the camera's web page.

**The picture breaks up or tears.** Change **Connection type** to **TCP** in the camera's
settings. UDP is lower latency but loses packets on a busy or wireless network, and lost packets
in video look like tearing.

**It works, then stops after a few minutes.** Some cameras only allow one or two simultaneous
streams. Close the camera's own app, or anything else watching it. VisionMesh only opens one
connection per camera, and uses that same connection for recording.

**High processor use on the server.** Expected, and worth understanding. See below.

## Advanced

### Why RTSP cameras cost more processor time than USB ones

An RTSP camera sends H.264 or H.265. Browsers cannot play that straight out of a pipe without a
full player stack, so VisionMesh transcodes it to MJPEG for live viewing. That transcode is the
processor cost, and it is unavoidable for the live view.

Recording does **not** pay that cost. When a network camera records continuously, the same ffmpeg
process that produces the live view also writes the camera's original stream to disk with
`-c copy` — no re-encoding, full source quality, and only one connection to the camera.

So a network camera recording continuously costs one transcode for whoever is watching, and
nothing extra for the recording.

### Reducing the load

- Point VisionMesh at the camera's **substream** rather than its main stream. Most cameras offer
  one, typically 640×480, and it is ideal for a live wall.
- Lower the requested frame rate. 10 frames per second is plenty for most surveillance.
- A camera nobody is watching and nothing is recording is stopped entirely, so an idle camera
  costs nothing at all.

### Supported protocols

`rtsp`, `rtsps`, `rtmp`, `rtmps`, `http` and `https`. Anything else is refused.

That refusal is deliberate. The address is handed to ffmpeg, which also speaks `file:` and
`concat:`, and without the check an administrator could be talked into pointing a "camera" at a
local file.

### Credentials

Passwords are encrypted at rest with AES-256-GCM and never returned by the API in any form. The
authenticated URL is built in memory only when ffmpeg is started, and any URL that reaches a log,
an error message or the camera panel has its credentials replaced with `***` first.
