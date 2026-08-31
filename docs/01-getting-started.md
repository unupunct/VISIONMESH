# Getting started

This is the shortest path from nothing to a camera on your screen.

## What VisionMesh is

VisionMesh turns cameras of all kinds into one system you watch from one place. A webcam plugged
into a computer, an old phone on a windowsill, and a proper security camera on your network all
end up side by side on the same screen.

Everything runs on your own hardware. There is no cloud, no account to create, and no
subscription. Your footage never leaves your network unless you deliberately send it somewhere.

## The three pieces

**The server** is the part you install. It keeps the settings, the recordings and the users, and
it is what your browser and your phone connect to. You need exactly one, on a machine that stays
switched on.

**An agent** is a small program you install on another computer so its camera can be used. You
only need one if you want to use a camera plugged into a machine that is not the server.

**Network cameras** need neither. The server talks to them directly.

## What you need

- A computer that stays on: an old desktop, a mini PC, a NUC, or a home server. It does not need
  to be powerful.
- Enough disk space for however much footage you want to keep. A 720p camera recording all the
  time uses roughly 20-40 GB a week.
- At least one camera.

## Step 1 — Install the server

**Linux**

```bash
wget https://raw.githubusercontent.com/unupunct/VISIONMESH/main/install.sh
```

Read it before running it. Then:

```bash
chmod +x install.sh && sudo ./install.sh
```

**Windows** — download the release, unzip it, and run `VisionMesh.Server.exe`.

Full detail: [Linux server](02-linux-server.md) · [Windows server](03-windows-server.md)

## Step 2 — Open the dashboard

The installer prints an address like `http://192.168.1.10:8088`. Open it in any browser on the
same network.

You will see the setup wizard. It asks five short questions, each with a working default:

1. A name for this server
2. An administrator username and password
3. Where recordings should go
4. How long to keep them
5. A confirmation

**Expected result:** the dashboard, with an empty camera wall and an **Add camera** button.

## Step 3 — Add your first camera

The easiest first camera is a phone, because there is nothing to install.

1. Press **Add camera**
2. Choose **Android phone** or **iPhone / iPad**
3. Point the phone's camera at the code on screen and open the link it offers
4. Give the camera a name, such as "Front door"
5. Press **Pair with server**, then **Start camera**

**Expected result:** within a few seconds, the phone appears on the camera wall with a live
picture and a green **LIVE** badge.

Other kinds of camera:

- [A webcam on this or another computer](04-windows-camera.md)
- [A network camera found automatically](09-onvif.md)
- [A camera added by address](08-rtsp.md)

## Step 4 — Decide what to record

Recording is off by default, so nothing is written to disk until you ask for it.

Open a camera, press **Settings**, and choose under Recording:

- **Record all the time** — keeps everything, uses the most disk, never misses anything
- **Record when something moves** — keeps far less, but only captures what the detector notices
- **Record on a schedule** — for a camera you only care about overnight
- **Only when I press record** — leaves the camera live but recording nothing

Recording needs [ffmpeg](12-recording.md#ffmpeg-is-required). If it is not installed, VisionMesh
says so plainly and the recording controls are switched off rather than silently doing nothing.

## Step 5 — Watch from your phone

On the same Wi-Fi, open the same address in the phone's browser and sign in. On Android and iOS
you can add it to the home screen, and it behaves like an app.

To watch from outside your home, do **not** forward a port. Use a private network instead:
[Remote access with Tailscale](11-tailscale.md).

## Where to go next

| | |
|---|---|
| [Recording](12-recording.md) | What gets recorded and for how long |
| [Motion detection](13-motion.md) | How it works and how to stop false alarms |
| [Storage](14-storage.md) | Where footage goes and how long it lasts |
| [Home Assistant](10-home-assistant.md) | Cameras and automations in your smart home |
| [Troubleshooting](15-troubleshooting.md) | When something is not working |
| [Security](16-security.md) | What VisionMesh does with your footage |

## Common problems

**The dashboard does not open.** Check the server is running: on Linux,
`sudo systemctl status visionmesh`. Check you are using the right address — the Network page
lists every address the server can be reached on. Check the phone or laptop is on the same
network, not on a guest Wi-Fi.

**The setup wizard will not accept my password.** It has to be at least 10 characters. A short
phrase you will remember works better than a scrambled word you will not.

**"ffmpeg is not installed".** That is expected on a fresh machine, and USB and phone cameras
work without it. Install it when you want network cameras or recording:
`sudo apt install ffmpeg`.
