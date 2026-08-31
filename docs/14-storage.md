# Storage

## Where recordings go

You chose the folder during setup. Change it under **Settings** → Recordings folder.

| System | Default |
|---|---|
| Linux | `/var/lib/visionmesh/recordings` |
| Windows | `C:\ProgramData\VisionMesh\Recordings` |

Point it anywhere with space: a second disk, a large drive, or a NAS.

```
D:\Cameras
/mnt/surveillance
/srv/nas/cctv
```

Press **Test this folder** after changing it. VisionMesh checks by actually writing a file, because
checking existence and permissions separately gives the wrong answer often enough — on network
shares, read-only mounts and SELinux systems — to be worth the round trip.

## The storage page

**Storage** in the sidebar shows:

- How much the recordings use, and how much of the disk is free
- How long the free space would last, projected from what this server has actually been writing
- How much each camera is using

That projection is measured, not assumed. Every other tool in this space multiplies a guessed
bitrate by a guessed number of hours; VisionMesh divides free space by what it has really written,
and says nothing at all until there is enough history to be honest about it.

## How long footage is kept

Each camera has its own retention period. Once a recording is older than that, it is deleted
automatically.

Set it to 0 to keep everything until the disk fills up.

There is also an optional **total storage limit** under Settings → Advanced. When recordings exceed
it, the oldest are deleted even if they are within their retention period, and an event is written
so it never happens silently.

## Using a NAS

Mount the share first, then point VisionMesh at the mount point. It has no built-in SMB or NFS
client, which is deliberate: the operating system does it better and handles reconnection.

**Linux** — add it to `/etc/fstab` so it survives a reboot:

```
//nas.local/cctv  /mnt/surveillance  cifs  credentials=/etc/samba/cctv,uid=visionmesh,gid=visionmesh,_netdev  0  0
```

The `uid` matters. Without it the share is owned by root and the VisionMesh service account cannot
write to it.

**Windows** — map the drive as the account the service runs as, or use a UNC path directly. A drive
letter mapped by your own user is not visible to a service running as another account, which is a
common and confusing failure.

## Common problems

**"VisionMesh cannot write to that folder."** On Linux, check the `visionmesh` account can:

```bash
sudo -u visionmesh touch /mnt/surveillance/test && sudo -u visionmesh rm /mnt/surveillance/test
```

On Windows, check the account the service runs as has write permission.

**Recordings stopped.** Check free space first. VisionMesh raises a warning event when the disk
drops below 2 GB, but a disk that fills between checks stops recording immediately.

**The disk fills up faster than expected.** Look at the per-camera table on the Storage page. It is
usually one camera at a high resolution, or a camera set to record continuously that you meant to
set to motion.

**"Only N MB of disk space is left."** Reduce retention, set a storage limit, or add space.
VisionMesh will keep deleting the oldest recordings to stay alive, so you lose history rather than
recording.

**The NAS disconnects and recordings vanish.** They have not been deleted, but VisionMesh cannot
see them and its index will drop the rows for files it can no longer find. Reconnect the share; a
later scan picks the files back up, because the filesystem is the source of truth.

## Advanced

### What is actually stored

Recordings are MP4 files on disk. Nothing else. The database holds only paths, time ranges and
sizes — an index over the filesystem rather than a copy of it.

That means the archive survives losing the database. Point a fresh VisionMesh at the same folder
and a scan re-indexes what is there.

### How sizes are measured

From the files themselves, at index time. Not estimated, not derived from a bitrate. The Storage
page adds up real file sizes, which is why it can be trusted for planning.

### Retention and the cap

Retention runs first and deletes what is genuinely past its window. The size cap runs second, and
only removes footage the user still wanted, which is why it writes an event when it does.

Events are kept for twice the retention period, capped at 90 days, so an event log always outlives
the footage it refers to.

### Segment naming

Files are named `YYYYMMDD-HHMMSS.mp4` in local time, in a folder named after the camera's id — not
its name. A camera renamed after a year of recording keeps its archive, and two cameras with the
same name never collide.
