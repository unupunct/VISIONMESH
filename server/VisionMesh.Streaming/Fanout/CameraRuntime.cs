using System.Collections.Concurrent;
using VisionMesh.Core.Abstractions;
using VisionMesh.Core.Models;

namespace VisionMesh.Streaming.Fanout;

/// <summary>
/// Live, measured statistics for one camera.
///
/// Every number is computed from frames that actually arrived, over a real sliding time
/// window. Nothing here is estimated: if not enough frames have been seen yet, the metric
/// stays null and the UI shows a dash rather than inventing a plausible value.
/// </summary>
public sealed class CameraRuntime
{
    private const int WindowSize = 90;          // ~3s at 30fps, ~6s at 15fps
    private static readonly TimeSpan MaxWindow = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly Queue<(DateTimeOffset At, int Bytes)> _window = new();

    public string CameraId { get; }

    public CameraRuntime(string cameraId) => CameraId = cameraId;

    public CameraState State { get; set; } = CameraState.Offline;
    public long FramesReceived { get; private set; }
    public long BytesReceived { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public DateTimeOffset? LastFrameUtc { get; private set; }
    public DateTimeOffset? LastHeartbeatUtc { get; set; }
    public DateTimeOffset? StartedUtc { get; set; }
    public string? LastError { get; set; }
    public long? DroppedFrames { get; set; }
    public bool Recording { get; set; }
    public int? BatteryPercent { get; set; }
    public bool? BatteryCharging { get; set; }
    public string? NetworkQuality { get; set; }
    /// <summary>Agent-reported fps, kept separately so we can prefer our own server-side measurement.</summary>
    public double? AgentReportedFps { get; set; }

    private double? _latencyMsSmoothed;

    public void RecordFrame(VideoFrame frame)
    {
        lock (_gate)
        {
            FramesReceived++;
            BytesReceived += frame.Jpeg.Length;
            if (frame.Width > 0) Width = frame.Width;
            if (frame.Height > 0) Height = frame.Height;
            LastFrameUtc = frame.ReceivedUtc;
            LastError = null;

            _window.Enqueue((frame.ReceivedUtc, frame.Jpeg.Length));
            while (_window.Count > WindowSize) _window.Dequeue();
            while (_window.Count > 2 && frame.ReceivedUtc - _window.Peek().At > MaxWindow) _window.Dequeue();

            if (frame.CaptureUnixMs > 0)
            {
                // Only meaningful when the capture clock is comparable to ours. Negative or absurd
                // values mean the agent clock is skewed, so we discard rather than display nonsense.
                var delta = frame.ReceivedUtc.ToUnixTimeMilliseconds() - frame.CaptureUnixMs;
                if (delta is >= 0 and < 10_000)
                {
                    _latencyMsSmoothed = _latencyMsSmoothed is { } previous
                        ? (previous * 0.8) + (delta * 0.2)
                        : delta;
                }
            }
        }
    }

    /// <summary>Frames per second over the sliding window, or null when fewer than two frames are in it.</summary>
    public double? GetFps()
    {
        lock (_gate)
        {
            if (_window.Count < 2) return null;
            var span = _window.Last().At - _window.Peek().At;
            if (span <= TimeSpan.Zero) return null;
            return Math.Round((_window.Count - 1) / span.TotalSeconds, 1);
        }
    }

    /// <summary>Measured payload bitrate in bits per second, or null when the window is too small.</summary>
    public double? GetBitrateBps()
    {
        lock (_gate)
        {
            if (_window.Count < 2) return null;
            var span = _window.Last().At - _window.Peek().At;
            if (span <= TimeSpan.Zero) return null;
            // Skip the oldest sample's bytes: it marks the window start, its bytes arrived before it.
            var bytes = _window.Skip(1).Sum(entry => (long)entry.Bytes);
            return Math.Round(bytes * 8 / span.TotalSeconds, 0);
        }
    }

    public double? GetLatencyMs()
    {
        lock (_gate) return _latencyMsSmoothed is { } value ? Math.Round(value, 1) : null;
    }

    /// <summary>Clears measurements when a camera stops, so a restarted camera does not show stale rates.</summary>
    public void ResetMeasurements()
    {
        lock (_gate)
        {
            _window.Clear();
            _latencyMsSmoothed = null;
            Width = 0;
            Height = 0;
            LastFrameUtc = null;
            AgentReportedFps = null;
        }
    }

    public CameraHealth ToHealth(int viewerCount) => new()
    {
        CameraId = CameraId,
        State = State,
        Fps = GetFps() ?? AgentReportedFps,
        BitrateBps = GetBitrateBps(),
        LatencyMs = GetLatencyMs(),
        DroppedFrames = DroppedFrames,
        FramesReceived = FramesReceived,
        BytesReceived = BytesReceived,
        Width = Width,
        Height = Height,
        LastFrameUtc = LastFrameUtc,
        LastHeartbeatUtc = LastHeartbeatUtc,
        Recording = Recording,
        ViewerCount = viewerCount,
        LastError = LastError,
        BatteryPercent = BatteryPercent,
        BatteryCharging = BatteryCharging,
        NetworkQuality = NetworkQuality,
    };
}

/// <summary>Registry of per-camera runtime state, keyed by camera id.</summary>
public sealed class CameraRuntimeRegistry
{
    private readonly ConcurrentDictionary<string, CameraRuntime> _runtimes = new(StringComparer.Ordinal);

    public CameraRuntime Get(string cameraId) => _runtimes.GetOrAdd(cameraId, static id => new CameraRuntime(id));

    public CameraRuntime? Find(string cameraId) => _runtimes.TryGetValue(cameraId, out var runtime) ? runtime : null;

    public void Remove(string cameraId) => _runtimes.TryRemove(cameraId, out _);

    public IReadOnlyCollection<CameraRuntime> All => _runtimes.Values.ToArray();
}
