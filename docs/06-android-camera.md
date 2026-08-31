# Using an Android phone as a camera

An old phone makes a good camera. It has a decent lens, Wi-Fi, and it is already sitting in a
drawer.

There is nothing to install. VisionMesh turns the phone's browser into a camera.

## What you need

- An Android phone or tablet with a working camera
- On the same Wi-Fi as the VisionMesh server
- A charger — this is not optional, see below
- **The server reachable over HTTPS**, or over a private network such as Tailscale

That last point matters. Browsers only allow a page to use the camera over a secure connection.
On a plain `http://192.168.x.x` address the phone will refuse, and VisionMesh will say so rather
than leaving you at a permission prompt that never appears. See [Advanced](#advanced) below.

## Step 1 — Get a pairing code

On the server: **Add camera** → **Android phone**. A QR code appears.

## Step 2 — Scan it

Point the phone's normal camera app at the code and open the link it offers. No app to install,
no store account.

If scanning does not work, open the camera page address shown under the code and type the pairing
code in by hand.

## Step 3 — Name it and pair

Give the camera a name, such as "Front door", and press **Pair with server**.

## Step 4 — Choose the camera and quality

- **Rear camera** is usually what you want. The front camera is fine for a desk.
- **Balanced**, 720p at 15 frames per second, suits almost everything.
- **Low power**, 720p at 8 frames per second, is the right choice for a phone running for weeks.
- **High quality**, 1080p at 20 frames per second, uses noticeably more battery, heat and network.

## Step 5 — Start it

Press **Start camera** and allow the camera permission when the browser asks.

**Expected result:** the preview appears, the badge reads **STREAMING**, and the phone shows up on
the camera wall within a few seconds.

## Keeping it running

**The page has to stay open with the screen on.** Android suspends a background tab within
seconds, and no amount of cleverness changes that. VisionMesh requests a screen wake lock so the
display does not switch off by itself, but the page still has to be in the foreground.

For a phone that will run for weeks:

- Keep it plugged in. Streaming video uses the camera, the processor and the radio continuously; a
  phone doing this will not last a day on battery and will get warm.
- Add the page to your home screen, from the browser menu. It then opens without browser chrome
  and behaves more like an app.
- Turn screen brightness right down. It does not affect the picture the camera captures.
- Exclude the browser from battery optimisation, under Settings → Apps → Battery.

## Common problems

**"This page needs a secure connection."** The phone reached VisionMesh over plain HTTP.
See [Advanced](#advanced).

**"Camera access was refused."** Allow the camera for that site in the browser's site settings.
On Chrome: the padlock or the icon left of the address, then Permissions.

**It streams, then stops after a minute.** The screen switched off or the page went to the
background. That is Android suspending the page, not a fault in VisionMesh.

**The picture is choppy.** Wi-Fi. Move the phone closer to the access point, or choose **Low
power**, which halves the frame rate.

**The phone gets hot.** Choose a lower quality profile. 1080p at 20 frames per second is real
work for a phone.

**It says paired but never appears.** Check the phone is on the same network and not on a guest
Wi-Fi, which usually blocks devices from talking to each other.

## Advanced

### Why HTTPS is required

`getUserMedia`, the browser interface for using a camera, is only available in a secure context.
That means HTTPS, or `localhost`. This is a browser rule, not a VisionMesh one, and it exists to
stop any site on any network from silently opening a camera.

Two practical ways round it:

- **Tailscale.** Devices get a `*.ts.net` name with a real certificate, so the phone reaches
  VisionMesh over HTTPS with no configuration. This also makes the camera work from outside your
  home. See [Remote access with Tailscale](11-tailscale.md).
- **A reverse proxy with a certificate.** Caddy or nginx in front of VisionMesh, with a
  certificate from Let's Encrypt or your own authority.

### What the browser camera actually is

It is a complete VisionMesh agent implemented in the page. It claims a pairing code for a device
token, holds open the same WebSocket the Windows and Linux agents use, advertises its cameras, and
pushes JPEG frames behind the same binary header. The server cannot tell the difference and does
not need to.

Frames are drawn to a canvas and encoded with the browser's own JPEG encoder, then paced to the
requested frame rate. If encoding takes longer than the frame interval, which happens on older
phones at high quality, the next tick is skipped rather than queued — stacking frames up would
grow memory until the tab was killed.

Battery level and network type are reported back and shown in the dashboard, when the browser
exposes them.

### A native app

There is no native Android app yet. The browser camera covers the same ground for most people, and
the agent protocol is documented, so a native app would be additive rather than a rewrite. What a
native app would add is background operation, which is the one thing a page genuinely cannot do.
