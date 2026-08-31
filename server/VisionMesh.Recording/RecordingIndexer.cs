using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VisionMesh.Core.Abstractions;
using VisionMesh.Core.Models;
using VisionMesh.Core.Util;
using VisionMesh.Database.Repositories;
using VisionMesh.Streaming.Sources;

namespace VisionMesh.Recording;

/// <summary>
/// Keeps the recordings table in step with the files on disk, and enforces retention.
///
/// ffmpeg writes segment files on its own schedule and does not tell us when one is finished, so
/// the index is built by scanning rather than by being notified. Scanning is also what makes the
/// archive survivable: if the database is lost or a recording happened during a crash, the files
/// are still there and a later scan picks them up. The filesystem is the source of truth; the
/// table is an index over it.
/// </summary>
public sealed class RecordingIndexer(
    CameraRepository cameras,
    RecordingRepository recordings,
    SettingsRepository settings,
    EventRepository events,
    StorageManager storage,
    RecordingEngine engine,
    IRealtimeNotifier notifier,
    ILogger<RecordingIndexer> log) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);
    /// <summary>A file untouched for this long is finished; ffmpeg is still writing anything newer.</summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(15);
    /// <summary>Free space below this triggers a storage warning to the dashboard.</summary>
    private const long LowSpaceBytes = 2L * 1024 * 1024 * 1024;

    private DateTimeOffset _lastStorageWarning = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the recorders get going before the first scan, so a start-up scan does not race
        // ffmpeg creating its first segment.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RunMaintenancePass();
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Recording maintenance pass failed.");
            }

            try { await Task.Delay(ScanInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Adds files that exist on disk but are not yet in the index.</summary>
    /// <summary>
    /// One full maintenance pass: index what has been written, then delete what should no longer
    /// be kept.
    ///
    /// Public so it can be run directly. This deletes footage off disk, and logic that deletes a
    /// user's recordings should be reachable by a test rather than only from inside a timer.
    /// </summary>
    public void RunMaintenancePass()
    {
        IndexNewSegments();
        ApplyRetention();
        ApplyStorageCap();
        CheckFreeSpace();
        PurgeOldEvents();
    }

    private void IndexNewSegments()
    {
        var known = new HashSet<string>(
            recordings.Query(null, null, null, 1000, 0).Select(s => s.FilePath),
            StringComparer.OrdinalIgnoreCase);

        var cutoff = DateTime.UtcNow - SettleTime;

        foreach (var camera in cameras.GetAll())
        {
            var directory = storage.GetCameraDirectory(camera);
            if (!Directory.Exists(directory)) continue;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(directory, "*.mp4", SearchOption.TopDirectoryOnly); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning("Could not list recordings for camera {Camera}: {Error}", camera.Id, ex.Message);
                continue;
            }

            foreach (var file in files)
            {
                if (known.Contains(file)) continue;

                FileInfo info;
                try { info = new FileInfo(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                if (!info.Exists) continue;
                if (info.LastWriteTimeUtc > cutoff) continue;   // still being written

                if (info.Length == 0)
                {
                    // ffmpeg creates the next segment file before it has anything to put in it.
                    // An empty file that has settled is a failed segment, not a recording.
                    TryDelete(file);
                    continue;
                }

                var start = RecordingPlan.ParseStartTime(file)
                            ?? new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero);

                var segment = new RecordingSegment
                {
                    CameraId = camera.Id,
                    FilePath = file,
                    StartUtc = start.ToUniversalTime(),
                    EndUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    SizeBytes = info.Length,
                    // The file itself carries no clue about what caused the recording, so the
                    // engine is asked what it was doing when this segment began. Assuming
                    // Continuous here would label every motion clip wrongly in the timeline.
                    Trigger = engine.TriggerAt(camera.Id, start.ToUniversalTime()),
                    Closed = true,
                };
                segment.Id = recordings.Insert(segment);
                log.LogDebug("Indexed recording {File} ({Size} bytes) for camera {Camera}.", Path.GetFileName(file), info.Length, camera.Id);
            }
        }
    }

    /// <summary>Deletes recordings past their camera's retention window.</summary>
    private void ApplyRetention()
    {
        var globalDays = settings.GetInt(SettingsRepository.Keys.RetentionDays, StorageManager.DefaultRetentionDays);

        foreach (var camera in cameras.GetAll())
        {
            var days = camera.RetentionDays > 0 ? camera.RetentionDays : globalDays;
            if (days <= 0) continue;   // zero means keep forever, which is a valid choice

            var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
            foreach (var segment in recordings.GetExpired(camera.Id, cutoff))
            {
                DeleteSegment(segment, $"older than the {days} day retention window");
            }
        }
    }

    /// <summary>
    /// Enforces an optional total-size cap by deleting the oldest recordings first.
    /// Runs after retention so it only ever removes footage the user still wanted - which is why
    /// it also writes an event, rather than deleting silently.
    /// </summary>
    private void ApplyStorageCap()
    {
        var limit = storage.GetStorageLimitBytes();
        if (limit <= 0) return;

        var used = recordings.GetTotalBytes();
        if (used <= limit) return;

        log.LogInformation("Recordings use {Used} bytes, over the {Limit} byte cap. Removing the oldest.", used, limit);

        var deleted = 0;
        foreach (var segment in recordings.GetOldest(500))
        {
            if (used <= limit) break;
            used -= segment.SizeBytes;
            DeleteSegment(segment, "the storage limit was reached");
            deleted++;
        }

        if (deleted > 0)
        {
            var cameraEvent = new CameraEvent
            {
                Type = EventType.StorageWarning,
                Severity = EventSeverity.Warning,
                TimestampUtc = DateTimeOffset.UtcNow,
                Detail = $"Deleted {deleted} recording(s) to stay within the storage limit.",
            };
            cameraEvent.Id = events.Insert(cameraEvent);
            notifier.EventRaised(cameraEvent);
        }
    }

    private void CheckFreeSpace()
    {
        var info = storage.GetStorageInfo();
        if (info.Error is not null || info.TotalBytes == 0) return;
        if (info.FreeBytes >= LowSpaceBytes) return;

        // Warn at most hourly: a full disk stays full, and repeating the alert every scan would
        // bury everything else in the events list.
        if (DateTimeOffset.UtcNow - _lastStorageWarning < TimeSpan.FromHours(1)) return;
        _lastStorageWarning = DateTimeOffset.UtcNow;

        var message = $"Only {info.FreeBytes / (1024 * 1024)} MB of disk space is left for recordings.";
        log.LogWarning("{Message}", message);

        var cameraEvent = new CameraEvent
        {
            Type = EventType.StorageWarning,
            Severity = EventSeverity.Error,
            TimestampUtc = DateTimeOffset.UtcNow,
            Detail = message,
        };
        cameraEvent.Id = events.Insert(cameraEvent);
        notifier.EventRaised(cameraEvent);
        notifier.StorageWarning(message, info.FreeBytes);
    }

    /// <summary>Events are kept twice as long as footage, capped at 90 days, so the log stays bounded.</summary>
    private void PurgeOldEvents()
    {
        var days = settings.GetInt(SettingsRepository.Keys.RetentionDays, StorageManager.DefaultRetentionDays);
        if (days <= 0) return;

        var keepDays = Math.Min(90, Math.Max(days * 2, 14));
        events.PurgeOlderThan(DateTimeOffset.UtcNow.AddDays(-keepDays));
    }

    private void DeleteSegment(RecordingSegment segment, string reason)
    {
        if (TryDelete(segment.FilePath))
        {
            recordings.Delete(segment.Id);
            log.LogDebug("Deleted recording {File} because it was {Reason}.", Path.GetFileName(segment.FilePath), reason);
        }
        else if (!File.Exists(segment.FilePath))
        {
            // Already gone from disk: drop the row so the index stops claiming it exists.
            recordings.Delete(segment.Id);
        }
    }

    private bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Could not delete {File}: {Error}", Ids.SafeFileName(Path.GetFileName(path)), ex.Message);
            return false;
        }
    }
}
