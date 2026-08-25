using System.Text.Json.Serialization;
using VisionMesh.Core.Models;

namespace VisionMesh.Api;

/// <summary>
/// Shapes returned by the HTTP API.
///
/// These exist so entities are never serialised directly. That is not ceremony: the camera entity
/// carries an encrypted password and an internal config blob, and returning it whole would put
/// both in every dashboard response. Mapping explicitly means a new secret field cannot leak by
/// being added to a model.
/// </summary>
public sealed record CameraDto(
    string Id,
    string Name,
    string SourceKind,
    string? DeviceId,
    string? DeviceName,
    string? SourceId,
    string? GroupName,
    bool Enabled,
    string State,
    string RecordingMode,
    int RetentionDays,
    bool PrivacyMode,
    bool PtzSupported,
    int Width,
    int Height,
    int Fps,
    int Quality,
    DateTimeOffset CreatedUtc,
    double? FloorPlanX,
    double? FloorPlanY,
    CameraHealth? Health,
    CameraConnectionDto? Connection)
{
    public static CameraDto From(Camera camera, string? deviceName, CameraHealth? health, CameraConnectionDto? connection)
        => new(
            camera.Id,
            camera.Name,
            camera.SourceKind.ToString(),
            camera.DeviceId,
            deviceName,
            camera.SourceId,
            camera.GroupName,
            camera.Enabled,
            camera.State.ToString(),
            camera.RecordingMode.ToString(),
            camera.RetentionDays,
            camera.PrivacyMode,
            camera.PtzSupported,
            camera.DesiredWidth,
            camera.DesiredHeight,
            camera.DesiredFps,
            camera.DesiredQuality,
            camera.CreatedUtc,
            camera.FloorPlanX,
            camera.FloorPlanY,
            health,
            connection);
}

/// <summary>
/// Connection details for a network camera. The URL is redacted and the password is never
/// present in any form, encrypted or otherwise.
/// </summary>
public sealed record CameraConnectionDto(
    string? RtspUrl,
    string? Username,
    bool HasPassword,
    string Transport,
    string? Manufacturer,
    string? Model,
    string? OnvifProfileName,
    string? ScheduleDays,
    string? ScheduleStart,
    string? ScheduleEnd,
    int? MotionSensitivity)
{
    public static CameraConnectionDto From(CameraSourceConfig config)
        => new(
            UrlRedactor.Redact(config.RtspUrl),
            config.Username,
            !string.IsNullOrEmpty(config.PasswordEnc),
            config.Transport.ToString(),
            config.Manufacturer,
            config.Model,
            config.OnvifProfileName,
            config.ScheduleDays,
            config.ScheduleStart,
            config.ScheduleEnd,
            config.MotionSensitivity);
}

public sealed record DeviceDto(
    string Id,
    string Name,
    string Kind,
    string Platform,
    string AgentVersion,
    string State,
    bool Connected,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastSeenUtc,
    string? LastAddress,
    int CameraCount,
    int? BatteryPercent,
    bool? BatteryCharging,
    IReadOnlyList<CaptureDeviceInfo> AvailableCameras);

public sealed record UserDto(string Id, string Username, string Role, bool Disabled, DateTimeOffset CreatedUtc, DateTimeOffset? LastLoginUtc)
{
    public static UserDto From(User user) => new(user.Id, user.Username, user.Role.ToString(), user.Disabled, user.CreatedUtc, user.LastLoginUtc);
}

public sealed record EventDto(long Id, string? CameraId, string? CameraName, string? DeviceId, string Type, string Severity, DateTimeOffset TimestampUtc, string? Detail);

public sealed record RecordingDto(long Id, string CameraId, string? CameraName, DateTimeOffset StartUtc, DateTimeOffset? EndUtc, long SizeBytes, string Trigger, bool Closed, double? DurationSeconds)
{
    public static RecordingDto From(RecordingSegment segment, string? cameraName)
        => new(
            segment.Id,
            segment.CameraId,
            cameraName,
            segment.StartUtc,
            segment.EndUtc,
            segment.SizeBytes,
            segment.Trigger.ToString(),
            segment.Closed,
            segment.EndUtc is { } end ? (end - segment.StartUtc).TotalSeconds : null);
}

// ---- request bodies --------------------------------------------------------

public sealed class LoginRequest
{
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
}

/// <summary>Creates a camera. Which fields are required depends on <see cref="SourceKind"/>.</summary>
public sealed class CreateCameraRequest
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("sourceKind")] public string SourceKind { get; set; } = "";
    [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
    [JsonPropertyName("sourceId")] public string? SourceId { get; set; }
    [JsonPropertyName("groupName")] public string? GroupName { get; set; }
    [JsonPropertyName("rtspUrl")] public string? RtspUrl { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("transport")] public string? Transport { get; set; }
    [JsonPropertyName("onvifAddress")] public string? OnvifAddress { get; set; }
    [JsonPropertyName("onvifProfileToken")] public string? OnvifProfileToken { get; set; }
    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("height")] public int? Height { get; set; }
    [JsonPropertyName("fps")] public int? Fps { get; set; }
    [JsonPropertyName("quality")] public int? Quality { get; set; }
}

public sealed class UpdateCameraRequest
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("groupName")] public string? GroupName { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
    [JsonPropertyName("recordingMode")] public string? RecordingMode { get; set; }
    [JsonPropertyName("retentionDays")] public int? RetentionDays { get; set; }
    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("height")] public int? Height { get; set; }
    [JsonPropertyName("fps")] public int? Fps { get; set; }
    [JsonPropertyName("quality")] public int? Quality { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    /// <summary>Null leaves the stored password untouched; empty string clears it.</summary>
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("rtspUrl")] public string? RtspUrl { get; set; }
    [JsonPropertyName("transport")] public string? Transport { get; set; }
    [JsonPropertyName("scheduleDays")] public string? ScheduleDays { get; set; }
    [JsonPropertyName("scheduleStart")] public string? ScheduleStart { get; set; }
    [JsonPropertyName("scheduleEnd")] public string? ScheduleEnd { get; set; }
    [JsonPropertyName("motionSensitivity")] public int? MotionSensitivity { get; set; }
    [JsonPropertyName("floorPlanX")] public double? FloorPlanX { get; set; }
    [JsonPropertyName("floorPlanY")] public double? FloorPlanY { get; set; }
}

public sealed class PtzRequest
{
    [JsonPropertyName("pan")] public double Pan { get; set; }
    [JsonPropertyName("tilt")] public double Tilt { get; set; }
    [JsonPropertyName("zoom")] public double Zoom { get; set; }
    /// <summary>True to stop all movement, ignoring the velocity values.</summary>
    [JsonPropertyName("stop")] public bool Stop { get; set; }
}

public sealed class CreateUserRequest
{
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "Viewer";
}

public sealed class UpdateUserRequest
{
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("disabled")] public bool? Disabled { get; set; }
}

public sealed class SetupRequest
{
    [JsonPropertyName("serverName")] public string ServerName { get; set; } = "VisionMesh";
    [JsonPropertyName("adminUsername")] public string AdminUsername { get; set; } = "";
    [JsonPropertyName("adminPassword")] public string AdminPassword { get; set; } = "";
    [JsonPropertyName("recordingsPath")] public string? RecordingsPath { get; set; }
    [JsonPropertyName("retentionDays")] public int RetentionDays { get; set; } = 7;
}

public sealed class SettingsRequest
{
    [JsonPropertyName("serverName")] public string? ServerName { get; set; }
    [JsonPropertyName("recordingsPath")] public string? RecordingsPath { get; set; }
    [JsonPropertyName("retentionDays")] public int? RetentionDays { get; set; }
    [JsonPropertyName("storageLimitGb")] public int? StorageLimitGb { get; set; }
    [JsonPropertyName("motionSensitivity")] public int? MotionSensitivity { get; set; }
    [JsonPropertyName("ffmpegPath")] public string? FfmpegPath { get; set; }
    [JsonPropertyName("advancedMode")] public bool? AdvancedMode { get; set; }
}

public sealed class OnvifProbeRequest
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }
}
