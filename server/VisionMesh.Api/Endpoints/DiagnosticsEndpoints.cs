using System.Diagnostics;
using System.Net.NetworkInformation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using VisionMesh.Api.Auth;
using VisionMesh.Core.Models;
using VisionMesh.Core.Util;
using VisionMesh.Database.Repositories;
using VisionMesh.Recording;
using VisionMesh.Streaming;
using VisionMesh.Streaming.Fanout;
using VisionMesh.Streaming.Ingest;
using VisionMesh.Streaming.Sources;

namespace VisionMesh.Api.Endpoints;

/// <summary>One step of a diagnosis, in language a non-technical user can act on.</summary>
/// <param name="Status">ok, warning, failed or skipped.</param>
public sealed record DiagnosticStep(string Name, string Status, string Message, string? Advice = null, object? Detail = null)
{
    public static DiagnosticStep Ok(string name, string message, object? detail = null) => new(name, "ok", message, null, detail);
    public static DiagnosticStep Warning(string name, string message, string? advice = null, object? detail = null) => new(name, "warning", message, advice, detail);
    public static DiagnosticStep Failed(string name, string message, string? advice = null, object? detail = null) => new(name, "failed", message, advice, detail);
    public static DiagnosticStep Skipped(string name, string message) => new(name, "skipped", message);
}

/// <summary>
/// The "Fix Camera" wizard: works through the chain from device to picture and reports, in plain
/// language, the first thing that is actually wrong.
///
/// Every check performs a real test - it pings the camera, opens its stream, counts frames. It
/// never infers a result from stored state, because stored state is exactly what is wrong when a
/// user reaches for this button.
/// </summary>
public static class DiagnosticsEndpoints
{
    public static void MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/cameras/{id}/diagnose").WithTags("Diagnostics").RequireOperator();

        group.MapPost("/", async (
            string id,
            CameraRepository cameras,
            AgentRegistry agents,
            FrameBus frameBus,
            CameraRuntimeRegistry runtimes,
            CameraSupervisor supervisor,
            SettingsRepository settings,
            StorageManager storage,
            RecordingEngine recordingEngine,
            FfmpegLocator ffmpegLocator,
            SecretProtector secrets,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var camera = cameras.GetById(id);
            if (camera is null) return Results.NotFound(new { error = "That camera does not exist." });

            var steps = new List<DiagnosticStep>();
            var log = loggerFactory.CreateLogger("VisionMesh.Diagnostics");

            // 1. Is the camera even meant to be running?
            if (!camera.Enabled)
            {
                steps.Add(DiagnosticStep.Failed("Camera enabled", "This camera is switched off.",
                    "Turn the camera on in its settings."));
                return Results.Ok(Summarise(camera, steps));
            }
            steps.Add(DiagnosticStep.Ok("Camera enabled", "The camera is switched on."));

            if (camera.PrivacyMode)
            {
                steps.Add(DiagnosticStep.Failed("Privacy mode", "Privacy mode is on, so this camera is not capturing anything.",
                    "Turn privacy mode off to start viewing and recording again."));
                return Results.Ok(Summarise(camera, steps));
            }
            steps.Add(DiagnosticStep.Ok("Privacy mode", "Privacy mode is off."));

            // 2. Source-specific reachability.
            var config = CameraSourceConfig.FromJson(camera.ConfigJson);

            if (camera.SourceKind is CameraSourceKind.Rtsp or CameraSourceKind.Onvif)
            {
                await DiagnoseNetworkCameraAsync(steps, camera, config, settings, ffmpegLocator, secrets, log, cancellationToken);
            }
            else
            {
                DiagnoseAgentCamera(steps, camera, agents);
            }

            // 3. Are frames actually arriving?
            await DiagnoseLiveFramesAsync(steps, camera, frameBus, supervisor, runtimes, cancellationToken);

            // 4. Can it record, if it is meant to?
            DiagnoseRecording(steps, camera, storage, recordingEngine);

            return Results.Ok(Summarise(camera, steps));
        })
        .WithName("DiagnoseCamera")
        .WithSummary("Runs an end-to-end check of one camera and explains what is wrong.");

        // A quick connection test, used by the Test Connection button on the camera panel.
        app.MapPost("/api/cameras/{id}/test", async (
            string id,
            CameraRepository cameras,
            FrameBus frameBus,
            CameraSupervisor supervisor,
            CameraRuntimeRegistry runtimes,
            CancellationToken cancellationToken) =>
        {
            var camera = cameras.GetById(id);
            if (camera is null) return Results.NotFound(new { error = "That camera does not exist." });
            if (camera.PrivacyMode)
                return Results.Ok(new { ok = false, reason = "Privacy mode is on, so this camera is not capturing." });

            await supervisor.EnsureRunningAsync(id, cancellationToken);

            var started = Stopwatch.StartNew();
            using var subscription = frameBus.Subscribe(id);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));

            // Count frames over a short window: one frame proves connection, several prove the
            // stream is actually flowing at a usable rate.
            var frames = 0;
            long bytes = 0;
            TimeSpan? firstFrameAt = null;

            try
            {
                while (frames < 30)
                {
                    var frame = await subscription.ReadAsync(timeout.Token);
                    if (frame is null) break;
                    firstFrameAt ??= started.Elapsed;
                    frames++;
                    bytes += frame.Jpeg.Length;
                    if (started.Elapsed > TimeSpan.FromSeconds(3) && frames >= 2) break;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }

            var runtime = runtimes.Find(id);
            var elapsed = started.Elapsed;

            return Results.Ok(new
            {
                ok = frames > 0,
                framesReceived = frames,
                timeToFirstFrameMs = firstFrameAt is { } first ? Math.Round(first.TotalMilliseconds) : (double?)null,
                measuredFps = frames >= 2 && elapsed.TotalSeconds > 0 ? Math.Round(frames / elapsed.TotalSeconds, 1) : (double?)null,
                measuredBitrateBps = frames >= 2 && elapsed.TotalSeconds > 0 ? Math.Round(bytes * 8 / elapsed.TotalSeconds) : (double?)null,
                resolution = runtime is { Width: > 0 } ? $"{runtime.Width}x{runtime.Height}" : null,
                latencyMs = runtime?.GetLatencyMs(),
                error = frames == 0 ? runtime?.LastError ?? "No video arrived from this camera within 12 seconds." : null,
            });
        })
        .RequireOperator()
        .WithTags("Diagnostics")
        .WithName("TestCameraConnection");
    }

    private static void DiagnoseAgentCamera(List<DiagnosticStep> steps, Camera camera, AgentRegistry agents)
    {
        if (string.IsNullOrEmpty(camera.DeviceId))
        {
            steps.Add(DiagnosticStep.Failed("Device", "This camera is not linked to any device.",
                "Delete the camera and add it again."));
            return;
        }

        var connection = agents.Find(camera.DeviceId);
        if (connection is null)
        {
            steps.Add(DiagnosticStep.Failed("Device online", "The computer or phone this camera lives on is not connected.",
                "Check that the device is switched on, connected to the network, and that VisionMesh is running on it."));
            return;
        }

        steps.Add(DiagnosticStep.Ok("Device online", $"{connection.DeviceName} is connected.",
            new { connection.Platform, connection.AgentVersion, connectedSince = connection.ConnectedUtc }));

        var advertised = connection.CaptureDevices
            .FirstOrDefault(d => string.Equals(d.SourceId, camera.SourceId, StringComparison.OrdinalIgnoreCase));

        if (advertised is null)
        {
            steps.Add(DiagnosticStep.Failed("Camera present", "The device is connected, but this camera is no longer attached to it.",
                "Plug the camera back in, or check that another program has not taken it over."));
            return;
        }

        if (!advertised.Available)
        {
            steps.Add(DiagnosticStep.Failed("Camera available", advertised.Unavailable ?? "The camera is attached but cannot be opened.",
                "Another program may be using the camera, or VisionMesh may not have permission to use it."));
            return;
        }

        steps.Add(DiagnosticStep.Ok("Camera available", $"'{advertised.Name}' is attached and can be opened.",
            new { formats = advertised.Formats.Count }));
    }

    private static async Task DiagnoseNetworkCameraAsync(
        List<DiagnosticStep> steps,
        Camera camera,
        CameraSourceConfig config,
        SettingsRepository settings,
        FfmpegLocator ffmpegLocator,
        SecretProtector secrets,
        ILogger log,
        CancellationToken cancellationToken)
    {
        var ffmpeg = await ffmpegLocator.LocateAsync(settings.Get(SettingsRepository.Keys.FfmpegPath), cancellationToken: cancellationToken);
        if (!ffmpeg.Available)
        {
            steps.Add(DiagnosticStep.Failed("ffmpeg", "ffmpeg is not installed on the server.",
                "Network cameras need ffmpeg. Install it, then set its location in Settings if VisionMesh still cannot find it."));
            return;
        }
        steps.Add(DiagnosticStep.Ok("ffmpeg", $"ffmpeg {ffmpeg.Version} is available."));

        if (string.IsNullOrWhiteSpace(config.RtspUrl))
        {
            steps.Add(DiagnosticStep.Failed("Stream address", "No stream address is configured for this camera.",
                "Edit the camera and enter its RTSP address."));
            return;
        }

        if (!Uri.TryCreate(config.RtspUrl, UriKind.Absolute, out var uri))
        {
            steps.Add(DiagnosticStep.Failed("Stream address", "The configured stream address is not a valid URL.",
                "Edit the camera and correct the address."));
            return;
        }

        steps.Add(DiagnosticStep.Ok("Stream address", $"Configured as {UrlRedactor.Redact(config.RtspUrl)}."));

        // Ping is informational: plenty of cameras drop ICMP while serving RTSP perfectly.
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(uri.Host, 2000);
            steps.Add(reply.Status == IPStatus.Success
                ? DiagnosticStep.Ok("Network reachable", $"{uri.Host} replied in {reply.RoundtripTime} ms.", new { latencyMs = reply.RoundtripTime })
                : DiagnosticStep.Warning("Network reachable", $"{uri.Host} did not reply to a ping ({reply.Status}).",
                    "Many cameras ignore pings on purpose, so this alone is not a fault. The stream test below is what matters."));
        }
        catch (Exception ex) when (ex is PingException or System.Net.Sockets.SocketException)
        {
            steps.Add(DiagnosticStep.Warning("Network reachable", $"Could not ping {uri.Host}: {ex.Message}",
                "Check that the address is right and that the camera is on the same network as the server."));
        }

        // Credential decryption is a real failure mode after restoring a database without its key.
        if (!string.IsNullOrEmpty(config.PasswordEnc))
        {
            var password = secrets.Unprotect(config.PasswordEnc);
            if (password is null)
            {
                steps.Add(DiagnosticStep.Failed("Stored password", "The saved camera password could not be decrypted.",
                    "This usually means the server's secret key file was replaced. Edit the camera and enter the password again."));
                return;
            }
            steps.Add(DiagnosticStep.Ok("Stored password", "A password is saved and can be read."));
        }
        else if (!string.IsNullOrEmpty(config.Username))
        {
            steps.Add(DiagnosticStep.Warning("Stored password", "A username is set but no password is saved.",
                "If the camera needs a password, edit the camera and enter it."));
        }
    }

    private static async Task DiagnoseLiveFramesAsync(
        List<DiagnosticStep> steps,
        Camera camera,
        FrameBus frameBus,
        CameraSupervisor supervisor,
        CameraRuntimeRegistry runtimes,
        CancellationToken cancellationToken)
    {
        // If an earlier step already failed hard there is no point waiting fifteen seconds for
        // frames that cannot arrive.
        if (steps.Any(s => s.Status == "failed"))
        {
            steps.Add(DiagnosticStep.Skipped("Live video", "Skipped because an earlier check failed."));
            return;
        }

        await supervisor.EnsureRunningAsync(camera.Id, cancellationToken);

        using var subscription = frameBus.Subscribe(camera.Id);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        var started = Stopwatch.StartNew();
        var frames = 0;

        try
        {
            while (frames < 5 && started.Elapsed < TimeSpan.FromSeconds(15))
            {
                var frame = await subscription.ReadAsync(timeout.Token);
                if (frame is null) break;
                frames++;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }

        var runtime = runtimes.Find(camera.Id);

        if (frames == 0)
        {
            steps.Add(DiagnosticStep.Failed("Live video", "No video arrived from this camera.",
                runtime?.LastError is { Length: > 0 } error
                    ? $"The camera reported: {error}"
                    : "Check the camera's address, username and password, and that nothing else is already using it."));
            return;
        }

        var fps = runtime?.GetFps();
        var detail = new { frames, fps, resolution = runtime is { Width: > 0 } ? $"{runtime.Width}x{runtime.Height}" : null };

        if (fps is { } measured && measured < camera.DesiredFps * 0.5)
        {
            steps.Add(DiagnosticStep.Warning("Live video",
                $"Video is arriving, but at {measured:0.#} frames per second instead of the {camera.DesiredFps} requested.",
                "This usually means the network is congested or the server is busy. Try a lower resolution or frame rate.",
                detail));
        }
        else
        {
            steps.Add(DiagnosticStep.Ok("Live video", $"Video is arriving normally ({frames} frames received).", detail));
        }
    }

    private static void DiagnoseRecording(List<DiagnosticStep> steps, Camera camera, StorageManager storage, RecordingEngine recordingEngine)
    {
        if (camera.RecordingMode == RecordingMode.Off)
        {
            steps.Add(DiagnosticStep.Skipped("Recording", "This camera is not set to record."));
            return;
        }

        if (!recordingEngine.RecordingAvailable)
        {
            steps.Add(DiagnosticStep.Failed("Recording", "Recording is switched on for this camera, but ffmpeg is not installed.",
                "Install ffmpeg on the server. Until then nothing is being recorded."));
            return;
        }

        var directory = storage.GetCameraDirectory(camera);
        var (writable, error) = StorageManager.TestWritable(directory);
        if (!writable)
        {
            steps.Add(DiagnosticStep.Failed("Recording folder", $"VisionMesh cannot write to '{directory}': {error}",
                "Check the recordings folder in Settings, and that the server has permission to write there."));
            return;
        }
        steps.Add(DiagnosticStep.Ok("Recording folder", $"Recordings can be written to {directory}."));

        var info = storage.GetStorageInfo();
        if (info.TotalBytes > 0 && info.FreeBytes < 1024L * 1024 * 1024)
        {
            steps.Add(DiagnosticStep.Warning("Disk space", $"Only {info.FreeBytes / (1024 * 1024)} MB of disk space is left.",
                "Reduce the retention period, set a storage limit, or free up space."));
        }
        else
        {
            steps.Add(DiagnosticStep.Ok("Disk space", $"{info.FreeBytes / (1024 * 1024 * 1024)} GB free."));
        }

        steps.Add(recordingEngine.IsRecording(camera.Id)
            ? DiagnosticStep.Ok("Recording active", "This camera is recording right now.")
            : DiagnosticStep.Ok("Recording active", camera.RecordingMode == RecordingMode.Motion
                ? "Waiting for motion. Nothing is being recorded at this moment, which is expected."
                : "Not recording at this moment."));
    }

    private static object Summarise(Camera camera, List<DiagnosticStep> steps)
    {
        var failed = steps.FirstOrDefault(s => s.Status == "failed");
        var warnings = steps.Count(s => s.Status == "warning");

        return new
        {
            cameraId = camera.Id,
            cameraName = camera.Name,
            healthy = failed is null,
            summary = failed is not null
                ? failed.Message
                : warnings > 0
                    ? $"The camera is working, with {warnings} thing(s) worth looking at."
                    : "Everything checked out. This camera is working normally.",
            recommendedAction = failed?.Advice,
            steps,
        };
    }
}
