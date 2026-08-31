using VisionMesh.Streaming.Sources;
using Xunit;

namespace VisionMesh.Tests;

/// <summary>
/// The ffmpeg arguments that decide whether a recording is playable.
///
/// This exists because of a bug that only surfaced when recording a real camera. The flags that
/// make each segment survive an interruption have to reach the mp4 muxer *inside* the segment
/// muxer, and the segment muxer does not forward a plain <c>-movflags</c> to it. Written the
/// obvious way, the flags are silently dropped: every segment becomes a normal MP4 whose moov
/// atom is written only when the muxer closes, so an interrupted recording is the right size,
/// appears in the archive, and plays nowhere.
///
/// It fails invisibly, which is exactly the kind of thing worth pinning with a test.
/// </summary>
public class RecordingPlanTests
{
    private static string[] Arguments(int segmentSeconds = 600, string directory = "/recordings/cam")
        => new RecordingPlan(directory, segmentSeconds).BuildSegmentArguments().ToArray();

    [Fact]
    public void FragmentationFlagsGoThroughSegmentFormatOptions()
    {
        var arguments = Arguments();

        var index = Array.IndexOf(arguments, "-segment_format_options");
        Assert.True(index >= 0, "The segment format options are missing, so the mp4 muxer never sees the fragmentation flags.");

        var value = arguments[index + 1];
        Assert.StartsWith("movflags=", value);
        Assert.Contains("frag_keyframe", value);
        Assert.Contains("empty_moov", value);
    }

    [Fact]
    public void APlainMovflagsArgumentIsNeverUsed()
    {
        // The whole point: -movflags looks correct, is accepted by ffmpeg without complaint, and
        // does nothing here. If someone "simplifies" the arguments back to it, this fails.
        Assert.DoesNotContain("-movflags", Arguments());
    }

    [Fact]
    public void FaststartIsNeverRequested()
    {
        // faststart relocates the moov atom in a final rewrite pass. A surveillance recording is
        // normally ended by being stopped, so that pass never runs and the file has no moov.
        Assert.DoesNotContain(Arguments(), argument => argument.Contains("faststart", StringComparison.Ordinal));
    }

    [Fact]
    public void SegmentsAreNamedByTheTimeTheyStart()
    {
        var arguments = Arguments(directory: "/recordings/cam_abc");

        Assert.Contains("-strftime", arguments);
        Assert.Equal("1", arguments[Array.IndexOf(arguments, "-strftime") + 1]);

        // The last argument is the output pattern, and the indexer parses start times back out of
        // these names, so the two have to agree.
        Assert.EndsWith(RecordingPlan.FilePattern, arguments[^1]);
        Assert.Contains("cam_abc", arguments[^1]);
    }

    [Fact]
    public void TheSegmentLengthIsPassedThrough()
    {
        var arguments = Arguments(segmentSeconds: 300);

        var index = Array.IndexOf(arguments, "-segment_time");
        Assert.True(index >= 0);
        Assert.Equal("300", arguments[index + 1]);

        Assert.Equal("mp4", arguments[Array.IndexOf(arguments, "-segment_format") + 1]);
        Assert.Equal("segment", arguments[Array.IndexOf(arguments, "-f") + 1]);
    }

    [Fact]
    public void FileNamesRoundTripThroughTheIndexer()
    {
        // The recorder names files and the indexer reads the start time back out of them. If those
        // two ever disagree, every recording is indexed at the wrong time.
        var when = new DateTimeOffset(2026, 8, 31, 14, 23, 58, TimeSpan.Zero).ToLocalTime();

        var name = RecordingPlan.BuildFileName(when);
        var parsed = RecordingPlan.ParseStartTime($"/recordings/cam/{name}");

        Assert.NotNull(parsed);
        Assert.Equal(when.ToUnixTimeSeconds(), parsed!.Value.ToUnixTimeSeconds());
    }

    [Theory]
    [InlineData("not-a-timestamp.mp4")]
    [InlineData("20260831.mp4")]
    [InlineData("")]
    public void AnUnparseableNameReturnsNullRatherThanGuessing(string name)
        => Assert.Null(RecordingPlan.ParseStartTime($"/recordings/cam/{name}"));
}
