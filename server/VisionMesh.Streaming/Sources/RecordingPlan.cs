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
    /// The ffmpeg arguments that turn an output into timestamped, crash-survivable MP4 segments.
    ///
    /// This lives in one place, and is tested, because of a bug that only appeared under a real
    /// recording: the flags that make a segment playable have to reach the mp4 muxer *inside* the
    /// segment muxer, and the segment muxer does not forward a plain <c>-movflags</c> to it.
    /// Written the obvious way, the flags are silently ignored, every segment is a normal MP4
    /// whose moov atom is only written when the muxer closes, and any recording that is
    /// interrupted is the right size, appears in the archive, and plays nowhere.
    ///
    /// Verified by killing ffmpeg mid-recording both ways: <c>-movflags</c> leaves
    /// "moov atom not found", <c>-segment_format_options</c> leaves a playable file.
    /// </summary>
    public IEnumerable<string> BuildSegmentArguments()
    {
        yield return "-f";
        yield return "segment";
        yield return "-segment_time";
        yield return SegmentSeconds.ToString(CultureInfo.InvariantCulture);
        yield return "-segment_format";
        yield return "mp4";

        // Fragmented output, so a segment stays playable if the recorder stops or the machine
        // loses power mid-write. faststart is deliberately absent: it relocates the moov atom in
        // a final rewrite pass an interrupted recording never reaches.
        yield return "-segment_format_options";
        yield return "movflags=+frag_keyframe+empty_moov+default_base_moof";

        // Segments start on the clock so file names line up with wall time.
        yield return "-segment_atclocktime";
        yield return "1";
        yield return "-reset_timestamps";
        yield return "1";
        yield return "-strftime";
        yield return "1";

        yield return Path.Combine(Directory, FilePattern);
    }

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
