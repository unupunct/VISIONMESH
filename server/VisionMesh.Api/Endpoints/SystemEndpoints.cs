using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using VisionMesh.Api.Auth;
using VisionMesh.Core.Models;
using VisionMesh.Core.Util;
using VisionMesh.Database.Repositories;
using VisionMesh.Recording;
using VisionMesh.Streaming.Ingest;
using VisionMesh.Streaming.Sources;

namespace VisionMesh.Api.Endpoints;

/// <summary>Sign-in, first-run setup, server health and settings.</summary>
public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        MapAuth(app);
        MapSetup(app);
        MapSystem(app);
        MapSettings(app);
    }

    private static void MapAuth(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Authentication");

        group.MapPost("/login", (HttpContext http, LoginRequest request, AuthService auth) =>
        {
            var address = http.ClientAddress();
            var result = auth.Login(request.Username, request.Password, address, http.Request.Headers.UserAgent.ToString());

            if (!result.Success || result.Token is null || result.ExpiresUtc is null)
            {
                return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status401Unauthorized);
            }

            // The cookie is what makes <img src="...stream.mjpeg"> work in a browser without
            // putting a credential in the URL.
            auth.WriteSessionCookie(http, result.Token, result.ExpiresUtc.Value);

            return Results.Ok(new
            {
                token = result.Token,
                expiresUtc = result.ExpiresUtc,
                user = new { id = result.User!.Id, username = result.User.Username, role = result.User.Role.ToString() },
            });
        })
        .WithName("Login")
        .WithSummary("Signs in and returns a session token.");

        group.MapPost("/logout", (HttpContext http, AuthService auth) =>
        {
            var token = AuthService.ExtractSessionToken(http);
            if (token is not null) auth.Logout(token, auth.Authenticate(http), http.ClientAddress());
            AuthService.ClearSessionCookie(http);
            return Results.Ok(new { ok = true });
        })
        .WithName("Logout");

        group.MapGet("/me", (HttpContext http) =>
        {
            var user = http.CurrentUser();
            return Results.Ok(new { id = user.Id, username = user.Username, role = user.Role.ToString() });
        })
        .RequireViewer()
        .WithName("GetCurrentUser");
    }

    private static void MapSetup(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/setup").WithTags("Setup");

        // Deliberately unauthenticated: before the first administrator exists there is nobody who
        // could authenticate. It stops working the moment a user exists.
        group.MapGet("/status", (UserRepository users, SettingsRepository settings, NetworkInfoService network) =>
        {
            var needsSetup = users.Count() == 0;
            return Results.Ok(new
            {
                needsSetup,
                serverName = settings.GetString(SettingsRepository.Keys.ServerName, "VisionMesh"),
                suggestedRecordingsPath = StorageManager.DefaultRoot,
                addresses = network.GetDashboardUrls(),
            });
        })
        .WithName("GetSetupStatus");

        // Also unauthenticated, and for the same reason: the wizard has to be able to check the
        // recordings folder before the account that would authorise the check exists. It stops
        // working the moment setup is complete, and it reveals nothing beyond whether a path is
        // writable by the server's own account.
        group.MapPost("/test-path", (SettingsRequest request, UserRepository users) =>
        {
            if (users.Count() > 0)
            {
                return Results.Json(new { error = "This server is already set up." }, statusCode: StatusCodes.Status409Conflict);
            }

            if (string.IsNullOrWhiteSpace(request.RecordingsPath))
                return Results.BadRequest(new { error = "Enter a folder to test." });

            var (writable, error) = StorageManager.TestWritable(request.RecordingsPath.Trim());
            return Results.Ok(new { writable, error });
        })
        .WithName("TestSetupStoragePath");

        group.MapPost("/", (HttpContext http, SetupRequest request, UserRepository users, SettingsRepository settings, AuthService auth) =>
        {
            if (users.Count() > 0)
            {
                return Results.Json(new { error = "This server is already set up." }, statusCode: StatusCodes.Status409Conflict);
            }

            if (string.IsNullOrWhiteSpace(request.AdminUsername))
                return Results.BadRequest(new { error = "Choose a username for the administrator account." });

            var passwordProblem = ValidatePassword(request.AdminPassword);
            if (passwordProblem is not null) return Results.BadRequest(new { error = passwordProblem });

            var recordingsPath = string.IsNullOrWhiteSpace(request.RecordingsPath) ? StorageManager.DefaultRoot : request.RecordingsPath.Trim();
            var (writable, error) = StorageManager.TestWritable(recordingsPath);
            if (!writable)
            {
                return Results.BadRequest(new { error = $"VisionMesh cannot write to '{recordingsPath}': {error}" });
            }

            var admin = new User
            {
                Id = Ids.NewId("usr"),
                Username = request.AdminUsername.Trim(),
                PasswordHash = PasswordHasher.Hash(request.AdminPassword),
                Role = UserRole.Administrator,
                CreatedUtc = DateTimeOffset.UtcNow,
            };
            users.Insert(admin);

            settings.Set(SettingsRepository.Keys.ServerName, string.IsNullOrWhiteSpace(request.ServerName) ? "VisionMesh" : request.ServerName.Trim());
            settings.Set(SettingsRepository.Keys.RecordingsPath, recordingsPath);
            settings.SetInt(SettingsRepository.Keys.RetentionDays, Math.Clamp(request.RetentionDays, 0, 3650));
            settings.SetBool(SettingsRepository.Keys.SetupComplete, true);

            var login = auth.Login(admin.Username, request.AdminPassword, http.ClientAddress(), http.Request.Headers.UserAgent.ToString());
            if (login is { Success: true, Token: not null, ExpiresUtc: not null })
            {
                auth.WriteSessionCookie(http, login.Token, login.ExpiresUtc.Value);
            }

            return Results.Ok(new
            {
                ok = true,
                token = login.Token,
                user = new { id = admin.Id, username = admin.Username, role = admin.Role.ToString() },
            });
        })
        .WithName("CompleteSetup")
        .WithSummary("Creates the first administrator and finishes first-run configuration.");
    }

    private static void MapSystem(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/system").WithTags("System").RequireViewer();

        group.MapGet("/", async (
            SettingsRepository settings,
            CameraRepository cameras,
            DeviceRepository devices,
            AgentRegistry agents,
            StorageManager storage,
            RecordingEngine recordingEngine,
            FfmpegLocator ffmpegLocator,
            SystemMetricsService metrics,
            CancellationToken cancellationToken) =>
        {
            var ffmpeg = await ffmpegLocator.LocateAsync(settings.Get(SettingsRepository.Keys.FfmpegPath), cancellationToken: cancellationToken);
            var allCameras = cameras.GetAll();

            var health = new SystemHealth
            {
                ServerName = settings.GetString(SettingsRepository.Keys.ServerName, "VisionMesh"),
                Version = BuildInfo.Version,
                Platform = BuildInfo.PlatformDescription,
                Uptime = metrics.Uptime,
                CpuPercent = metrics.GetCpuPercent(),
                ProcessMemoryBytes = metrics.GetProcessMemoryBytes(),
                MachineTotalMemoryBytes = metrics.GetMachineTotalMemoryBytes(),
                MachineAvailableMemoryBytes = metrics.GetMachineAvailableMemoryBytes(),
                CameraCount = allCameras.Count,
                CamerasOnline = allCameras.Count(c => c.State == CameraState.Online),
                CamerasRecording = allCameras.Count(c => recordingEngine.IsRecording(c.Id)),
                DeviceCount = devices.GetAll().Count,
                DevicesOnline = agents.Count,
                Storage = storage.GetStorageInfo(),
                FfmpegAvailable = ffmpeg.Available,
                FfmpegPath = ffmpeg.Path,
                FfmpegVersion = ffmpeg.Version,
            };

            return Results.Ok(health);
        })
        .WithName("GetSystemHealth")
        .WithSummary("Server health, camera counts and storage.");

        group.MapGet("/capabilities", async (SettingsRepository settings, FfmpegLocator ffmpegLocator, CancellationToken cancellationToken) =>
        {
            var ffmpeg = await ffmpegLocator.LocateAsync(settings.Get(SettingsRepository.Keys.FfmpegPath), cancellationToken: cancellationToken);

            // Clients use this to hide features that genuinely cannot work on this install,
            // rather than offering a button that fails when pressed.
            return Results.Ok(new
            {
                ffmpeg = new { available = ffmpeg.Available, version = ffmpeg.Version, path = ffmpeg.Path },
                networkCameras = ffmpeg.Available,
                recording = ffmpeg.Available,
                onvifDiscovery = true,
                motionDetection = ffmpeg.Available,
                webRtc = false,
                notes = ffmpeg.Available
                    ? null
                    : "ffmpeg is not installed. RTSP and ONVIF cameras, recording and motion detection are unavailable until it is.",
            });
        })
        .WithName("GetCapabilities");

        group.MapGet("/network", (NetworkInfoService network) => Results.Ok(network.GetNetworkStatus()))
            .WithName("GetNetworkStatus")
            .WithSummary("Interfaces, addresses and the URLs the dashboard is reachable on.");

        group.MapGet("/audit", (AuditRepository audit, int? limit, int? offset) =>
            Results.Ok(audit.Query(limit ?? 100, offset ?? 0)))
            .RequireAdministrator()
            .WithName("GetAuditLog");
    }

    private static void MapSettings(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings").WithTags("Settings");

        group.MapGet("/", (SettingsRepository settings, StorageManager storage) => Results.Ok(new
        {
            serverName = settings.GetString(SettingsRepository.Keys.ServerName, "VisionMesh"),
            recordingsPath = storage.GetRoot(),
            retentionDays = settings.GetInt(SettingsRepository.Keys.RetentionDays, StorageManager.DefaultRetentionDays),
            storageLimitGb = settings.GetInt(SettingsRepository.Keys.StorageLimitGb, 0),
            motionSensitivity = settings.GetInt(SettingsRepository.Keys.MotionSensitivity, 50),
            ffmpegPath = settings.Get(SettingsRepository.Keys.FfmpegPath),
            advancedMode = settings.GetBool(SettingsRepository.Keys.AdvancedMode, false),
        }))
        .RequireViewer()
        .WithName("GetSettings");

        group.MapPut("/", async (
            HttpContext http,
            SettingsRequest request,
            SettingsRepository settings,
            AuthService auth,
            FfmpegLocator ffmpegLocator,
            CancellationToken cancellationToken) =>
        {
            if (request.ServerName is { } name && !string.IsNullOrWhiteSpace(name))
                settings.Set(SettingsRepository.Keys.ServerName, name.Trim());

            if (request.RecordingsPath is { } path && !string.IsNullOrWhiteSpace(path))
            {
                var (writable, error) = StorageManager.TestWritable(path.Trim());
                if (!writable) return Results.BadRequest(new { error = $"VisionMesh cannot write to '{path}': {error}" });
                settings.Set(SettingsRepository.Keys.RecordingsPath, path.Trim());
            }

            if (request.RetentionDays is { } days) settings.SetInt(SettingsRepository.Keys.RetentionDays, Math.Clamp(days, 0, 3650));
            if (request.StorageLimitGb is { } limit) settings.SetInt(SettingsRepository.Keys.StorageLimitGb, Math.Max(0, limit));
            if (request.MotionSensitivity is { } sensitivity) settings.SetInt(SettingsRepository.Keys.MotionSensitivity, Math.Clamp(sensitivity, 1, 100));
            if (request.AdvancedMode is { } advanced) settings.SetBool(SettingsRepository.Keys.AdvancedMode, advanced);

            if (request.FfmpegPath is not null)
            {
                settings.Set(SettingsRepository.Keys.FfmpegPath, request.FfmpegPath.Trim());
                // Re-probe immediately so the response can tell the user whether the path works.
                var located = await ffmpegLocator.LocateAsync(request.FfmpegPath.Trim(), forceRefresh: true, cancellationToken);
                if (!located.Available)
                {
                    return Results.BadRequest(new { error = "No working ffmpeg was found at that path." });
                }
            }

            auth.Audit(http.CurrentUser(), "settings.update", address: http.ClientAddress());
            return Results.Ok(new { ok = true });
        })
        .RequireAdministrator()
        .WithName("UpdateSettings");

        group.MapPost("/test-path", (SettingsRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.RecordingsPath))
                return Results.BadRequest(new { error = "Enter a folder to test." });

            var (writable, error) = StorageManager.TestWritable(request.RecordingsPath.Trim());
            return Results.Ok(new { writable, error });
        })
        .RequireAdministrator()
        .WithName("TestStoragePath")
        .WithSummary("Checks a folder is writable by actually writing to it.");
    }

    /// <summary>
    /// Password rules kept deliberately minimal: length is what actually matters, and complexity
    /// rules mostly produce predictable substitutions.
    /// </summary>
    internal static string? ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return "Choose a password.";
        if (password.Length < 10) return "Use a password of at least 10 characters.";
        if (password.Length > 200) return "That password is too long.";

        var common = new[] { "password", "12345678", "visionmesh", "qwertyui", "admin123" };
        if (common.Any(c => password.Contains(c, StringComparison.OrdinalIgnoreCase)))
            return "That password is too easy to guess. Choose something less common.";

        return null;
    }
}

/// <summary>Version and platform strings, read once at startup.</summary>
public static class BuildInfo
{
    public static string Version { get; } =
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";

    public static string PlatformDescription { get; } =
        $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})";

    public static readonly DateTimeOffset StartedUtc = DateTimeOffset.UtcNow;
}

/// <summary>
/// Process and machine metrics measured over real sampling intervals.
///
/// CPU percent is null until two samples exist rather than reporting a meaningless first value -
/// a single snapshot of total processor time says nothing about current load.
/// </summary>
public sealed class SystemMetricsService
{
    private readonly object _gate = new();
    private TimeSpan _lastCpuTime;
    private DateTimeOffset _lastSample = DateTimeOffset.MinValue;
    private double? _lastPercent;

    public TimeSpan Uptime => DateTimeOffset.UtcNow - BuildInfo.StartedUtc;

    public double? GetCpuPercent()
    {
        lock (_gate)
        {
            var process = Process.GetCurrentProcess();
            var now = DateTimeOffset.UtcNow;
            var cpuTime = process.TotalProcessorTime;

            if (_lastSample == DateTimeOffset.MinValue)
            {
                _lastSample = now;
                _lastCpuTime = cpuTime;
                return null;
            }

            var elapsed = now - _lastSample;
            // Too short an interval turns scheduling jitter into a wild percentage.
            if (elapsed < TimeSpan.FromMilliseconds(500)) return _lastPercent;

            var used = cpuTime - _lastCpuTime;
            _lastSample = now;
            _lastCpuTime = cpuTime;

            var percent = used.TotalMilliseconds / (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100.0;
            _lastPercent = Math.Round(Math.Clamp(percent, 0, 100), 1);
            return _lastPercent;
        }
    }

    public long GetProcessMemoryBytes() => Process.GetCurrentProcess().WorkingSet64;

    public long? GetMachineTotalMemoryBytes()
    {
        try { return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes is > 0 and var total ? total : null; }
        catch (PlatformNotSupportedException) { return null; }
    }

    public long? GetMachineAvailableMemoryBytes()
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            if (info.TotalAvailableMemoryBytes <= 0) return null;
            return Math.Max(0, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes);
        }
        catch (PlatformNotSupportedException) { return null; }
    }
}
