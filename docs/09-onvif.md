# ONVIF cameras

ONVIF is a standard that lets cameras describe themselves. VisionMesh uses it to find cameras on
your network and configure them for you, so in most cases you pick yours from a list rather than
typing anything.

## What you need

- A camera that supports ONVIF, which most IP cameras made since about 2012 do
- The camera on the same network segment as the server
- Its username and password
- **ffmpeg installed on the server**

## Step 1 — Search

**Add camera** → **IP camera**.

VisionMesh asks every camera on the local network to identify itself and lists the ones that
answer. This takes about five seconds.

**Expected result:** a list of cameras with their names, models and addresses.

## Step 2 — Enter the camera's credentials

Pick your camera and enter its username and password.

These are the ones you set when you first configured the camera. If you never changed them, check
the label on the camera or its manual. `admin` with a blank password, or `admin`/`admin`, is
common on cameras that have never been set up — and is worth changing before anything else.

## Step 3 — Choose the quality

VisionMesh reads the camera's media profiles and lists each as a quality option, with the
resolution, codec and frame rate the camera reports.

The first is normally the main stream, at the camera's full resolution. The others are substreams
at lower resolutions. For a camera on a wall of several, a substream is usually the better choice:
it looks the same at tile size and uses a fraction of the network.

Profiles that support pan, tilt and zoom are marked.

## Step 4 — Name it and add it

**Expected result:** a live picture within a few seconds, and PTZ controls on the camera panel if
the camera supports them.

## Common problems

**"No cameras answered."** Discovery uses multicast, which does not cross network boundaries. If
the camera is on a different subnet or VLAN — a common setup for security cameras — it will not
answer. Add it by address instead: [RTSP cameras](08-rtsp.md).

Some cameras also ship with ONVIF switched off. Check the camera's own web page under Network,
Advanced or Integration Protocol.

**"The camera did not accept those details."** Wrong username or password. Note that many cameras
need a separate ONVIF account, created in their own web page, distinct from the admin login.

**"The camera answered but does not offer a media service."** It supports part of ONVIF but not
the part that describes video. Add it by RTSP address instead.

**A profile has no stream URL.** Some cameras advertise profiles they cannot actually stream.
VisionMesh marks those and you should pick another.

**PTZ controls do not appear.** The camera did not report a PTZ service, or the profile you chose
has no PTZ configuration. Try another profile — on many cameras only the main one supports it.

**PTZ moves the wrong way.** Some cameras invert tilt. There is no correction for that in
VisionMesh; the camera's own web page usually has a setting for it.

## Advanced

### How discovery works

WS-Discovery: a SOAP probe sent by UDP multicast to `239.255.255.250:3702`, asking for devices of
type `NetworkVideoTransmitter`. Cameras that match answer with their service address and a set of
scopes carrying their name, model and location.

The probe is sent from **every** usable network interface rather than just the default route. A
server with a separate camera VLAN, or with Docker or Tailscale interfaces, would otherwise probe
the wrong network and report that you have no cameras. It is sent twice, because cameras
occasionally miss a single multicast datagram.

### What VisionMesh asks the camera

1. `GetServices`, falling back to `GetCapabilities` on older cameras, to find the media and PTZ
   endpoints
2. `GetDeviceInformation` for the manufacturer, model, firmware and serial
3. `GetProfiles` for the available encoder configurations
4. `GetStreamUri` per profile, for the RTSP address
5. `GetSnapshotUri`, which is optional in the standard and often absent

Authentication is WS-Security UsernameToken with a password digest: a SHA-1 over a nonce, a
timestamp and the password. The password itself never crosses the wire.

### Why the client is hand-written

The generated stack from the official WSDLs pulls in WCF, and real cameras deviate from their own
schemas often enough that a forgiving reader is more reliable than a strict one. Every accessor in
the VisionMesh client tolerates a missing element, because a camera that omits a field is far more
common than one that is specification-perfect.

### Identity

Cameras are identified by their ONVIF endpoint reference, usually a `urn:uuid`, not by IP address.
A camera whose DHCP lease changes keeps working.

### PTZ

Pan, tilt and zoom use ONVIF continuous move, with velocities normalised to -1 to 1. Continuous
move keeps going until it is told to stop, so the dashboard sends a stop when you release a
control. If it did not, the camera would keep turning until it hit its limit.

Home Assistant gets buttons rather than a joystick: each press nudges the camera briefly and stops
it again, which is what a button can honestly express.
