# The VisionMesh server on Windows

## What you need

- Windows 10 or 11, 64-bit
- A machine that stays switched on
- Somewhere with disk space for recordings

Nothing else. The download is self-contained, so there is no runtime to install first.

## Step 1 — Download and unzip

Get `VisionMesh-Server-Windows-x64.zip` from
[Releases](https://github.com/unupunct/VISIONMESH/releases) and unzip it somewhere permanent, such
as `C:\VisionMesh`. Do not run it from your Downloads folder.

## Step 2 — Run it

Double-click `VisionMesh.Server.exe`. A console window opens and prints something like:

```
VisionMesh Server 1.0.0 starting.
Dashboard: http://192.168.1.10:8088
```

Windows Firewall will ask whether to allow it. Allow it on **private networks**, so other devices
in your home can reach the dashboard. There is no reason to allow it on public networks.

## Step 3 — Open the dashboard

Open `http://localhost:8088` on the same machine, or the address it printed from another device.

**Expected result:** the setup wizard.

## Step 4 — Run it in the background

While the console window is open, closing it stops the server. To have it start with Windows and
keep running, install it as a service.

From an **administrator** PowerShell. Note the space after each `=`, which `sc.exe` requires:

```powershell
sc.exe create VisionMesh binPath= "C:\VisionMesh\VisionMesh.Server.exe" start= auto
```

```powershell
sc.exe start VisionMesh
```

**Expected result:** `sc.exe query VisionMesh` reports `RUNNING`, and the dashboard is reachable
after a reboot without anyone signing in.

To remove it:

```powershell
sc.exe stop VisionMesh; sc.exe delete VisionMesh
```

## Where data goes

`C:\ProgramData\VisionMesh` holds the database and the encryption key. Recordings go wherever you
chose in the wizard.

Back that folder up. In particular keep `secret.key`: without it, saved camera passwords cannot be
decrypted and have to be entered again.

## Installing ffmpeg

Needed for network cameras and recording. USB and phone cameras work without it.

Download a build from [ffmpeg.org](https://ffmpeg.org/download.html) and either add it to `PATH`
or drop `ffmpeg.exe` beside `VisionMesh.Server.exe`. VisionMesh finds it either way, and
Settings → Advanced shows what it found.

## Using the webcam in this computer

Install the Windows agent on the same machine and pair it with `http://localhost:8088`. See
[Windows camera](04-windows-camera.md).

## Common problems

**"Another program may already be using that port."** Something else has 8088. Start with a
different one:

```powershell
.\VisionMesh.Server.exe --port 9000
```

**Other devices cannot reach the dashboard.** The firewall rule was probably declined, or set to
public networks only. Check Windows Defender Firewall → Allow an app, and make sure VisionMesh is
ticked for private networks.

**The service starts and immediately stops.** Look in Event Viewer under Windows Logs →
Application. The usual cause is the data folder not being writable by the account the service runs
as.

**SmartScreen warns about the download.** The releases are not code-signed. Verify the checksum
against `SHA256SUMS.txt` in the release, which is a stronger check than a signature anyway:

```powershell
Get-FileHash VisionMesh-Server-Windows-x64.zip -Algorithm SHA256
```

## Advanced

The server accepts `--port`, `--data` and `--no-api-docs`, and reads `VISIONMESH_PORT` and
`VISIONMESH_DATA` from the environment. `--help` lists everything.

To run the service as a specific account, so it can reach a network share for recordings, set it
in `services.msc` under Log On.
