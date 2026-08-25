using Microsoft.Extensions.Logging;
using VisionMesh.Core.Abstractions;

namespace VisionMesh.Recording.Motion;

/// <summary>
/// Watches one camera for motion and keeps a short rolling buffer of recent frames.
///
/// The buffer is what makes motion recordings usable: without it, every clip starts at the
/// instant the detector fired, which is always a second or two after the interesting thing
/// entered the frame. Holding a few seconds of JPEGs costs a few megabytes and turns
/// "something already happened" into "here is it happening".
/// </summary>
public sealed class MotionWatcher : IAsyncDisposable
{
    private readonly string _cameraId;
    private readonly IFrameBus _frameBus;
    private readonly MotionDetector _detector;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _bufferGate = new();
    private readonly Queue<ReadOnlyMemory<byte>> _preroll = new();
    private readonly int _prerollCapacity;

    private Task? _worker;

    /// <summary>Raised when motion begins, after the consecutive-frame requirement is met.</summary>
    public event Action<MotionWatcher, double>? MotionStarted;
    /// <summary>Raised once motion has been absent for the whole cool-down period.</summary>
    public event Action<MotionWatcher>? MotionEnded;

    public MotionWatcher(
        string cameraId,
        IFrameBus frameBus,
        int sensitivity,
        int fps,
        TimeSpan prerollDuration,
        TimeSpan coolDown,
        ILogger log)
    {
        _cameraId = cameraId;
        _frameBus = frameBus;
        _detector = new MotionDetector(sensitivity);
        _log = log;
        CoolDown = coolDown;

        var effectiveFps = Math.Clamp(fps <= 0 ? 15 : fps, 1, 60);
        // Cap the buffer so a high-fps camera with a long pre-roll cannot eat unbounded memory.
        _prerollCapacity = Math.Clamp((int)(prerollDuration.TotalSeconds * effectiveFps), 0, 300);
    }

    public string CameraId => _cameraId;
    public TimeSpan CoolDown { get; }

    /// <summary>True from the moment motion is detected until the cool-down expires.</summary>
    public bool MotionActive { get; private set; }
    public DateTimeOffset? LastMotionUtc { get; private set; }
    public double LastChangedRatio => _detector.LastChangedRatio;
    /// <summary>False when frames could not be decoded, so the UI can explain why motion never fires.</summary>
    public bool DetectionWorking { get; private set; } = true;

    public void Start() => _worker ??= Task.Run(() => RunAsync(_stop.Token));

    /// <summary>Snapshot of the buffered frames, oldest first, for priming a new recording.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> TakePreroll()
    {
        lock (_bufferGate) return _preroll.ToArray();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var subscription = _frameBus.Subscribe(_cameraId);
        var undecodable = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await subscription.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null) break;

                if (_prerollCapacity > 0)
                {
                    lock (_bufferGate)
                    {
                        _preroll.Enqueue(frame.Jpeg);
                        while (_preroll.Count > _prerollCapacity) _preroll.Dequeue();
                    }
                }

                var result = _detector.Evaluate(frame.Jpeg.Span);
                if (!result.Evaluated)
                {
                    // A handful of undecodable frames is normal at startup. A sustained run means
                    // the source is progressive JPEG or otherwise unsupported, and the user needs
                    // to be told rather than left wondering why motion never triggers.
                    if (++undecodable == 60)
                    {
                        DetectionWorking = false;
                        _log.LogWarning("Motion detection is not working on camera {Camera}: its frames could not be analysed.", _cameraId);
                    }
                    continue;
                }

                undecodable = 0;
                DetectionWorking = true;

                if (result.Motion)
                {
                    LastMotionUtc = DateTimeOffset.UtcNow;
                    if (!MotionActive)
                    {
                        MotionActive = true;
                        _log.LogInformation("Motion detected on camera {Camera} ({Percent:P1} of frame changed).", _cameraId, result.ChangedRatio);
                        MotionStarted?.Invoke(this, result.ChangedRatio);
                    }
                }
                else if (MotionActive && LastMotionUtc is { } last && DateTimeOffset.UtcNow - last > CoolDown)
                {
                    MotionActive = false;
                    MotionEnded?.Invoke(this);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Watcher stopped.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Motion watching failed for camera {Camera}.", _cameraId);
        }
    }

    /// <summary>Called from the reconcile loop so the cool-down expires even when frames stop arriving.</summary>
    public void Tick()
    {
        if (!MotionActive) return;
        if (LastMotionUtc is not { } last) return;
        if (DateTimeOffset.UtcNow - last <= CoolDown) return;

        MotionActive = false;
        MotionEnded?.Invoke(this);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_worker is not null)
        {
            try { await _worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        lock (_bufferGate) _preroll.Clear();
        _stop.Dispose();
    }
}
