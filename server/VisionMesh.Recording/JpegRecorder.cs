using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using VisionMesh.Core.Abstractions;
using VisionMesh.Streaming.Sources;

namespace VisionMesh.Recording;

/// <summary>
/// Records a camera whose frames arrive as JPEG (agents and phones) by piping them into ffmpeg
/// and letting it produce segmented H.264 MP4 files.
///
/// This transcodes, and there is no way around it: the source really is a sequence of JPEGs, and
/// storing them as MJPEG would use roughly ten times the disk for the same picture. Network
/// cameras take a different path that copies their existing H.264 stream untouched.
///
/// The recorder subscribes to the frame bus rather than tapping the source directly, so it sees
/// exactly the frames viewers see and adds no extra load on the camera.
/// </summary>
public sealed class JpegRecorder : IAsyncDisposable
{
    private readonly string _cameraId;
    private readonly IFrameBus _frameBus;
    private readonly string _ffmpegPath;
    private readonly RecordingPlan _plan;
    private readonly int _fps;
    private readonly IReadOnlyList<ReadOnlyMemory<byte>> _preroll;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();

    private Task? _worker;

    public JpegRecorder(
        string cameraId,
        IFrameBus frameBus,
        string ffmpegPath,
        RecordingPlan plan,
        int fps,
        ILogger log,
        IReadOnlyList<ReadOnlyMemory<byte>>? preroll = null)
    {
        _cameraId = cameraId;
        _frameBus = frameBus;
        _ffmpegPath = ffmpegPath;
        _plan = plan;
        // An input frame rate of zero would make ffmpeg guess, and it guesses badly on a live pipe.
        _fps = Math.Clamp(fps <= 0 ? 15 : fps, 1, 60);
        _preroll = preroll ?? Array.Empty<ReadOnlyMemory<byte>>();
        _log = log;
        _log.LogInformation("Recording camera {Camera} to {Directory}.", cameraId, plan.Directory);
    }

    public string CameraId => _cameraId;

    /// <summary>Set when the recorder stops because of an error, for display on the camera panel.</summary>
    public string? LastError { get; private set; }

    public void Start() => _worker ??= Task.Run(() => RunAsync(_stop.Token));

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_plan.Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"Cannot write to the recordings folder: {ex.Message}";
            _log.LogError("Recording for camera {Camera} could not start: {Error}", _cameraId, LastError);
            return;
        }

        using var process = new Process { StartInfo = BuildStartInfo() };
        var stderr = new StringBuilder();

        try
        {
            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                lock (stderr)
                {
                    if (stderr.Length > 4000) stderr.Clear();
                    stderr.AppendLine(e.Data);
                }
            };

            if (!process.Start())
            {
                LastError = "ffmpeg could not be started.";
                return;
            }
            process.BeginErrorReadLine();

            // Subscribe before writing the pre-roll so no live frame is missed in between.
            using var subscription = _frameBus.Subscribe(_cameraId);
            var input = process.StandardInput.BaseStream;

            foreach (var buffered in _preroll)
            {
                try
                {
                    await input.WriteAsync(buffered, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    break;
                }
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await subscription.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null) break;

                if (process.HasExited)
                {
                    lock (stderr) LastError = Tail(stderr.ToString());
                    _log.LogWarning("ffmpeg exited while recording camera {Camera}: {Error}", _cameraId, LastError);
                    break;
                }

                try
                {
                    await input.WriteAsync(frame.Jpeg, cancellationToken).ConfigureAwait(false);
                    await input.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // ffmpeg closed the pipe; the exit path below reports why.
                    break;
                }
            }

            // Closing stdin lets ffmpeg finalise the current segment properly rather than
            // leaving a file with no moov atom that nothing can play.
            try { process.StandardInput.Close(); } catch (IOException) { }

            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                _log.LogWarning("ffmpeg did not exit in time for camera {Camera}; terminating it.", _cameraId);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop.
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.LogError(ex, "Recording of camera {Camera} failed.", _cameraId);
        }
        finally
        {
            TryKill(process);
        }
    }

    private ProcessStartInfo BuildStartInfo()
    {
        var info = new ProcessStartInfo(_ffmpegPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        void Add(string argument) => info.ArgumentList.Add(argument);

        Add("-hide_banner");
        Add("-loglevel"); Add("warning");

        // Input: a raw stream of concatenated JPEGs on stdin.
        Add("-f"); Add("image2pipe");
        Add("-framerate"); Add(_fps.ToString(CultureInfo.InvariantCulture));
        Add("-i"); Add("-");

        // Output: H.264 in segmented MP4. veryfast is the right point on the speed/size curve for
        // a machine that may be encoding several cameras at once; yuv420p is what every player wants.
        Add("-c:v"); Add("libx264");
        Add("-preset"); Add("veryfast");
        Add("-pix_fmt"); Add("yuv420p");
        Add("-crf"); Add("26");
        // A keyframe every two seconds bounds how far back a seek has to decode from.
        Add("-g"); Add((_fps * 2).ToString(CultureInfo.InvariantCulture));
        Add("-an");

        Add("-f"); Add("segment");
        Add("-segment_time"); Add(_plan.SegmentSeconds.ToString(CultureInfo.InvariantCulture));
        Add("-segment_format"); Add("mp4");
        Add("-segment_atclocktime"); Add("1");
        Add("-reset_timestamps"); Add("1");
        Add("-strftime"); Add("1");
        Add("-movflags"); Add("+faststart+frag_keyframe+empty_moov");
        Add(Path.Combine(_plan.Directory, RecordingPlan.FilePattern));

        return info;
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 300 ? trimmed[^300..] : trimmed;
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_worker is not null)
        {
            try { await _worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _stop.Dispose();
        _log.LogInformation("Stopped recording camera {Camera}.", _cameraId);
    }
}
