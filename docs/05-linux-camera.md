# Using a Linux PC's camera

Install the VisionMesh agent on a Linux computer, and its cameras become cameras on your server.
This is also how you use a webcam plugged into the server itself.

## What you need

- A 64-bit Linux machine with a camera
- On the same network as the VisionMesh server
- Permission to use the camera, which means being in the `video` group

## Step 1 — Download and unpack

```bash
sudo mkdir -p /opt/visionmesh-agent
```

```bash
sudo tar -xzf VisionMesh-Agent-Linux-x64.tar.gz -C /opt/visionmesh-agent
```

Use the `arm64` archive on a Raspberry Pi or other arm64 machine.

## Step 2 — Get permission to use the camera

```bash
sudo usermod -aG video $USER
```

Sign out and back in. This is the single most common reason a Linux camera does not work, and it
is not optional.

## Step 3 — Check the camera is visible

```bash
/opt/visionmesh-agent/visionmesh-agent list
```

**Expected result:**

```
CAMERAS FOUND (1)

  HD Pro Webcam C920
    /dev/video0
    1920 x 1080 at 30 fps, MJPG (forwarded without re-encoding)
    12 formats available
    uvcvideo · usb-0000:00:14.0-1
```

If it finds nothing, fix that before going further.

## Step 4 — Get a pairing code

On the server: **Devices** → **Add device**.

## Step 5 — Pair

```bash
/opt/visionmesh-agent/visionmesh-agent pair
```

It asks for the server address and the code. If the agent is on the server itself, use
`http://localhost:8088`.

## Step 6 — Run it

```bash
/opt/visionmesh-agent/visionmesh-agent
```

**Expected result:** the computer appears under **Devices** as connected.

## Step 7 — Add the camera

On the server: **Add camera** → **USB or built-in camera**. Pick the computer, pick the camera,
name it, and add it.

## Running it in the background

The agent ships with a systemd unit. It runs as its own account, so edit `User=` to match the
account you paired with — the pairing token lives in that account's config directory.

```bash
sudo cp /opt/visionmesh-agent/visionmesh-agent.service /etc/systemd/system/
```

```bash
sudo systemctl daemon-reload && sudo systemctl enable --now visionmesh-agent
```

```bash
sudo journalctl -u visionmesh-agent -f
```

## Common problems

**"No cameras were found."** Almost always the `video` group. Check:

```bash
groups | grep video
```

If `video` is not listed, run the `usermod` above and sign out and back in. A new shell is not
enough; the group membership is attached at login.

**"VisionMesh does not have permission to use /dev/video0."** The same problem, reported more
precisely.

**"The camera is already being used by another program."** Find what has it:

```bash
sudo fuser -v /dev/video0
```

**A camera appears that is not a camera.** Some drivers, especially on Raspberry Pi, create several
`/dev/video*` nodes for one physical camera — for encoders and metadata as well as capture.
VisionMesh asks each node what it is and only lists the ones that actually capture video, so pick
the one with a sensible name.

**"This camera offers no video format VisionMesh can use."** The camera only produces formats the
agent cannot turn into JPEG, such as H.264 or a raw Bayer format. Check what it offers:

```bash
v4l2-ctl --device /dev/video0 --list-formats-ext
```

**It works, but the processor is busy.** The camera is probably not producing MJPEG, so the agent
is encoding every frame. `list` shows which format was chosen. Lowering the frame rate helps more
than lowering the resolution.

## Advanced

The agent uses V4L2 with memory-mapped streaming — `VIDIOC_REQBUFS`, `mmap`, `VIDIOC_QBUF` and
`VIDIOC_DQBUF` — rather than `read()`. `read()` copies every frame through the kernel an extra
time, and plenty of drivers do not implement it at all.

Formats are tried in order: MJPEG, JPEG, YUYV, RGB24, BGR24. The first two are forwarded
untouched. The rest are encoded to JPEG by the agent using its own encoder, which exists so the
agent has no imaging library dependency.

`VIDIOC_S_FMT` is a negotiation, not a command: the driver writes back what it will actually
deliver, which may be a different size from the one requested. The agent reports the negotiated
format, so what the dashboard shows is what the camera is really doing.

The agent stores its token in `$XDG_CONFIG_HOME/visionmesh/agent.json`, or
`~/.config/visionmesh/agent.json`. Running as a system account with no home directory, it falls
back to `/etc/visionmesh/agent.json`.
