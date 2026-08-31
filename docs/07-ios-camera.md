# Using an iPhone or iPad as a camera

An old iPhone or iPad makes an excellent camera. The lens is usually better than a cheap security
camera, and there is nothing to install.

## What you need

- An iPhone or iPad running iOS or iPadOS 15 or later
- On the same Wi-Fi as the VisionMesh server
- A charger
- **The server reachable over HTTPS**, or over a private network such as Tailscale

Safari only allows a page to use the camera over a secure connection, and unlike some browsers it
gives no way to make an exception for a local address. On a plain `http://192.168.x.x` address it
simply will not work, and VisionMesh says so rather than leaving you at a prompt that never
appears. See [Advanced](#advanced).

## Step 1 — Get a pairing code

On the server: **Add camera** → **iPhone / iPad**. A QR code appears.

## Step 2 — Scan it

Point the Camera app at the code and tap the notification that appears. Safari opens the VisionMesh
camera page with the code already filled in.

## Step 3 — Name it and pair

Give the camera a name, such as "Front door", and tap **Pair with server**.

## Step 4 — Choose the camera and quality

- **Rear camera** for anything outdoors or across a room.
- **Balanced**, 720p at 15 frames per second, suits almost everything.
- **Low power** for a device that will run for weeks.

## Step 5 — Start it

Tap **Start camera** and allow the camera permission when Safari asks.

**Expected result:** the preview appears, the badge reads **STREAMING**, and the device shows up on
the camera wall within a few seconds.

## Keeping it running

**The page has to stay open with the screen on.** iOS suspends a background tab almost
immediately, and locks the screen on its own timer. VisionMesh requests a screen wake lock, which
recent iOS versions honour, but the page still has to be in the foreground.

For a device that will run for weeks:

- Keep it plugged in.
- Add it to the Home Screen: Share, then **Add to Home Screen**. It then opens without Safari's
  chrome and stays put more reliably.
- Settings → Display & Brightness → Auto-Lock → **Never**.
- Turn brightness right down. It does not affect the captured picture.
- Guided Access, under Settings → Accessibility, locks the device to the one page so a stray tap
  cannot leave it.

## Common problems

**"This page needs a secure connection."** Safari reached VisionMesh over plain HTTP. This is the
most common problem on iOS. See [Advanced](#advanced).

**"Camera access was refused."** Settings → Safari → Camera → Allow, or tap the "aA" or page
settings icon in the address bar and allow the camera for that site.

**It streams, then stops when I put the phone down.** The screen locked, or the page went to the
background. Set Auto-Lock to Never.

**The picture stops when a call comes in.** iOS takes the camera for the call. It resumes when the
call ends, and the dashboard shows the camera as offline in between.

**It says paired but never appears.** Check the device is on the same network, and not on a guest
Wi-Fi.

## Advanced

### Getting HTTPS on iOS

Safari is stricter than most browsers, and offers no override for a local address. The two
practical options:

- **Tailscale.** Install it on the server and on the iPhone. Devices get a `*.ts.net` name with a
  real certificate, so Safari is satisfied with no configuration, and the camera also works from
  outside your home. See [Remote access with Tailscale](11-tailscale.md).
- **A reverse proxy with a real certificate.** A self-signed certificate is not enough for
  `getUserMedia` on iOS unless the certificate authority is installed and explicitly trusted on the
  device, which is more work than Tailscale.

### What the browser camera actually is

It is a complete VisionMesh agent implemented in the page: it pairs for a device token, holds open
the same WebSocket the desktop agents use, and pushes JPEG frames behind the same binary header.

### About the App Store

There is no native iOS app. Distributing one is not like publishing a binary on GitHub: it needs
an Apple Developer account, App Store review, and either TestFlight or the App Store for
distribution. Nothing in this repository can shortcut that, and claiming otherwise would be
dishonest.

If you want to build and run one on your own device, Apple's free provisioning lets you install an
app you have built yourself for seven days at a time. The agent protocol is documented in
[Advanced](17-advanced.md), so a native client is a well-defined piece of work rather than a
rewrite.

What a native app would add is background operation. That is the one thing a web page genuinely
cannot do on iOS, and it is the reason a native client is worth building eventually.
