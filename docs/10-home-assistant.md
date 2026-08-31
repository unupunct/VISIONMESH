# Home Assistant

VisionMesh cameras become proper Home Assistant camera entities, with live video, snapshots and
the sensors you need to build automations around them.

This is a real integration, not a page telling you to copy RTSP URLs by hand.

## What you need

- Home Assistant, any recent version
- A VisionMesh server on the same network
- A VisionMesh account to sign in with

Which account matters:

- A **Viewer** account is enough to see cameras and their state.
- An **Operator** account is needed for Home Assistant to start recording, set privacy mode, or
  move a camera.

Make a dedicated account for Home Assistant rather than reusing your own. It makes the audit log
readable, and it can be revoked on its own.

## Step 1 — Install the integration

Copy the `custom_components/visionmesh` folder from the VisionMesh release into your Home
Assistant `config/custom_components` folder, so you end up with:

```
config/custom_components/visionmesh/manifest.json
```

With the Samba or File Editor add-on, or over SSH:

```bash
unzip VisionMesh-HomeAssistant-Integration.zip -d /config
```

## Step 2 — Restart Home Assistant

Settings → System → Restart. The integration is not detected until it does.

## Step 3 — Add it

If VisionMesh is on the same network, Home Assistant will usually have found it already: look
under Settings → Devices & services for a discovered **VisionMesh** card, and press **Configure**.

If not, press **Add integration** and search for VisionMesh.

Enter:

- **Server address** — for example `http://192.168.1.10:8088`
- **Username** and **password** — the VisionMesh account

**Expected result:** a VisionMesh device appears with one sub-device per camera.

## Step 4 — Choose which cameras appear

Press **Configure** on the integration to pick which cameras become entities. By default they all
do.

## What you get

Per camera:

| Entity | What it is |
|---|---|
| `camera.front_door` | Live video and snapshots |
| `binary_sensor.front_door_online` | Whether the camera is sending video |
| `binary_sensor.front_door_motion` | Whether motion is happening now |
| `binary_sensor.front_door_recording` | Whether it is recording now |
| `sensor.front_door_frame_rate` | Measured frames per second |
| `sensor.front_door_bitrate` | Measured bitrate |
| `sensor.front_door_latency` | Measured latency |
| `sensor.front_door_state` | Online, Offline, Degraded, Paused or Privacy |
| `switch.front_door_privacy_mode` | Stops capture and recording entirely |
| `switch.front_door_record` | Starts and stops recording |
| `button.front_door_pan_left` and friends | PTZ, on cameras that support it |

Per server:

| Entity | What it is |
|---|---|
| `sensor.visionmesh_cameras_online` | How many cameras are up |
| `sensor.visionmesh_cameras_total` | How many exist |
| `sensor.visionmesh_cameras_recording` | How many are recording |
| `sensor.visionmesh_storage_used` | Disk used by recordings |

A motion sensor is only created for cameras set to record on motion. A battery sensor is only
created for cameras that actually report one. An entity that would sit permanently unknown is not
created at all.

## Automations

**Motion at the front door turns on the porch light and sends a notification**

```yaml
automation:
  - alias: "Porch light on motion"
    trigger:
      platform: state
      entity_id: binary_sensor.front_door_motion
      to: "on"
    condition:
      condition: sun
      after: sunset
    action:
      - service: light.turn_on
        target: { entity_id: light.porch }
      - service: notify.mobile_app_phone
        data:
          title: "Front door"
          message: "Movement detected"
          data:
            image: /api/camera_proxy/camera.front_door
```

**Tell me when a camera goes offline**

```yaml
automation:
  - alias: "Camera offline"
    trigger:
      platform: state
      entity_id: binary_sensor.garage_online
      to: "off"
      for: "00:02:00"
    action:
      - service: notify.mobile_app_phone
        data:
          message: "The garage camera has been offline for two minutes."
```

The `for:` matters. Without it, a brief Wi-Fi hiccup sends a notification.

**Privacy mode when everyone is home**

```yaml
automation:
  - alias: "Privacy when home"
    trigger:
      platform: state
      entity_id: group.family
      to: "home"
    action:
      - service: switch.turn_on
        target: { entity_id: switch.living_room_privacy_mode }
```

**Record while a door is open**

```yaml
automation:
  - alias: "Record while the back door is open"
    trigger:
      platform: state
      entity_id: binary_sensor.back_door
    action:
      - service: "switch.turn_{{ 'on' if trigger.to_state.state == 'on' else 'off' }}"
        target: { entity_id: switch.backyard_record }
```

## Common problems

**"Could not reach VisionMesh at that address."** Check the address from Home Assistant itself, not
from your laptop. In a container or on Home Assistant OS, `localhost` means Home Assistant, not
your VisionMesh machine — use the IP address.

**"That username or password was not accepted."** Sign in to the VisionMesh dashboard with the
same details to confirm them. Note that VisionMesh throttles repeated failures, so wait a minute
after several attempts.

**Cameras appear but show no picture.** Home Assistant is reaching the API but not the video.
Check the address is reachable from Home Assistant, and that the account has at least Viewer.

**The recording switch fails with an error.** Almost always ffmpeg missing on the VisionMesh
server. The error passes the server's own message through, which says so.

**Entities went unavailable after a VisionMesh restart.** They come back on the next poll, within
about five seconds. If they do not, the session may have been revoked; Home Assistant will offer
re-authentication.

**Motion never triggers.** The camera has to be set to **Record when something moves** in
VisionMesh. Motion detection does not run on cameras that are not using it.

## Advanced

### How it works

The integration polls `/api/homeassistant/entities` every five seconds through a single
coordinator, so twenty cameras with six entities each make one request, not 120.

Live video does not go through that loop. The camera entity returns a stream URL and Home
Assistant fetches the video straight from VisionMesh. Each request mints a token scoped to one
camera and valid for two minutes, so a URL that ends up in a browser or on a cast device grants
very little.

### Entity IDs

Unique IDs are built from the VisionMesh camera id, which never changes for the life of the
camera. Renaming a camera or changing its IP address does not orphan the entity or create a
duplicate.

### Motion

VisionMesh raises motion *events*; Home Assistant wants a *state*. The integration tracks the
newest motion event per camera and holds the binary sensor on while VisionMesh is recording that
motion, which is the same window the server treats as one motion episode. That keeps the sensor
in step with what actually gets recorded, rather than inventing a separate timeout.

### MQTT

MQTT discovery is available separately, in VisionMesh under Settings → Home Assistant. It publishes
the same state values independently of this integration, which is useful if you want automations
to keep working while the integration reloads.

Video deliberately does not travel over MQTT. It can, technically, and every frame would then be a
round trip through your broker — making the broker the bottleneck for your whole smart home.

### Diagnostics

Download diagnostics from the integration's menu when reporting a bug. Addresses, usernames,
passwords and stream URLs are redacted, because these files routinely end up attached to public
issues.
