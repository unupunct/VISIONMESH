# The VisionMesh server on Linux

## What you need

- A 64-bit Linux machine that stays switched on. x86_64 or arm64, including a Raspberry Pi 4 or 5
  running the 64-bit version of Raspberry Pi OS.
- systemd, which every mainstream distribution uses.
- Root access, for the installer.

32-bit ARM is not supported. On a Raspberry Pi, install the 64-bit OS.

## Step 1 — Download the installer

```bash
wget https://raw.githubusercontent.com/unupunct/VISIONMESH/main/install.sh
```

## Step 2 — Read it

You are about to run this as root. Read it first. It is a few hundred lines and says what it does
at each step.

```bash
less install.sh
```

## Step 3 — Run it

```bash
chmod +x install.sh && sudo ./install.sh
```

The installer detects your distribution and architecture, downloads the matching release, verifies
its checksum, creates a service account, installs a systemd unit, starts the server, and prints
the address to open.

**Expected result:**

```
VisionMesh 1.0.0 is installed and running.

  Open the dashboard at:  http://192.168.1.10:8088
```

## What it creates

| Path | What it is |
|---|---|
| `/opt/visionmesh` | The program |
| `/var/lib/visionmesh` | Database and encryption key |
| `/var/lib/visionmesh/recordings` | Recordings, unless you choose otherwise |
| `/etc/systemd/system/visionmesh.service` | The service |
| user `visionmesh` | A service account with no login shell |

## Managing the service

```bash
sudo systemctl status visionmesh
```

```bash
sudo systemctl restart visionmesh
```

```bash
sudo journalctl -u visionmesh -f
```

## Installing ffmpeg

Needed for network cameras and recording. USB and phone cameras work without it.

```bash
sudo apt install ffmpeg
```

```bash
sudo dnf install ffmpeg
```

## Using a camera plugged into the server itself

The installer adds the `visionmesh` service account to the `video` group, so a webcam plugged into
the server can be used. Install the agent on the same machine and pair it with
`http://localhost:8088`. See [Linux camera](05-linux-camera.md).

## Changing the port

```bash
sudo ./install.sh --port 9000
```

Or edit `/etc/systemd/system/visionmesh.service`, change `--port`, then reload:

```bash
sudo systemctl daemon-reload && sudo systemctl restart visionmesh
```

## Upgrading

Run the installer again. It replaces the program and leaves your database, recordings and settings
alone.

```bash
sudo ./install.sh
```

## Uninstalling

```bash
sudo ./install.sh --uninstall
```

Your data stays in `/var/lib/visionmesh`. Delete it yourself if you want it gone.

## Common problems

**"This installer needs systemd."** Your distribution uses something else. Download the release,
unpack it, and run `VisionMesh.Server` under whatever service manager you use.

**"Could not work out the latest version."** No internet access, or GitHub is unreachable. Pass a
version explicitly:

```bash
sudo ./install.sh --version v1.0.0
```

**The service will not start.** Read the actual error:

```bash
sudo journalctl -u visionmesh -n 50 --no-pager
```

The two usual causes are another program already using port 8088, and the data directory not being
writable.

**Port already in use.** Find what has it, then reinstall with `--port` set to something else:

```bash
sudo ss -tlnp | grep 8088
```

## Advanced

The systemd unit is deliberately restrictive: `ProtectSystem=strict`, `PrivateTmp`,
`NoNewPrivileges`, and a write path limited to the data directory. A surveillance server should
not be able to do much beyond its own job.

`RestrictAddressFamilies` allows `AF_NETLINK` because the server enumerates network interfaces to
report the addresses it can be reached on. `DeviceAllow=char-video4linux` and
`SupplementaryGroups=video` are what let a camera plugged into the server work.

The server also accepts `VISIONMESH_PORT` and `VISIONMESH_DATA` environment variables, which is
usually tidier than editing a command line in a unit file.
