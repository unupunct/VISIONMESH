# Installing VisionMesh

VisionMesh is one server, plus optionally an agent on any other computer whose camera you want to
use. Phones need nothing installed at all.

---

## Linux

### The quick way

```bash
wget https://raw.githubusercontent.com/unupunct/VISIONMESH/main/install.sh
```

Read it first. You should read any script you are about to run as root.

```bash
chmod +x install.sh
sudo ./install.sh
```

The installer detects your distribution and architecture, downloads the right release, creates a
service user, installs a systemd unit, starts VisionMesh, and prints the address to open. Running
it again upgrades in place and keeps your database, recordings and settings.

### What it creates

| Path | What it is |
|---|---|
| `/opt/visionmesh` | The program |
| `/var/lib/visionmesh` | Database and encryption key |
| `/var/lib/visionmesh/recordings` | Recordings, unless you choose otherwise |
| `/etc/systemd/system/visionmesh.service` | The service |
| user `visionmesh` | A service account with no login shell |

### Managing it

```bash
sudo systemctl status visionmesh
```

```bash
sudo systemctl restart visionmesh
```

```bash
sudo journalctl -u visionmesh -f
```

### Uninstalling

```bash
sudo ./install.sh --uninstall
```

Your recordings and database are left in place. Remove `/var/lib/visionmesh` yourself if you want
them gone.

---

## Windows

1. Download the Windows release from [Releases](https://github.com/unupunct/VISIONMESH/releases).
2. Unzip it somewhere permanent, such as `C:\VisionMesh`.
3. Run `VisionMesh.Server.exe`.
4. Open `http://localhost:8088`.

### Running it in the background

From an administrator PowerShell. Note the space after each `=`, which `sc.exe` requires:

```powershell
sc.exe create VisionMesh binPath= "C:\VisionMesh\VisionMesh.Server.exe" start= auto
```

```powershell
sc.exe start VisionMesh
```

To remove the service:

```powershell
sc.exe stop VisionMesh; sc.exe delete VisionMesh
```

### Where data goes

`C:\ProgramData\VisionMesh` holds the database and the encryption key. Recordings go wherever you
chose in the setup wizard.

---

## ffmpeg

Needed for RTSP and ONVIF cameras, and for recording. USB and phone cameras work without it.

```bash
sudo apt install ffmpeg
```

```bash
sudo dnf install ffmpeg
```

On Windows, download a build from [ffmpeg.org](https://ffmpeg.org/download.html) and either put it
on `PATH` or drop `ffmpeg.exe` beside `VisionMesh.Server.exe`. VisionMesh finds it either way, and
Settings → Advanced shows what it found.

---

## Camera agents

Only needed for a camera plugged into a computer that is **not** the server.

### Windows

```powershell
.\VisionMesh.Agent.Windows.exe pair
```

```powershell
.\VisionMesh.Agent.Windows.exe
```

To run it in the background:

```powershell
sc.exe create VisionMeshAgent binPath= "C:\VisionMesh\VisionMesh.Agent.Windows.exe" start= auto
```

### Linux

```bash
sudo mkdir -p /opt/visionmesh-agent && sudo tar -xzf VisionMesh-Agent-Linux-x64.tar.gz -C /opt/visionmesh-agent
```

```bash
sudo /opt/visionmesh-agent/visionmesh-agent pair
```

The agent needs permission to use the camera:

```bash
sudo usermod -aG video $USER
```

Sign out and back in for that to take effect, then check it worked:

```bash
visionmesh-agent list
```

---

## Building from source

You need the .NET 8 SDK and nothing else.

```bash
git clone https://github.com/unupunct/VISIONMESH.git && cd VISIONMESH && dotnet test
```

```bash
dotnet publish server/VisionMesh.Server -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o out
```

Replace the runtime identifier with `win-x64` or `linux-arm64` as needed.
`scripts/build-release.sh` builds every target and writes checksums.

---

## Upgrading

Run the installer again on Linux, or replace the files on Windows. Database migrations run
automatically at startup and are forward-only, so your cameras, users and recordings survive.

Back up `/var/lib/visionmesh` (or `C:\ProgramData\VisionMesh`) before a major upgrade. In
particular, keep `secret.key`: without it, saved camera passwords cannot be decrypted and have to
be entered again.
