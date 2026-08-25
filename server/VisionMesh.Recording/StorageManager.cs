using Microsoft.Extensions.Logging;
using VisionMesh.Core.Models;
using VisionMesh.Core.Util;
using VisionMesh.Database.Repositories;

namespace VisionMesh.Recording;

/// <summary>
/// Works out where recordings live and how much room is left.
///
/// Every number reported here comes from the filesystem and from indexed file sizes. There are
/// no "estimated hours remaining" figures based on assumed bitrates: retention is projected from
/// what this installation has actually been writing, and when there is not enough history to
/// project from, the answer is "not enough data yet" rather than a confident guess.
/// </summary>
public sealed class StorageManager(SettingsRepository settings, RecordingRepository recordings, ILogger<StorageManager> log)
{
    public const int DefaultRetentionDays = 7;

    /// <summary>Default recordings location, used until the first-run wizard sets one.</summary>
    public static string DefaultRoot => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VisionMesh", "Recordings")
        : "/var/lib/visionmesh/recordings";

    public string GetRoot() => settings.GetString(SettingsRepository.Keys.RecordingsPath, DefaultRoot);

    /// <summary>Per-camera folder. Named with the camera id so a rename never orphans an archive.</summary>
    public string GetCameraDirectory(Camera camera)
        => Path.Combine(GetRoot(), Ids.SafeFileName(camera.Id));

    /// <summary>
    /// Verifies a directory can actually be written to, by writing to it.
    /// Checking existence and permissions separately gives the wrong answer often enough
    /// (network shares, read-only mounts, SELinux) that the write is worth the cost.
    /// </summary>
    public static (bool Writable, string? Error) TestWritable(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".visionmesh-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return (true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return (false, ex.Message);
        }
    }

    public StorageInfo GetStorageInfo()
    {
        var root = GetRoot();
        var info = new StorageInfo
        {
            Path = root,
            Exists = Directory.Exists(root),
            RetentionDays = settings.GetInt(SettingsRepository.Keys.RetentionDays, DefaultRetentionDays),
            UsedByRecordingsBytes = recordings.GetTotalBytes(),
        };

        try
        {
            // DriveInfo needs a path that exists; walk up until we find one so a not-yet-created
            // recordings folder still reports the capacity of the volume it will live on.
            var probe = root;
            while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
            {
                var parent = Path.GetDirectoryName(probe);
                if (parent == probe) break;
                probe = parent;
            }

            if (!string.IsNullOrEmpty(probe))
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(probe)) ?? probe);
                if (drive.IsReady)
                {
                    return info with { TotalBytes = drive.TotalSize, FreeBytes = drive.AvailableFreeSpace };
                }
            }
            return info with { Error = "The storage volume is not available." };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log.LogWarning("Could not read storage capacity for {Path}: {Error}", root, ex.Message);
            return info with { Error = ex.Message };
        }
    }

    /// <summary>Optional hard cap on total recording size, in bytes. Zero means retention days alone apply.</summary>
    public long GetStorageLimitBytes()
    {
        var gigabytes = settings.GetInt(SettingsRepository.Keys.StorageLimitGb, 0);
        return gigabytes <= 0 ? 0 : (long)gigabytes * 1024 * 1024 * 1024;
    }

    /// <summary>
    /// Measured bytes per day across the last week of indexed recordings, or null when there is
    /// not yet a day of history. Used to project how long the disk will last - from real data only.
    /// </summary>
    public double? GetMeasuredBytesPerDay()
    {
        var now = DateTimeOffset.UtcNow;
        var segments = recordings.Query(null, now.AddDays(-7), now, 1000, 0);
        if (segments.Count < 2) return null;

        var oldest = segments.Min(s => s.StartUtc);
        var span = now - oldest;
        if (span < TimeSpan.FromHours(1)) return null;

        var bytes = segments.Sum(s => s.SizeBytes);
        return bytes / span.TotalDays;
    }
}
