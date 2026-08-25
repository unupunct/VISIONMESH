using System.Globalization;

namespace VisionMesh.Streaming.Sources;

/// <summary>
/// Where and how a camera's recordings are written.
///
/// Files are named from the wall-clock time they start, which makes the archive readable
/// straight from a file manager and lets the indexer recover a segment's start time after a
/// crash without consulting the database.
/// </summary>
public sealed record RecordingPlan(string Directory, int SegmentSeconds)
{
    /// <summary>ffmpeg strftime pattern for segment names, e.g. <c>20260825-143000.mp4</c>.</summary>
    public const string FilePattern = "%Y%m%d-%H%M%S.mp4";

    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>Builds the name a segment starting now would get, matching <see cref="FilePattern"/>.</summary>
    public static string BuildFileName(DateTimeOffset startLocal)
        => startLocal.ToString(TimestampFormat, CultureInfo.InvariantCulture) + ".mp4";

    /// <summary>
    /// Recovers a segment's start time from its file name.
    /// ffmpeg writes these in local time, so the value is interpreted as local and converted.
    /// </summary>
    public static DateTimeOffset? ParseStartTime(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        if (!DateTime.TryParseExact(name, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return null;

        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local));
    }
}
