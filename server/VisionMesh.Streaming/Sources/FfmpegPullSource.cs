using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using VisionMesh.Core.Abstractions;
using VisionMesh.Core.Models;
using VisionMesh.Streaming.Fanout;

namespace VisionMesh.Streaming.Sources;

/// <summary>
/// Pulls one network camera (RTSP, or an ONVIF camera's RTSP stream URI) through ffmpeg and
/// publishes its frames onto the frame bus.
///
/// ffmpeg is asked for MJPEG output rather than a raw pixel format so the server never has to
/// carry an encoder: the CPU cost stays inside ffmpeg, which does it far better than we could,
/// and the bytes that come out are exactly what the browser and the frame bus already speak.
///
/// This is a transcode, and it is honest about being one - an H.264 camera cannot be forwarded
/// as MJPEG without re-encoding. Recording takes a separate path that copies the original
/// stream without touching the codec.
/// </summary>
public sealed class FfmpegPullSource : IAsyncDisposable
{
    private static readonly TimeSpan FrameWatchdog = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);

    private readonly Camera _camera;
    private readonly string _authenticatedUrl;
    private readonly RtspTransport _transport;
    private readonly string _ffmpegPath;
    private readonly IFrameBus _frameBus;
    private readonly CameraRuntime _runtime;
    private readonly RecordingPlan? _recording;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();

    private Task? _worker;

    public FfmpegPullSource(
        Camera camera,
        string authenticatedUrl,
        RtspTransport transport,
        string ffmpegPath,
        IFrameBus frameBus,
        CameraRuntime runtime,
        RecordingPlan? recording,
        ILogger log)
    {
        _camera = camera;
        _authenticatedUrl = authenticatedUrl;
        _transport = transport;
        _ffmpegPath = ffmpegPath;
        _frameBus = frameBus;
        _runtime = runtime;
        _recording = recording;
        _log = log;
    }

    public string CameraId => _camera.Id;

    public void Start() => _worker ??= Task.Run(() => RunAsync(_stop.Token));

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var backoff = MinBackoff;

        while (!cancellationToken.IsCancellationRequested)
        {
            var startedUtc = DateTimeOffset.UtcNow;
            try
            {
                await PumpOnceAsync(cancellationToken).ConfigureAwait(false);

                // A session that survived a while was healthy; reset the backoff so a camera
                // that drops once an hour reconnects instantly rather than after two minutes.
                if (DateTimeOffset.UtcNow - startedUtc > TimeSpan.FromMinutes(1)) backoff = MinBackoff;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _runtime.LastError = ex.Message;
                _runtime.State = CameraState.Degraded;
                _log.LogWarning("Camera {Camera} pull failed: {Error}", _camera.Id, ex.Message);
            }

            if (cancellationToken.IsCancellationRequested) break;

            _runtime.State = CameraState.Offline;
            _runtime.ResetMeasurements();

            try { await Task.Delay(backoff, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }

        _runtime.State = CameraState.Offline;
        _runtime.ResetMeasurements();
    }

    private async Task PumpOnceAsync(CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = BuildStartInfo(),
            EnableRaisingEvents = true,
        };

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (stderr)
            {
                // Keep only the tail: ffmpeg can be extremely chatty on a failing camera.
                if (stderr.Length > 4000) stderr.Clear();
                stderr.AppendLine(e.Data);
            }
        };

        if (!process.Start()) throw new InvalidOperationException("Could not start ffmpeg.");
        process.BeginErrorReadLine();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watchdog = StartWatchdogAsync(process, linked);

        try
        {
            var reader = new JpegStreamReader(process.StandardOutput.BaseStream);
            uint sequence = 0;

            while (!linked.Token.IsCancellationRequested)
            {
                var jpeg = await reader.ReadFrameAsync(linked.Token).ConfigureAwait(false);
                if (jpeg is null) break;

                var now = DateTimeOffset.UtcNow;
                var frame = new VideoFrame
                {
                    CameraId = _camera.Id,
                    Jpeg = jpeg,
                    ReceivedUtc = now,
                    CaptureUnixMs = now.ToUnixTimeMilliseconds(),
                    Sequence = unchecked(sequence++),
                    Width = _camera.DesiredWidth,
                    Height = _camera.DesiredHeight,
                };

                _runtime.RecordFrame(frame);
                _runtime.State = CameraState.Online;
                _frameBus.Publish(frame);
            }
        }
        finally
        {
            linked.Cancel();
            await watchdog.ConfigureAwait(false);
            TryKill(process);
        }

        string tail;
        lock (stderr) tail = stderr.ToString().Trim();

        if (!cancellationToken.IsCancellationRequested && tail.Length > 0)
        {
            // ffmpeg echoes the input URL in its diagnostics, which would leak the RTSP password
            // into our logs and into the camera health panel. Strip it before it goes anywhere.
            var safe = Sanitise(tail);
            _runtime.LastError = safe.Length > 300 ? safe[^300..] : safe;
            throw new InvalidOperationException(_runtime.LastError);
        }
    }

    private ProcessStartInfo BuildStartInfo()
    {
        var info = new ProcessStartInfo(_ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        void Add(string argument) => info.ArgumentList.Add(argument);

        Add("-hide_banner");
        Add("-loglevel"); Add("warning");
        Add("-nostdin");

        if (_authenticatedUrl.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase) && _transport != RtspTransport.Auto)
        {
            Add("-rtsp_transport");
            Add(_transport == RtspTransport.Tcp ? "tcp" : "udp");
        }

        Add("-i"); Add(_authenticatedUrl);

        // Output 1: MJPEG on stdout for live viewing. This is a transcode and cannot avoid being
        // one, because no browser plays raw H.264 out of a pipe without a full player stack.
        Add("-an");     // surveillance live view is video only; audio would double the work for nothing
        Add("-sn");
        Add("-f"); Add("image2pipe");
        Add("-vcodec"); Add("mjpeg");
        Add("-q:v"); Add(MapQuality(_camera.DesiredQuality).ToString(CultureInfo.InvariantCulture));

        if (_camera.DesiredFps > 0)
        {
            Add("-r");
            Add(_camera.DesiredFps.ToString(CultureInfo.InvariantCulture));
        }

        if (_camera.DesiredWidth > 0 && _camera.DesiredHeight > 0)
        {
            // force_original_aspect_ratio=decrease keeps the picture undistorted when the camera's
            // native aspect ratio differs from the requested box.
            Add("-vf");
            Add($"scale={_camera.DesiredWidth}:{_camera.DesiredHeight}:force_original_aspect_ratio=decrease");
        }

        Add("-");

        // Output 2: the recording, written straight from the camera's own encoded stream.
        // -c copy means the archive keeps full source quality at zero CPU cost, and the whole
        // camera still only holds one RTSP session open - many cameras allow very few.
        if (_recording is { } plan)
        {
            Directory.CreateDirectory(plan.Directory);

            Add("-c"); Add("copy");
            Add("-an");
            Add("-f"); Add("segment");
            Add("-segment_time"); Add(plan.SegmentSeconds.ToString(CultureInfo.InvariantCulture));
            Add("-segment_format"); Add("mp4");
            // Segments must start on a keyframe or the first seconds of each file are unplayable.
            Add("-segment_atclocktime"); Add("1");
            Add("-reset_timestamps"); Add("1");
            Add("-strftime"); Add("1");
            // movflags makes each segment playable even if the process is killed mid-write.
            Add("-movflags"); Add("+faststart+frag_keyframe+empty_moov");
            Add(Path.Combine(plan.Directory, RecordingPlan.FilePattern));
        }

        return info;
    }

    /// <summary>Maps the 1-100 quality the UI shows onto ffmpeg's inverted 2-31 mjpeg scale.</summary>
    private static int MapQuality(int quality)
    {
        var clamped = Math.Clamp(quality, 1, 100);
        return (int)Math.Round(2 + ((100 - clamped) * 29.0 / 99.0));
    }

    /// <summary>
    /// Kills the process if no frame arrives for a while. ffmpeg can sit forever on a camera
    /// that accepted the TCP connection and then went silent, which no exit code would tell us about.
    /// </summary>
    private async Task StartWatchdogAsync(Process process, CancellationTokenSource linked)
    {
        try
        {
            while (!linked.Token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), linked.Token).ConfigureAwait(false);

                var last = _runtime.LastFrameUtc ?? _runtime.StartedUtc ?? DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - last <= FrameWatchdog) continue;

                _log.LogWarning("Camera {Camera} produced no frames for {Seconds}s; restarting ffmpeg.",
                    _camera.Id, (int)FrameWatchdog.TotalSeconds);
                _runtime.LastError = "The camera stopped sending video.";
                linked.Cancel();
                TryKill(process);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Stream ended normally.
        }
    }

    private string Sanitise(string text)
    {
        var redacted = UrlRedactor.Redact(_authenticatedUrl);
        return text.Replace(_authenticatedUrl, redacted, StringComparison.OrdinalIgnoreCase);
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
    }
}
