using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using VisionMesh.Api.Auth;
using VisionMesh.Core.Abstractions;
using VisionMesh.Core.Models;
using VisionMesh.Core.Util;
using VisionMesh.Database.Repositories;
using VisionMesh.Recording;
using VisionMesh.Streaming;
using VisionMesh.Streaming.Fanout;
using VisionMesh.Streaming.Ingest;
using VisionMesh.Streaming.Sources;

namespace VisionMesh.Api.Endpoints;

/// <summary>Camera listing, creation, live streams, snapshots, recording control and PTZ.</summary>
public static class CameraEndpoints
{
    public static void MapCameraEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/cameras").WithTags("Cameras");

        // ---- read ----------------------------------------------------------

        group.MapGet("/", (CameraService service) => Results.Ok(service.GetAll()))
            .RequireViewer()
            .WithName("ListCameras");

        group.MapGet("/groups", (CameraRepository cameras) => Results.Ok(cameras.GetGroups()))
            .RequireViewer()
            .WithName("ListCameraGroups");

        group.MapGet("/{id}", (string id, CameraService service) =>
        {
            var camera = service.GetById(id);
            return camera is null ? NotFound(id) : Results.Ok(camera);
        })
        .RequireViewer()
        .WithName("GetCamera");

        // ---- create --------------------------------------------------------

        group.MapPost("/", async (
            HttpContext http,
            CreateCameraRequest request,
            CameraRepository cameras,
            DeviceRepository devices,
            AgentRegistry agents,
            CameraService service,
            SecretProtector secrets,
            CameraSupervisor supervisor,
            AuthService auth,
            IRealtimeNotifier notifier,
            CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<CameraSourceKind>(request.SourceKind, ignoreCase: true, out var kind))
                return Results.BadRequest(new { error = $"'{request.SourceKind}' is not a camera source VisionMesh understands." });

            var camera = new Camera
            {
                Id = Ids.NewId("cam"),
                Name = service.MakeUniqueName(request.Name),
                SourceKind = kind,
                GroupName = string.IsNullOrWhiteSpace(request.GroupName) ? null : request.GroupName.Trim(),
                CreatedUtc = DateTimeOffset.UtcNow,
                DesiredWidth = Math.Clamp(request.Width ?? 1280, 160, 3840),
                DesiredHeight = Math.Clamp(request.Height ?? 720, 120, 2160),
                DesiredFps = Math.Clamp(request.Fps ?? 15, 1, 60),
                DesiredQuality = Math.Clamp(request.Quality ?? 75, 1, 100),
                State = CameraState.Offline,
            };

            var config = new CameraSourceConfig();

            switch (kind)
            {
                case CameraSourceKind.AgentCamera:
                case CameraSourceKind.AndroidPhone:
                case CameraSourceKind.IosPhone:
                {
                    if (string.IsNullOrWhiteSpace(request.DeviceId) || string.IsNullOrWhiteSpace(request.SourceId))
                        return Results.BadRequest(new { error = "Choose which device and which camera on it to use." });

                    if (devices.GetById(request.DeviceId) is null)
                        return Results.BadRequest(new { error = "That device is not registered with this server." });

                    // Re-adding the same physical camera should be a no-op, not a duplicate tile.
                    if (cameras.GetByDeviceSource(request.DeviceId, request.SourceId) is { } existing)
                        return Results.Conflict(new { error = $"That camera is already added as '{existing.Name}'.", cameraId = existing.Id });

                    camera.DeviceId = request.DeviceId;
                    camera.SourceId = request.SourceId;

                    // Carry the capture device's real name through when the user did not supply one.
                    var advertised = agents.Find(request.DeviceId)?.CaptureDevices
                        .FirstOrDefault(d => string.Equals(d.SourceId, request.SourceId, StringComparison.OrdinalIgnoreCase));
                    if (string.IsNullOrWhiteSpace(request.Name) && advertised is not null)
                        camera.Name = service.MakeUniqueName(advertised.Name);
                    break;
                }

                case CameraSourceKind.Rtsp:
                {
                    if (string.IsNullOrWhiteSpace(request.RtspUrl))
                        return Results.BadRequest(new { error = "Enter the camera's RTSP address." });

                    if (!IsAcceptableStreamUrl(request.RtspUrl, out var problem))
                        return Results.BadRequest(new { error = problem });

                    config.RtspUrl = request.RtspUrl.Trim();
                    config.Username = request.Username;
                    config.PasswordEnc = string.IsNullOrEmpty(request.Password) ? null : secrets.Protect(request.Password);
                    config.Transport = ParseTransport(request.Transport);
                    break;
                }

                case CameraSourceKind.Onvif:
                {
                    if (string.IsNullOrWhiteSpace(request.RtspUrl))
                        return Results.BadRequest(new { error = "The ONVIF camera did not provide a stream address. Probe it again." });

                    config.RtspUrl = request.RtspUrl.Trim();
                    config.OnvifAddress = request.OnvifAddress;
                    config.OnvifProfileToken = request.OnvifProfileToken;
                    config.Username = request.Username;
                    config.PasswordEnc = string.IsNullOrEmpty(request.Password) ? null : secrets.Protect(request.Password);
                    config.Transport = ParseTransport(request.Transport);
                    break;
                }
            }

            camera.ConfigJson = config.ToJson();
            cameras.Insert(camera);

            auth.Audit(http.CurrentUser(), "camera.create", camera.Id, http.ClientAddress(), camera.Name);
            notifier.CameraAdded(camera);
            await supervisor.CameraChangedAsync(camera.Id, cancellationToken);

            return Results.Created($"/api/cameras/{camera.Id}", service.GetById(camera.Id));
        })
        .RequireAdministrator()
        .WithName("CreateCamera")
        .WithSummary("Adds a camera from any supported source.");

        // ---- update / delete ----------------------------------------------

        group.MapPatch("/{id}", async (
            HttpContext http,
            string id,
            UpdateCameraRequest request,
            CameraRepository cameras,
            CameraService service,
            SecretProtector secrets,
            CameraSupervisor supervisor,
            AuthService auth,
            CancellationToken cancellationToken) =>
        {
            var camera = cameras.GetById(id);
            if (camera is null) return NotFound(id);

            var config = CameraSourceConfig.FromJson(camera.ConfigJson);
            var needsRestart = false;

            if (request.Name is { } name && !string.IsNullOrWhiteSpace(name)) camera.Name = name.Trim();
            if (request.GroupName is not null) camera.GroupName = string.IsNullOrWhiteSpace(request.GroupName) ? null : request.GroupName.Trim();
            if (request.Enabled is { } enabled) { camera.Enabled = enabled; needsRestart = true; }
            if (request.RetentionDays is { } retention) camera.RetentionDays = Math.Clamp(retention, 0, 3650);
            if (request.FloorPlanX is { } x) camera.FloorPlanX = x;
            if (request.FloorPlanY is { } y) camera.FloorPlanY = y;

            if (request.RecordingMode is { } mode)
            {
                if (!Enum.TryParse<RecordingMode>(mode, ignoreCase: true, out var parsed))
                    return Results.BadRequest(new { error = $"'{mode}' is not a recording mode." });
                camera.RecordingMode = parsed;
            }

            if (request.Width is { } width) { camera.DesiredWidth = Math.Clamp(width, 160, 3840); needsRestart = true; }
            if (request.Height is { } height) { camera.DesiredHeight = Math.Clamp(height, 120, 2160); needsRestart = true; }
            if (request.Fps is { } fps) { camera.DesiredFps = Math.Clamp(fps, 1, 60); needsRestart = true; }
            if (request.Quality is { } quality) { camera.DesiredQuality = Math.Clamp(quality, 1, 100); needsRestart = true; }

            if (request.RtspUrl is { } url && !string.IsNullOrWhiteSpace(url))
            {
                if (!IsAcceptableStreamUrl(url, out var problem)) return Results.BadRequest(new { error = problem });
                config.RtspUrl = url.Trim();
                needsRestart = true;
            }

            if (request.Username is not null) { config.Username = request.Username; needsRestart = true; }

            // Null means "leave the password alone"; empty string means "remove it".
            if (request.Password is not null)
            {
                config.PasswordEnc = request.Password.Length == 0 ? null : secrets.Protect(request.Password);
                needsRestart = true;
            }

            if (request.Transport is not null) { config.Transport = ParseTransport(request.Transport); needsRestart = true; }
            if (request.ScheduleDays is not null) config.ScheduleDays = NormaliseScheduleDays(request.ScheduleDays);
            if (request.ScheduleStart is not null) config.ScheduleStart = request.ScheduleStart;
            if (request.ScheduleEnd is not null) config.ScheduleEnd = request.ScheduleEnd;
            if (request.MotionSensitivity is { } sensitivity) config.MotionSensitivity = Math.Clamp(sensitivity, 1, 100);

            camera.ConfigJson = config.ToJson();
            cameras.Update(camera);

            auth.Audit(http.CurrentUser(), "camera.update", camera.Id, http.ClientAddress());
            if (needsRestart) await supervisor.CameraChangedAsync(camera.Id, cancellationToken);

            return Results.Ok(service.GetById(camera.Id));
        })
        .RequireAdministrator()
        .WithName("UpdateCamera");

        group.MapDelete("/{id}", async (
            HttpContext http,
            string id,
            CameraRepository cameras,
            CameraSupervisor supervisor,
            AuthService auth,
            IRealtimeNotifier notifier,
            CancellationToken cancellationToken) =>
        {
            var camera = cameras.GetById(id);
            if (camera is null) return NotFound(id);

            await supervisor.CameraRemovedAsync(id, cancellationToken);
            cameras.Delete(id);

            auth.Audit(http.CurrentUser(), "camera.delete", id, http.ClientAddress(), camera.Name);
            notifier.CameraRemoved(id);

            // Recordings are deliberately left on disk. Deleting a camera should not destroy
            // footage that may still be needed; the recordings page can remove it explicitly.
            return Results.Ok(new { ok = true, recordingsKept = true });
        })
        .RequireAdministrator()
        .WithName("DeleteCamera");

        // ---- live media ----------------------------------------------------

        group.MapGet("/{id}/stream.mjpeg", async (
            HttpContext http,
            string id,
            CameraRepository cameras,
            FrameBus frameBus,
            CameraSupervisor supervisor,
            AuthService auth,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthoriseMedia(http, id, auth, out var denied)) return denied!;

            var camera = cameras.GetById(id);
            if (camera is null) return NotFound(id);
            if (camera.PrivacyMode) return PrivacyBlocked();
            if (!camera.Enabled) return Results.Json(new { error = "This camera is switched off." }, statusCode: StatusCodes.Status409Conflict);

            // Start the camera now rather than waiting for the next supervision pass.
            await supervisor.EnsureRunningAsync(id, cancellationToken);

            var log = loggerFactory.CreateLogger("VisionMesh.Stream");
            using var subscription = frameBus.Subscribe(id);

            try
            {
                var frames = await MjpegWriter.WriteStreamAsync(http.Response, subscription, cancellationToken);
                log.LogDebug("Streamed {Frames} frames of camera {Camera} to {Address}.", frames, id, http.ClientAddress());
            }
            catch (OperationCanceledException)
            {
                // The viewer navigated away. Entirely normal.
            }

            return Results.Empty;
        })
        .WithName("StreamCamera")
        .WithSummary("Live MJPEG stream. Accepts a session cookie, a bearer token, or a stream token.");

        group.MapGet("/{id}/snapshot.jpg", async (
            HttpContext http,
            string id,
            CameraRepository cameras,
            FrameBus frameBus,
            CameraSupervisor supervisor,
            AuthService auth,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthoriseMedia(http, id, auth, out var denied)) return denied!;

            var camera = cameras.GetById(id);
            if (camera is null) return NotFound(id);
            if (camera.PrivacyMode) return PrivacyBlocked();

            var frame = frameBus.GetLatestFrame(id);
            if (frame is null)
            {
                // No cached frame: start the camera and wait briefly for the first one.
                await supervisor.EnsureRunningAsync(id, cancellationToken);
                using var subscription = frameBus.Subscribe(id);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));

                frame = await subscription.ReadAsync(timeout.Token);
            }

            if (frame is null)
            {
                return Results.Json(
                    new { error = "The camera did not produce a picture in time.", code = "no_frame" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            http.Response.Headers.CacheControl = "no-store";
            return Results.File(frame.Jpeg.ToArray(), "image/jpeg");
        })
        .WithName("GetCameraSnapshot");

        group.MapPost("/{id}/stream-token", (HttpContext http, string id, CameraRepository cameras, AuthService auth) =>
        {
            if (cameras.GetById(id) is null) return NotFound(id);

            // For clients that cannot attach an Authorization header to a media request.
            // Scoped to one camera and valid for two minutes.
            var (token, expires) = auth.IssueStreamToken(id, http.CurrentUser());
            return Results.Ok(new { token, expiresUtc = expires });
        })
        .RequireViewer()
        .WithName("CreateStreamToken");

        // ---- control -------------------------------------------------------

        group.MapPost("/{id}/privacy", async (
            HttpContext http, string id, bool enabled,
            CameraRepository cameras, CameraSupervisor supervisor, EventRepository events,
            AuthService auth, IRealtimeNotifier notifier, CancellationToken cancellationToken) =>
        {
            var camera = cameras.GetById(id);
            if (camera is null) return NotFound(id);

            camera.PrivacyMode = enabled;
            cameras.Update(camera);

            var cameraEvent = new CameraEvent
            {
                CameraId = id,
                Type = enabled ? EventType.PrivacyEnabled : EventType.PrivacyDisabled,
                Severity = EventSeverity.Info,
                TimestampUtc = DateTimeOffset.UtcNow,
                Detail = $"Set by {http.CurrentUser().Username}.",
            };
            cameraEvent.Id = events.Insert(cameraEvent);
            notifier.EventRaised(cameraEvent);

            auth.Audit(http.CurrentUser(), enabled ? "camera.privacy.on" : "camera.privacy.off", id, http.ClientAddress());
            await supervisor.CameraChangedAsync(id, cancellationToken);

            return Results.Ok(new { ok = true, privacyMode = enabled });
        })
        .RequireOperator()
        .WithName("SetCameraPrivacy")
        .WithSummary("Stops or resumes all capture and recording for a camera.");

        group.MapPost("/{id}/pause", async (
            HttpContext http, string id, bool paused,
            CameraRepository cameras, CameraSupervisor supervisor, AuthService auth, CancellationToken cancellationToken) =>
        {
            var camera = cameras.GetById(id);
            if (camera is null) return NotFound(id);

            camera.State = paused ? CameraState.Paused : CameraState.Offline;
            cameras.Update(camera);
            auth.Audit(http.CurrentUser(), paused ? "camera.pause" : "camera.resume", id, http.ClientAddress());
            await supervisor.CameraChangedAsync(id, cancellationToken);

            return Results.Ok(new { ok = true, paused });
        })
        .RequireOperator()
        .WithName("SetCameraPaused");

        group.MapPost("/{id}/record", (
            HttpContext http, string id, bool start,
            CameraRepository cameras, RecordingEngine recordingEngine, AuthService auth) =>
        {
            var camera = cameras.GetById(id);
            if (camera is null) return NotFound(id);
            if (camera.PrivacyMode) return PrivacyBlocked();

            if (start)
            {
                if (!recordingEngine.StartManualRecording(id))
                {
                    return Results.Json(
                        new { error = "Recording needs ffmpeg, which is not installed on the server.", code = "ffmpeg_missing" },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }
            else
            {
                recordingEngine.StopManualRecording(id);
            }

            auth.Audit(http.CurrentUser(), start ? "camera.record.start" : "camera.record.stop", id, http.ClientAddress());
            return Results.Ok(new { ok = true, recording = start });
        })
        .RequireOperator()
        .WithName("SetCameraRecording");

        group.MapPost("/{id}/ptz", async (
            HttpContext http, string id, PtzRequest request,
            CameraRepository cameras, SecretProtector secrets, IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory, AuthService auth, CancellationToken cancellationToken) =>
        {
            var camera = cameras.GetById(id);
            if (camera is null) return NotFound(id);
            if (!camera.PtzSupported)
                return Results.BadRequest(new { error = "This camera does not support pan, tilt or zoom.", code = "ptz_unsupported" });

            var config = CameraSourceConfig.FromJson(camera.ConfigJson);
            if (string.IsNullOrEmpty(config.OnvifAddress) || string.IsNullOrEmpty(config.OnvifProfileToken))
                return Results.BadRequest(new { error = "This camera has no ONVIF PTZ configuration." });

            var client = new OnvifClient(httpClientFactory.CreateClient("onvif"), loggerFactory.CreateLogger("VisionMesh.Onvif"));
            var password = secrets.Unprotect(config.PasswordEnc);

            try
            {
                var capabilities = await client.GetCapabilitiesAsync(config.OnvifAddress, config.Username, password, cancellationToken);
                if (capabilities.PtzServiceUri is null)
                    return Results.BadRequest(new { error = "The camera did not report a PTZ service." });

                if (request.Stop)
                {
                    await client.StopAsync(capabilities.PtzServiceUri, config.OnvifProfileToken, config.Username, password, cancellationToken);
                }
                else
                {
                    await client.ContinuousMoveAsync(capabilities.PtzServiceUri, config.OnvifProfileToken,
                        request.Pan, request.Tilt, request.Zoom, config.Username, password, cancellationToken);
                }

                auth.Audit(http.CurrentUser(), "camera.ptz", id, http.ClientAddress(),
                    request.Stop ? "stop" : $"pan={request.Pan} tilt={request.Tilt} zoom={request.Zoom}");
                return Results.Ok(new { ok = true });
            }
            catch (OnvifClient.OnvifAuthenticationException ex)
            {
                return Results.Json(new { error = ex.Message, code = "camera_auth" }, statusCode: StatusCodes.Status502BadGateway);
            }
            catch (Exception ex) when (ex is OnvifClient.OnvifRequestException or HttpRequestException or TaskCanceledException)
            {
                return Results.Json(new { error = $"The camera did not accept the command: {ex.Message}" }, statusCode: StatusCodes.Status502BadGateway);
            }
        })
        .RequireOperator()
        .WithName("ControlPtz");
    }

    // ---- helpers -----------------------------------------------------------

    private static IResult NotFound(string id)
        => Results.Json(new { error = "That camera does not exist.", cameraId = id }, statusCode: StatusCodes.Status404NotFound);

    private static IResult PrivacyBlocked()
        => Results.Json(
            new { error = "This camera is in privacy mode. Turn privacy mode off to view it.", code = "privacy_mode" },
            statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// Authorises a media request from a session (header or cookie) or from a camera-scoped
    /// stream token. Media endpoints cannot use the normal filter because they must accept the
    /// stream token, which is intentionally not a session.
    /// </summary>
    private static bool TryAuthoriseMedia(HttpContext http, string cameraId, AuthService auth, out IResult? denied)
    {
        var user = auth.Authenticate(http);

        if (user is null)
        {
            var streamToken = http.Request.Query["token"].ToString();
            if (!string.IsNullOrEmpty(streamToken)) user = auth.ValidateStreamToken(streamToken, cameraId);
        }

        if (user is null)
        {
            denied = Results.Json(new { error = "Sign in to view this camera.", code = "unauthenticated" },
                                  statusCode: StatusCodes.Status401Unauthorized);
            return false;
        }

        http.Items[AuthEndpointFilter.UserItemKey] = user;
        denied = null;
        return true;
    }

    private static RtspTransport ParseTransport(string? value)
        => Enum.TryParse<RtspTransport>(value, ignoreCase: true, out var parsed) ? parsed : RtspTransport.Auto;

    /// <summary>Keeps only the seven day flags, so a malformed value cannot corrupt schedule checks.</summary>
    private static string? NormaliseScheduleDays(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = new string(value.Where(c => c is '0' or '1').ToArray());
        return trimmed.Length == 7 ? trimmed : null;
    }

    /// <summary>
    /// Rejects stream URLs that are not a media protocol.
    /// The URL is handed to ffmpeg, which speaks dozens of protocols including <c>file:</c> and
    /// <c>concat:</c>; without this an administrator could be tricked into pointing a "camera" at
    /// a local file. Restricting to network media protocols keeps that door shut.
    /// </summary>
    private static bool IsAcceptableStreamUrl(string url, out string? problem)
    {
        problem = null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            problem = "That does not look like a valid address. An RTSP address looks like rtsp://192.168.1.50:554/stream1";
            return false;
        }

        var allowed = new[] { "rtsp", "rtsps", "rtmp", "rtmps", "http", "https" };
        if (!allowed.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            problem = $"'{uri.Scheme}' is not a supported camera protocol. Use rtsp, rtsps, rtmp, http or https.";
            return false;
        }

        return true;
    }
}
