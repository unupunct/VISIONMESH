# Recording

Recording is off by default. Nothing is written to disk until you ask for it.

## ffmpeg is required

Recording needs [ffmpeg](https://ffmpeg.org/) on the server.

```bash
sudo apt install ffmpeg
```

When it is missing, VisionMesh says so plainly and the recording controls are switched off, rather
than appearing to work and silently recording nothing.

ffmpeg is not bundled because its licensing and per-distribution builds make shipping it inside an
installer a legal and packaging problem.

## Choosing when to record

Open a camera, press **Settings**, and choose under Recording.

**Record all the time.** Keeps everything. The most reliable choice, and the one that uses the
most disk. If you are unsure, start here.

**Record when something moves.** Keeps far less, but only captures what the detector notices.
Good for a quiet area, poor for a busy street. See [Motion detection](13-motion.md).

**Record on a schedule.** Continuous recording, but only inside a time window. Useful for a
camera you only care about overnight. If the end time is earlier than the start, the window runs
across midnight.

**Only when I press record.** The camera stays live but records nothing until you press **Record**
on its panel, or a Home Assistant automation switches it on.

## How long recordings are kept

Each camera has its own retention period, in its settings. Once a recording is older than that, it
is deleted automatically to make room.

Set it to 0 to keep everything until the disk fills up. VisionMesh will then start deleting the
oldest recordings when space runs out, and will tell you it is doing so.

There is also an optional total size cap under Settings → Advanced. When recordings exceed it, the
oldest are deleted even if they are inside their retention period — and an event is written, so
this never happens silently.

## Watching recordings back

**Recordings** in the sidebar. Choose a camera and a day.

The timeline shows blue blocks where there is footage and orange marks where events happened.
Click a blue block to play it, or use the list below for exact times and sizes.

Every clip can be downloaded, and downloads are named after the camera and the time they started.

## Where files go

Recordings are ten-minute MP4 segments, in a folder per camera, named after the time they started:

```
/var/lib/visionmesh/recordings/
    cam_a1b2c3d4e5f6/
        20260831-140000.mp4
        20260831-141000.mp4
```

That is deliberate. The archive is readable straight from a file manager, in time order, without
VisionMesh — which matters if you ever need footage after the server is gone.

Ten minutes is a compromise: short enough that losing a segment to a crash costs little and seeking
is quick, long enough that a week of footage is hundreds of files rather than thousands.

## Common problems

**"Recording needs ffmpeg, which is not installed on the server."** Install it, then check
Settings → Advanced shows it was found.

**Recording is on but nothing appears.** Press **Fix camera** on the camera. It checks whether the
recordings folder is writable, whether there is disk space, and whether the camera is producing
video at all, and reports the first real fault.

**Recordings stop after a while.** Usually the disk filling up. Check **Storage**. VisionMesh
raises a warning event when free space drops below 2 GB.

**Motion recording captures nothing.** Motion has to be tuned for the scene. See
[Motion detection](13-motion.md).

**A recording will not play in the browser.** Segments are written as fragmented MP4, so one that
was interrupted by a stop, a crash or a power cut still plays up to the point it reached. If one
genuinely will not open, check the **Storage** page: a disk that filled up mid-write is the usual
cause.

## Advanced

### Two recording paths, chosen deliberately

**Stream copy**, used for a network camera recording continuously or on a schedule. The same
ffmpeg process that produces the live view also writes the camera's own H.264 to disk with
`-c copy`. No re-encoding, full source quality, near-zero processor cost, and only one connection
to the camera — which matters because many cameras allow very few.

**Re-encode from frames**, used whenever recording has to start and stop on demand — motion,
manual — and whenever the source is genuinely a JPEG stream, which is the case for agents and
phones. Frames are piped into ffmpeg and encoded to H.264 at CRF 26 with a keyframe every two
seconds.

The reason for the split is that a stream copy cannot start and stop without restarting the
connection to the camera, which is far too disruptive to do on every motion event.

### Turning recording on restarts a network camera

Whether a network camera records is part of the ffmpeg command line, so switching recording on or
off restarts its connection. The live view drops for a second or two while that happens, and the
camera briefly shows as offline.

That is the cost of using one connection for both the live view and the recording, which is worth
paying: many cameras allow only one or two connections at all.

### Pre-roll

Motion recordings include a few seconds from *before* the trigger. VisionMesh keeps a short rolling
buffer of recent frames in memory and writes those into the file first.

Without it, every clip starts at the instant the detector fired, which is always a second or two
after the interesting thing entered the frame.

### How segments are indexed

ffmpeg writes segment files on its own schedule and does not report when one is finished, so the
index is built by scanning rather than by being told. A file untouched for fifteen seconds is
finished; anything newer is still being written.

That also makes the archive survivable: if the database is lost, or a recording happened during a
crash, the files are still there and a later scan picks them up. The filesystem is the source of
truth and the table is an index over it, not the other way round.

### Estimating disk use

Rather than guessing from a bitrate, look at the **Storage** page. It reports what this
installation has actually been writing, and projects from that. Before there is enough history to
measure, it says so instead of inventing a figure.

As a very rough starting point, one 720p camera recording continuously uses somewhere between 20
and 40 GB a week, depending on how much movement is in the scene.
