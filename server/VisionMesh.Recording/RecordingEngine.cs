using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VisionMesh.Core.Abstractions;
using VisionMesh.Core.Models;
using VisionMesh.Database.Repositories;
using VisionMesh.Recording.Motion;
using VisionMesh.Streaming;
using VisionMesh.Streaming.Fanout;
using VisionMesh.Streaming.Sources;

namespace VisionMesh.Recording;

/// <summary>
/// Decides what each camera should be recording and keeps the recorders in step with that.
///
/// Two recording paths exist and the engine picks between them deliberately:
///
///  * <b>Stream copy</b> - a network camera recording continuously has its own H.264 written
///    straight to disk by the same ffmpeg that serves the live view. No second connection to the
///    camera, no re-encode, full source quality, near-zero CPU.
///  * <b>Re-encode from frames</b> - used whenever recording has to start and stop on demand
///    (motion, manual) or when the source is genuinely a JPEG stream (agents, phones). Starting
///    and stopping a stream-copy recorder means restarting ffmpeg, which is far too disruptive to
///    do on every motion event.
/// </summary>
public sealed class RecordingEngine(
    CameraRepository cameras,
    SettingsRepository settings,
    EventRepository events,
    RecordingRepository recordings,
    StorageManager storage,
    CameraSupervisor supervisor,
    FrameBus frameBus,
    CameraRuntimeRegistry runtimes,
    FfmpegLocator ffmpegLocator,
    IRealtimeNotifier notifier,
    ILogger<RecordingEngine> log) : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MotionCoolDown = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MotionPreroll = TimeSpan.FromSeconds(4);
    /// <summary>Segment length. Ten minutes balances seek granularity against file count.</summary>
    public const int SegmentSeconds = 600;

    private readonly ConcurrentDictionary<string, JpegRecorder> _recorders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MotionWatcher> _watchers = new(StringComparer.Ordinal);
    /// <summary>Cameras the user has explicitly started recording from the UI.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _manual = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Why each camera started recording, and when.
    ///
    /// ffmpeg writes segment files with nothing in them to say what caused the recording, so the
    /// indexer cannot tell a motion clip from a continuous one by looking at the file. Without
    /// this the recordings list and the timeline would label everything "Continuous", which is
    /// exactly the kind of confidently wrong detail this project refuses to show.
    /// </summary>
    private readonly ConcurrentDictionary<string, List<TriggerMark>> _triggerHistory = new(StringComparer.Ordinal);

    /// <summary>How many recording reasons to remember per camera. A day of motion clips fits easily.</summary>
    private const int TriggerHistoryLimit = 200;

    private readonly record struct TriggerMark(DateTimeOffset At, RecordingTrigger Trigger);

    /// <summary>Whether ffmpeg was available on the last pass. Surfaced so the UI can explain itself.</summary>
    public bool RecordingAvailable { get; private set; }

    public bool IsRecording(string cameraId) => supervisor.RecordingCameras.ContainsKey(cameraId);

    public bool IsManuallyRecording(string cameraId) => _manual.ContainsKey(cameraId);

    /// <summary>Starts a manual recording. Returns false when ffmpeg is missing, so the UI can say so.</summary>
    public bool StartManualRecording(string cameraId)
    {
        if (!RecordingAvailable) return false;
        _manual[cameraId] = DateTimeOffset.UtcNow;
        return true;
    }

    public void StopManualRecording(string cameraId) => _manual.TryRemove(cameraId, out _);

    /// <summary>Live motion state for a camera, or null when it is not being watched.</summary>
    public MotionWatcher? GetMotionWatcher(string cameraId) => _watchers.GetValueOrDefault(cameraId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Tell the supervisor how to record network cameras via stream copy.
        supervisor.RecordingPlanner = PlanStreamCopyRecording;

        CloseSegmentsLeftOpenByAnUncleanShutdown();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Recording reconciliation failed.");
            }

            try { await Task.Delay(ReconcileInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        await StopEverythingAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The recording plan for a network camera doing continuous or scheduled recording.
    /// Returns null for every other case, which is what keeps motion and manual recording on the
    /// re-encode path where they can start and stop freely.
    /// </summary>
    private RecordingPlan? PlanStreamCopyRecording(Camera camera)
    {
        if (!RecordingAvailable) return null;
        if (camera.SourceKind is not (CameraSourceKind.Rtsp or CameraSourceKind.Onvif)) return null;
        if (!ShouldRecordContinuously(camera)) return null;
        return new RecordingPlan(storage.GetCameraDirectory(camera), SegmentSeconds);
    }

    private static bool ShouldRecordContinuously(Camera camera)
    {
        if (camera.PrivacyMode || !camera.Enabled) return false;

        return camera.RecordingMode switch
        {
            RecordingMode.Continuous => true,
            RecordingMode.Scheduled => CameraSourceConfig.FromJson(camera.ConfigJson).IsScheduleActive(DateTimeOffset.Now),
            _ => false,
        };
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ffmpeg = await ffmpegLocator.LocateAsync(settings.Get(SettingsRepository.Keys.FfmpegPath), cancellationToken: cancellationToken).ConfigureAwait(false);
            RecordingAvailable = ffmpeg.Available && ffmpeg.Path is not null;

            var all = cameras.GetAll();
            var live = new HashSet<string>(all.Select(c => c.Id), StringComparer.Ordinal);

            foreach (var camera in all)
            {
                await ReconcileCameraAsync(camera, ffmpeg.Path, cancellationToken).ConfigureAwait(false);
            }

            // Clean up recorders and watchers whose camera was deleted while they were running.
            foreach (var orphan in _recorders.Keys.Where(id => !live.Contains(id)).ToArray())
                await StopRecorderAsync(orphan).ConfigureAwait(false);
            foreach (var orphan in _watchers.Keys.Where(id => !live.Contains(id)).ToArray())
                await StopWatcherAsync(orphan).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReconcileCameraAsync(Camera camera, string? ffmpegPath, CancellationToken cancellationToken)
    {
        var config = CameraSourceConfig.FromJson(camera.ConfigJson);
        var wantsMotion = camera.RecordingMode == RecordingMode.Motion && camera.Enabled && !camera.PrivacyMode;

        // ---- motion watching ----
        if (wantsMotion)
        {
            if (!_watchers.ContainsKey(camera.Id))
            {
                var sensitivity = config.MotionSensitivity
                                  ?? settings.GetInt(SettingsRepository.Keys.MotionSensitivity, 50);

                var watcher = new MotionWatcher(camera.Id, frameBus, sensitivity, camera.DesiredFps,
                                                MotionPreroll, MotionCoolDown, log);
                watcher.MotionStarted += OnMotionStarted;
                if (_watchers.TryAdd(camera.Id, watcher)) watcher.Start();
                else await watcher.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                // Drives the cool-down even when the camera has stopped delivering frames.
                _watchers[camera.Id].Tick();
            }
        }
        else if (_watchers.ContainsKey(camera.Id))
        {
            await StopWatcherAsync(camera.Id).ConfigureAwait(false);
        }

        // ---- decide whether this camera should be recording right now ----
        var manual = _manual.ContainsKey(camera.Id);
        var motionActive = _watchers.TryGetValue(camera.Id, out var active) && active.MotionActive;
        var continuous = ShouldRecordContinuously(camera);

        var shouldRecord = camera.Enabled && !camera.PrivacyMode &&
                           camera.State != CameraState.Paused &&
                           (manual || motionActive || continuous);

        // Note the reason at the moment recording begins, so the indexer can label the files this
        // produces with what actually caused them rather than assuming.
        var wasRecording = supervisor.RecordingCameras.ContainsKey(camera.Id);
        if (shouldRecord && !wasRecording)
        {
            RecordTrigger(camera.Id, manual ? RecordingTrigger.Manual
                                   : motionActive ? RecordingTrigger.Motion
                                   : camera.RecordingMode == RecordingMode.Scheduled ? RecordingTrigger.Schedule
                                   : RecordingTrigger.Continuous);
        }

        if (shouldRecord) supervisor.RecordingCameras[camera.Id] = 0;
        else supervisor.RecordingCameras.TryRemove(camera.Id, out _);

        var runtime = runtimes.Find(camera.Id);
        if (runtime is not null) runtime.Recording = shouldRecord;

        // ---- stream copy handles continuous network recording; nothing more to do ----
        var streamCopyCoversIt = camera.SourceKind is CameraSourceKind.Rtsp or CameraSourceKind.Onvif
                                 && continuous && !manual && !motionActive;

        if (!shouldRecord || streamCopyCoversIt || ffmpegPath is null)
        {
            if (_recorders.ContainsKey(camera.Id)) await StopRecorderAsync(camera.Id).ConfigureAwait(false);

            if (shouldRecord && ffmpegPath is null)
            {
                log.LogDebug("Camera {Camera} should be recording but ffmpeg is not installed.", camera.Id);
            }
            return;
        }

        // ---- re-encode path ----
        if (_recorders.ContainsKey(camera.Id)) return;

        var preroll = motionActive && _watchers.TryGetValue(camera.Id, out var watcherForPreroll)
            ? watcherForPreroll.TakePreroll()
            : null;

        var plan = new RecordingPlan(storage.GetCameraDirectory(camera), SegmentSeconds);
        var recorder = new JpegRecorder(camera.Id, frameBus, ffmpegPath, plan, camera.DesiredFps, log, preroll);

        if (_recorders.TryAdd(camera.Id, recorder))
        {
            recorder.Start();
            RaiseRecordingEvent(camera.Id, started: true, manual ? RecordingTrigger.Manual : motionActive ? RecordingTrigger.Motion : RecordingTrigger.Continuous);
            notifier.RecordingChanged(camera.Id, true);
        }
        else
        {
            await recorder.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void RecordTrigger(string cameraId, RecordingTrigger trigger)
    {
        var history = _triggerHistory.GetOrAdd(cameraId, _ => new List<TriggerMark>());
        lock (history)
        {
            history.Add(new TriggerMark(DateTimeOffset.UtcNow, trigger));
            if (history.Count > TriggerHistoryLimit) history.RemoveRange(0, history.Count - TriggerHistoryLimit);
        }
    }

    /// <summary>
    /// Why a recording that began at <paramref name="startUtc"/> was started.
    ///
    /// Falls back to Continuous only when nothing is known, which is the honest default: a file
    /// found with no matching reason was almost certainly written by a stream-copy recording that
    /// started before this process did.
    /// </summary>
    public RecordingTrigger TriggerAt(string cameraId, DateTimeOffset startUtc)
    {
        if (!_triggerHistory.TryGetValue(cameraId, out var history)) return RecordingTrigger.Continuous;

        lock (history)
        {
            // A segment belongs to the most recent reason recorded at or before it started. The
            // small tolerance covers the gap between deciding to record and ffmpeg naming the file.
            var tolerance = startUtc.AddSeconds(2);
            for (var i = history.Count - 1; i >= 0; i--)
            {
                if (history[i].At <= tolerance) return history[i].Trigger;
            }
        }

        return RecordingTrigger.Continuous;
    }

    private void OnMotionStarted(MotionWatcher watcher, double changedRatio)
    {
        var cameraEvent = new CameraEvent
        {
            CameraId = watcher.CameraId,
            Type = EventType.Motion,
            Severity = EventSeverity.Info,
            TimestampUtc = DateTimeOffset.UtcNow,
            Detail = $"{changedRatio:P1} of the picture changed.",
        };

        try
        {
            cameraEvent.Id = events.Insert(cameraEvent);
            notifier.EventRaised(cameraEvent);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not record a motion event for camera {Camera}.", watcher.CameraId);
        }
    }

    private void RaiseRecordingEvent(string cameraId, bool started, RecordingTrigger trigger)
    {
        try
        {
            var cameraEvent = new CameraEvent
            {
                CameraId = cameraId,
                Type = started ? EventType.RecordingStarted : EventType.RecordingStopped,
                Severity = EventSeverity.Info,
                TimestampUtc = DateTimeOffset.UtcNow,
                Detail = trigger.ToString(),
            };
            cameraEvent.Id = events.Insert(cameraEvent);
            notifier.EventRaised(cameraEvent);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not record a recording event for camera {Camera}.", cameraId);
        }
    }

    /// <summary>
    /// Closes recording rows that a crash left marked open, using the file's real size on disk.
    /// Without this the storage figures drift upward every unclean shutdown.
    /// </summary>
    private void CloseSegmentsLeftOpenByAnUncleanShutdown()
    {
        foreach (var segment in recordings.GetOpenSegments())
        {
            try
            {
                var file = new FileInfo(segment.FilePath);
                if (file.Exists)
                {
                    recordings.Close(segment.Id, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero), file.Length);
                }
                else
                {
                    // The file never made it to disk, so the row describes nothing.
                    recordings.Delete(segment.Id);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning("Could not reconcile recording {Id}: {Error}", segment.Id, ex.Message);
            }
        }
    }

    private async Task StopRecorderAsync(string cameraId)
    {
        if (!_recorders.TryRemove(cameraId, out var recorder)) return;
        await recorder.DisposeAsync().ConfigureAwait(false);
        RaiseRecordingEvent(cameraId, started: false, RecordingTrigger.Manual);
        notifier.RecordingChanged(cameraId, false);
    }

    private async Task StopWatcherAsync(string cameraId)
    {
        if (!_watchers.TryRemove(cameraId, out var watcher)) return;
        watcher.MotionStarted -= OnMotionStarted;
        await watcher.DisposeAsync().ConfigureAwait(false);
    }

    private async Task StopEverythingAsync()
    {
        foreach (var id in _recorders.Keys.ToArray()) await StopRecorderAsync(id).ConfigureAwait(false);
        foreach (var id in _watchers.Keys.ToArray()) await StopWatcherAsync(id).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }
}
