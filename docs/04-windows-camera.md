# Using a Windows PC's camera

Install the VisionMesh agent on a Windows computer, and its webcams become cameras on your server.

You do not need this for phones or network cameras.

## What you need

- A Windows 10 or 11 machine with a camera
- On the same network as the VisionMesh server
- The server already set up

## Step 1 — Download the agent

Get `VisionMesh-Agent-Windows-x64.zip` from
[Releases](https://github.com/unupunct/VISIONMESH/releases) and unzip it somewhere permanent.

## Step 2 — Check the camera is visible

```powershell
.\VisionMesh.Agent.Windows.exe list
```

**Expected result:**

```
CAMERAS FOUND (1)

  Logitech BRIO
    1920 x 1080 at 30 fps, MJPG (forwarded without re-encoding)
    32 formats available
```

If it finds nothing, fix that before going further. The problem is between the camera and Windows,
not with VisionMesh.

## Step 3 — Get a pairing code

On the server: **Devices** → **Add device**. A code appears, valid for ten minutes.

## Step 4 — Pair

```powershell
.\VisionMesh.Agent.Windows.exe pair
```

It asks for the server address and the code.

**Expected result:**

```
Paired with 'Home Surveillance' as 'OFFICE-PC'.
Settings saved to C:\Users\you\AppData\Roaming\VisionMesh\agent.json
```

## Step 5 — Run it

```powershell
.\VisionMesh.Agent.Windows.exe
```

**Expected result:** the computer appears under **Devices** as connected.

## Step 6 — Add the camera

On the server: **Add camera** → **USB or built-in camera**. Pick the computer, pick the camera,
name it, and press **Add camera**.

**Expected result:** a live picture within a few seconds.

## Running it in the background

From an administrator PowerShell:

```powershell
sc.exe create VisionMeshAgent binPath= "C:\VisionMesh\VisionMesh.Agent.Windows.exe" start= auto
```

```powershell
sc.exe start VisionMeshAgent
```

A service runs without anyone signed in, which is what you want for a machine acting as a camera.

## Common problems

**"No cameras were found."** Open Settings → Privacy and security → Camera, and make sure camera
access is on and desktop apps are allowed to use it. Windows blocks this by default on some
installations.

**"The camera is already being used by another program."** Teams, Zoom, Skype or the camera's own
app has it open. Close it. Windows lets only one program use a webcam at a time.

**"Windows reported a device error."** Unplug the camera and plug it back in. If it is on a USB
hub, try a port directly on the machine.

**The agent connects but the camera stays offline.** Run `list` again — the camera may have been
unplugged, or Windows may have given it a different device path after a reboot. If the path
changed, remove the camera in the dashboard and add it again.

**It works until the machine sleeps.** A sleeping computer is not a camera. Set the machine never
to sleep in Power Options, or use a device that does not sleep.

## Advanced

The agent uses Media Foundation directly, so it opens the camera exactly the way Windows does,
which is why the camera's privacy light behaves normally.

Where a camera can produce MJPEG itself — and almost every USB webcam can — the agent asks for
that and forwards the frames without decoding or re-encoding them. That means near-zero processor
use and no quality loss. `list` says "forwarded without re-encoding" when that path is in use.

If a camera cannot produce JPEG, the agent asks Media Foundation for RGB32 and encodes the frames
itself, which does use processor time on that machine.

The agent stores its device token in `%APPDATA%\VisionMesh\agent.json`. Delete that file, or run
`unpair`, to forget the server.
