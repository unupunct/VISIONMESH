using VisionMesh.Core.Models;
using VisionMesh.Streaming.Sources;
using Xunit;

namespace VisionMesh.Tests;

/// <summary>
/// Which outputs a network camera's ffmpeg command line carries.
///
/// This decides how much processor a camera costs, and it was wrong in a way that only showed up
/// on someone else's machine. The MJPEG output was built unconditionally, so a camera that was
/// merely recording ran a full software decode and re-encode of every frame for nobody. Three
/// cameras recording pinned a processor that otherwise idled at two percent, and the user went
/// and used something else — reasonably.
///
/// Recording copies the camera's own stream and needs no decode. The transcode is now asked for
/// only when something is going to look at the pictures.
/// </summary>
public class PullSourceArgumentTests
{
    private static Camera Camera(int width = 1280, int height = 720, int fps = 15, int quality = 75) => new()
    {
        Id = "cam_test",
        Name = "Front Door",
        SourceKind = CameraSourceKind.Rtsp,
        DesiredWidth = width,
        DesiredHeight = height,
        DesiredFps = fps,
        DesiredQuality = quality,
    };

    private static string[] Arguments(bool liveOutput, RecordingPlan? recording, HardwareDecoder? decoder = null)
        => FfmpegPullSource.BuildArguments(
            Camera(), "rtsp://camera.example/stream", RtspTransport.Tcp, recording, liveOutput, decoder).ToArray();

    private static HardwareDecoder Vaapi() => new("vaapi", "/dev/dri/renderD128", "test");

    private static RecordingPlan Plan() => new("/recordings/cam_test", 600);

    [Fact]
    public void RecordingAloneNeverDecodesAFrame()
    {
        var arguments = Arguments(liveOutput: false, recording: Plan());

        Assert.DoesNotContain("image2pipe", arguments);
        Assert.DoesNotContain("mjpeg", arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("scale=", StringComparison.Ordinal));

        // What is left is a stream copy, which is the whole point: the archive keeps the camera's
        // own bytes and the processor does nothing.
        Assert.Contains("-c", arguments);
        Assert.Contains("copy", arguments);
        Assert.Contains("-segment_format_options", arguments);
    }

    [Fact]
    public void SomebodyWatchingGetsTheTranscode()
    {
        var arguments = Arguments(liveOutput: true, recording: null);

        Assert.Contains("image2pipe", arguments);
        Assert.Contains("mjpeg", arguments);
        Assert.Contains("-", arguments);   // the pipe ffmpeg writes frames to

        // Nothing is being recorded, so no segment output should appear.
        Assert.DoesNotContain("-segment_format_options", arguments);
    }

    [Fact]
    public void WatchingWhileRecordingCarriesBothOutputs()
    {
        var arguments = Arguments(liveOutput: true, recording: Plan());

        Assert.Contains("image2pipe", arguments);
        Assert.Contains("-segment_format_options", arguments);

        // One input, so the camera still only sees a single RTSP session. Many allow very few.
        Assert.Single(arguments, argument => argument == "-i");
    }

    [Fact]
    public void TheRequestedSizeAndRateOnlyApplyToTheLiveOutput()
    {
        // A recording is a copy of the source, so scaling or rate-limiting it would mean decoding
        // it, which is exactly the cost being avoided.
        var recordingOnly = Arguments(liveOutput: false, recording: Plan());

        Assert.DoesNotContain("-vf", recordingOnly);
        Assert.DoesNotContain("-r", recordingOnly);
        Assert.DoesNotContain("-q:v", recordingOnly);

        var watching = Arguments(liveOutput: true, recording: Plan());
        Assert.Contains("-vf", watching);
        Assert.Contains("-r", watching);
    }

    [Fact]
    public void StdinIsLeftAloneSoFfmpegCanBeAskedToQuit()
    {
        // VisionMesh stops ffmpeg by writing 'q' to its stdin, which is what finalises the
        // recording. -nostdin makes ffmpeg ignore stdin completely, so the quit was never read
        // and every stop sat through the timeout and then killed the process -- which showed up
        // as a nine second wait before a live view would appear.
        Assert.DoesNotContain("-nostdin", Arguments(liveOutput: true, recording: Plan()));
        Assert.DoesNotContain("-nostdin", Arguments(liveOutput: false, recording: Plan()));
    }

    [Fact]
    public void AHardwareDecoderIsAskedForBeforeTheInput()
    {
        // -hwaccel is an input option. After -i it would be accepted and quietly ignored, which
        // looks exactly like hardware decoding that is simply not helping.
        var arguments = Arguments(liveOutput: true, recording: null, decoder: Vaapi());

        var accel = Array.IndexOf(arguments, "-hwaccel");
        Assert.True(accel >= 0, "The chosen decoder never reached the command line.");
        Assert.Equal("vaapi", arguments[accel + 1]);
        Assert.True(accel < Array.IndexOf(arguments, "-i"));

        var device = Array.IndexOf(arguments, "-hwaccel_device");
        Assert.True(device >= 0);
        Assert.Equal("/dev/dri/renderD128", arguments[device + 1]);
    }

    [Fact]
    public void RecordingAloneAsksForNoDecoderAtAll()
    {
        // A stream copy decodes nothing, so a decoder there would set up a pipeline nothing uses
        // and give the machine one more thing to fail at for no benefit.
        Assert.DoesNotContain("-hwaccel", Arguments(liveOutput: false, recording: Plan(), decoder: Vaapi()));
    }

    [Fact]
    public void SoftwareDecodingAddsNothingToTheCommandLine()
    {
        Assert.DoesNotContain("-hwaccel", Arguments(liveOutput: true, recording: null, decoder: HardwareDecoder.Software));
        Assert.DoesNotContain("-hwaccel", Arguments(liveOutput: true, recording: null, decoder: null));
    }

    [Fact]
    public void TheInputComesBeforeEveryOutput()
    {
        // ffmpeg applies output options to whatever follows the input. An output option that
        // drifted in front of -i would silently become an input option instead.
        var arguments = Arguments(liveOutput: true, recording: Plan());

        var input = Array.IndexOf(arguments, "-i");
        Assert.True(input >= 0);
        Assert.True(Array.IndexOf(arguments, "image2pipe") > input);
        Assert.True(Array.IndexOf(arguments, "-segment_format_options") > input);
    }

    [Fact]
    public void AskingForNeitherOutputIsRefusedRatherThanHandedToFfmpeg()
    {
        // ffmpeg with no output fails immediately; the supervisor should never get that far, and
        // a source built that way is a programming mistake worth failing loudly on.
        Assert.Throws<ArgumentException>(() => new FfmpegPullSource(
            Camera(), "rtsp://camera.example/stream", RtspTransport.Tcp, "ffmpeg",
            new VisionMesh.Streaming.Fanout.FrameBus(),
            new VisionMesh.Streaming.Fanout.CameraRuntime("cam_test"),
            recording: null, liveOutput: false, HardwareDecoder.Software,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
    }
}
