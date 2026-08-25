using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VisionMesh.Core.Abstractions;
using VisionMesh.Core.Models;
using VisionMesh.Core.Util;
using VisionMesh.Database.Repositories;
using VisionMesh.Streaming.Fanout;
using VisionMesh.Streaming.Ingest;
using VisionMesh.Streaming.Sources;

namespace VisionMesh.Streaming;

/// <summary>
/// Decides which cameras should be capturing right now and reconciles reality with that decision.
///
/// Capture is demand-driven: a camera runs when somebody is watching it or when it is set to
/// record. An idle camera costs nothing, which is what makes a twenty-camera install viable on a
/// mini PC. The stop is deliberately delayed by a grace period so that flicking between cameras
/// in the dashboard does not tear down and rebuild a stream every few seconds.
/// </summary>
public sealed class CameraSupervisor(
    CameraRepository cameras,
    SettingsRepository settings,
    EventRepository events,
    AgentRegistry agents,
    FrameBus frameBus,
    CameraRuntimeRegistry runtimes,
    FfmpegLocator ffmpegLocator,
    SecretProtector secrets,
    IRealtimeNotifier notifier,
    ILogger<CameraSupervisor> log) : BackgroundService
{
    /// <summary>How long a camera keeps running after its last viewer leaves.</summary>
    private static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, FfmpegPullSource> _pullSources = new(StringComparer.Ordinal);
    /// <summary>Recording plan each running pull source was started with, so changes can be detected.</summary>
    private readonly ConcurrentDictionary<string, RecordingPlan> _pullPlans = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _idleSince = new(StringComparer.Ordinal);
    /// <summary>Cameras the user explicitly wants running regardless of viewers (manual recording, tests).</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pinned = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);

    /// <summary>Cameras currently recording, set by the recording engine so supervision keeps them alive.</summary>
    public ConcurrentDictionary<string, byte> RecordingCameras { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Supplies the recording plan for a network camera, or null when it should not record.
    /// Set by the recording engine at startup. Injected as a callback rather than a project
    /// reference so streaming does not have to depend on recording, which depends on it.
    /// </summary>
    public Func<Camera, RecordingPlan?>? RecordingPlanner { get; set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Nothing can be running yet after a restart, so clear any state the last run left behind.
        cameras.MarkAllOffline();

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
                log.LogError(ex, "Camera supervision pass failed.");
            }

            try { await Task.Delay(ReconcileInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        await StopAllPullSourcesAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a camera immediately rather than waiting for the next reconcile pass.
    /// Called when a viewer opens a stream, so the first frame arrives without a two second stall.
    /// </summary>
    public async Task EnsureRunningAsync(string cameraId, CancellationToken cancellationToken)
    {
        _pinned[cameraId] = DateTimeOffset.UtcNow;
        _idleSince.TryRemove(cameraId, out _);
        await ReconcileAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a supervision pass immediately. Used when something happened that changes what should
    /// be running - an agent connecting, for instance - so the dashboard does not wait for the timer.
    /// </summary>
    public Task ReconcileNowAsync(CancellationToken cancellationToken) => ReconcileAsync(cancellationToken);

    /// <summary>Reconciles one camera after the user edits or deletes it.</summary>
    public async Task CameraChangedAsync(string cameraId, CancellationToken cancellationToken)
    {
        // A settings change (resolution, source, credentials) only takes effect on a fresh start,
        // so tear the current session down and let reconciliation bring it back.
        await StopCameraAsync(cameraId, cancellationToken).ConfigureAwait(false);
        await ReconcileAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CameraRemovedAsync(string cameraId, CancellationToken cancellationToken)
    {
        _pinned.TryRemove(cameraId, out _);
        _idleSince.TryRemove(cameraId, out _);
        await StopCameraAsync(cameraId, cancellationToken).ConfigureAwait(false);
        frameBus.Reset(cameraId);
        runtimes.Remove(cameraId);
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        // One pass at a time: an API-triggered pass and the timer pass would otherwise both
        // decide to start the same camera and race on the pull-source dictionary.
        await _reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var camera in cameras.GetAll())
            {
                var runtime = runtimes.Get(camera.Id);
                var wanted = IsWanted(camera, now);

                if (wanted) await EnsureCameraStartedAsync(camera, runtime, cancellationToken).ConfigureAwait(false);
                else await EnsureCameraStoppedAsync(camera, runtime, now, cancellationToken).ConfigureAwait(false);

                ApplyPersistentState(camera, runtime);
            }
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    /// <summary>Whether a camera should be capturing at this instant, and why.</summary>
    private bool IsWanted(Camera camera, DateTimeOffset now)
    {
        if (!camera.Enabled) return false;

        // Privacy mode is an explicit user instruction to stop capturing. It overrides everything,
        // including recording schedules - that is the entire point of the feature.
        if (camera.PrivacyMode) return false;
        if (camera.State == CameraState.Paused) return false;

        if (RecordingCameras.ContainsKey(camera.Id)) return true;
        if (camera.RecordingMode is RecordingMode.Continuous or RecordingMode.Motion or RecordingMode.Scheduled) return true;
        if (frameBus.GetSubscriberCount(camera.Id) > 0) return true;

        // A pin keeps a camera alive briefly after an explicit start request, covering the gap
        // between "user clicked play" and "the browser actually opened the stream".
        if (_pinned.TryGetValue(camera.Id, out var pinnedAt))
        {
            if (now - pinnedAt < IdleGrace) return true;
            _pinned.TryRemove(camera.Id, out _);
        }

        return false;
    }

    private async Task EnsureCameraStartedAsync(Camera camera, CameraRuntime runtime, CancellationToken cancellationToken)
    {
        _idleSince.TryRemove(camera.Id, out _);

        switch (camera.SourceKind)
        {
            case CameraSourceKind.AgentCamera:
            case CameraSourceKind.AndroidPhone:
            case CameraSourceKind.IosPhone:
            {
                if (string.IsNullOrEmpty(camera.DeviceId)) return;
                var connection = agents.Find(camera.DeviceId);
                if (connection is null)
                {
                    // The owning device is offline. That is a normal state, not an error.
                    if (runtime.State != CameraState.Offline)
                    {
                        runtime.State = CameraState.Offline;
                        runtime.ResetMeasurements();
                    }
                    return;
                }
                if (connection.IsCapturing(camera.Id)) return;
                await connection.StartCaptureAsync(camera, cancellationToken).ConfigureAwait(false);
                break;
            }

            case CameraSourceKind.Rtsp:
            case CameraSourceKind.Onvif:
            {
                var wantedPlan = RecordingPlanner?.Invoke(camera);
                if (_pullSources.ContainsKey(camera.Id))
                {
                    // Recording was turned on or off while the stream was running. The recording
                    // output is part of the ffmpeg command line, so it can only change by restarting.
                    if (Equals(_pullPlans.GetValueOrDefault(camera.Id), wantedPlan)) return;

                    log.LogInformation("Recording settings changed for camera {Camera}; restarting its stream.", camera.Id);
                    await StopCameraAsync(camera.Id, cancellationToken).ConfigureAwait(false);
                }
                await StartPullSourceAsync(camera, runtime, wantedPlan, cancellationToken).ConfigureAwait(false);
                break;
            }
        }
    }

    private async Task StartPullSourceAsync(Camera camera, CameraRuntime runtime, RecordingPlan? plan, CancellationToken cancellationToken)
    {
        var ffmpeg = await ffmpegLocator.LocateAsync(settings.Get(SettingsRepository.Keys.FfmpegPath), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!ffmpeg.Available || ffmpeg.Path is null)
        {
            const string message = "ffmpeg is not installed, so network cameras cannot be streamed.";
            if (runtime.LastError != message)
            {
                runtime.LastError = message;
                runtime.State = CameraState.Degraded;
                log.LogWarning("Camera {Camera} cannot start: {Message}", camera.Id, message);
            }
            return;
        }

        var config = CameraSourceConfig.FromJson(camera.ConfigJson);
        if (string.IsNullOrWhiteSpace(config.RtspUrl))
        {
            runtime.LastError = "This camera has no stream URL configured.";
            runtime.State = CameraState.Degraded;
            return;
        }

        var password = secrets.Unprotect(config.PasswordEnc);
        if (password is null && !string.IsNullOrEmpty(config.PasswordEnc))
        {
            runtime.LastError = "The stored camera password could not be decrypted. Re-enter it in the camera settings.";
            runtime.State = CameraState.Degraded;
            log.LogError("Camera {Camera} has an undecryptable password; the secret key may have changed.", camera.Id);
            return;
        }

        var url = config.BuildAuthenticatedRtspUrl(password);
        if (url is null) return;

        var source = new FfmpegPullSource(camera, url, config.Transport, ffmpeg.Path, frameBus, runtime, plan, log);
        if (!_pullSources.TryAdd(camera.Id, source))
        {
            await source.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (plan is null) _pullPlans.TryRemove(camera.Id, out _);
        else _pullPlans[camera.Id] = plan;

        runtime.StartedUtc = DateTimeOffset.UtcNow;
        source.Start();
        log.LogInformation("Started network camera {Camera} ({Url}).", camera.Id, UrlRedactor.Redact(config.RtspUrl));
    }

    private async Task EnsureCameraStoppedAsync(Camera camera, CameraRuntime runtime, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var running = _pullSources.ContainsKey(camera.Id) ||
                      (camera.DeviceId is not null && agents.Find(camera.DeviceId)?.IsCapturing(camera.Id) == true);

        if (!running)
        {
            _idleSince.TryRemove(camera.Id, out _);
            return;
        }

        // Disabled, paused and privacy are immediate; merely having no viewers is not.
        var immediate = !camera.Enabled || camera.PrivacyMode || camera.State == CameraState.Paused;
        if (!immediate)
        {
            var since = _idleSince.GetOrAdd(camera.Id, now);
            if (now - since < IdleGrace) return;
        }

        await StopCameraAsync(camera.Id, cancellationToken).ConfigureAwait(false);
        _idleSince.TryRemove(camera.Id, out _);
    }

    private async Task StopCameraAsync(string cameraId, CancellationToken cancellationToken)
    {
        _pullPlans.TryRemove(cameraId, out _);
        if (_pullSources.TryRemove(cameraId, out var source)) await source.DisposeAsync().ConfigureAwait(false);

        foreach (var connection in agents.All)
        {
            if (connection.IsCapturing(cameraId))
                await connection.StopCaptureAsync(cameraId, cancellationToken).ConfigureAwait(false);
        }

        frameBus.Reset(cameraId);
    }

    /// <summary>
    /// Writes a camera's state back to the database and raises an event when it actually changes.
    /// Only transitions are recorded: writing every pass would fill the event log with noise.
    /// </summary>
    private void ApplyPersistentState(Camera camera, CameraRuntime runtime)
    {
        var effective = camera.PrivacyMode ? CameraState.Privacy
                      : camera.State == CameraState.Paused ? CameraState.Paused
                      : runtime.State;

        runtime.Recording = RecordingCameras.ContainsKey(camera.Id);

        if (effective == camera.State) return;

        cameras.SetState(camera.Id, effective);
        notifier.CameraStateChanged(camera.Id, effective);

        if (effective is CameraState.Online or CameraState.Offline)
        {
            var cameraEvent = new CameraEvent
            {
                CameraId = camera.Id,
                Type = effective == CameraState.Online ? EventType.CameraOnline : EventType.CameraOffline,
                Severity = effective == CameraState.Online ? EventSeverity.Info : EventSeverity.Warning,
                TimestampUtc = DateTimeOffset.UtcNow,
                Detail = effective == CameraState.Offline ? runtime.LastError : null,
            };
            cameraEvent.Id = events.Insert(cameraEvent);
            notifier.EventRaised(cameraEvent);
        }
    }

    private async Task StopAllPullSourcesAsync()
    {
        foreach (var key in _pullSources.Keys.ToArray())
        {
            if (_pullSources.TryRemove(key, out var source)) await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    public override void Dispose()
    {
        _reconcileGate.Dispose();
        base.Dispose();
    }
}
