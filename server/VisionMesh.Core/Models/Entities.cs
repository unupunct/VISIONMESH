namespace VisionMesh.Core.Models;

/// <summary>A machine or phone registered with the server. Identity is the Id, never the IP.</summary>
public sealed class Device
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DeviceKind Kind { get; set; }
    public string Platform { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    /// <summary>Last observed address. Informational only - never used as identity.</summary>
    public string? LastAddress { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }
    public DeviceState State { get; set; }
    /// <summary>PBKDF2 hash of the long-lived device token. The token itself is never stored.</summary>
    public string TokenHash { get; set; } = "";
    public string? BatteryJson { get; set; }
}

/// <summary>A camera as the user sees it in the dashboard.</summary>
public sealed class Camera
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public CameraSourceKind SourceKind { get; set; }
    /// <summary>Owning device for agent/phone cameras. Null for server-pulled RTSP/ONVIF.</summary>
    public string? DeviceId { get; set; }
    /// <summary>Stable identifier of the capture device within its host (e.g. \?\usb#... or /dev/video0).</summary>
    public string? SourceId { get; set; }
    public string? GroupName { get; set; }
    public bool Enabled { get; set; } = true;
    public CameraState State { get; set; }
    public RecordingMode RecordingMode { get; set; } = RecordingMode.Off;
    public int RetentionDays { get; set; } = 7;
    public bool PrivacyMode { get; set; }
    public bool AudioEnabled { get; set; }
    public bool PtzSupported { get; set; }
    public int DesiredWidth { get; set; } = 1280;
    public int DesiredHeight { get; set; } = 720;
    public int DesiredFps { get; set; } = 15;
    public int DesiredQuality { get; set; } = 75;
    public DateTimeOffset CreatedUtc { get; set; }
    /// <summary>Free-form per-source settings (RTSP url, ONVIF profile, etc). Secrets live in the credential store, not here.</summary>
    public string? ConfigJson { get; set; }
    public double? FloorPlanX { get; set; }
    public double? FloorPlanY { get; set; }
}

public sealed class User
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? LastLoginUtc { get; set; }
    public bool Disabled { get; set; }
}

public sealed class CameraEvent
{
    public long Id { get; set; }
    public string? CameraId { get; set; }
    public string? DeviceId { get; set; }
    public EventType Type { get; set; }
    public EventSeverity Severity { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public string? Detail { get; set; }
    public long? RecordingId { get; set; }
}

public sealed class RecordingSegment
{
    public long Id { get; set; }
    public string CameraId { get; set; } = "";
    public string FilePath { get; set; } = "";
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset? EndUtc { get; set; }
    public long SizeBytes { get; set; }
    public RecordingTrigger Trigger { get; set; }
    public bool Closed { get; set; }
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public string? UserId { get; set; }
    public string? Username { get; set; }
    public string Action { get; set; } = "";
    public string? Target { get; set; }
    public string? Address { get; set; }
    public string? Detail { get; set; }
}

/// <summary>Short-lived token used to pair a new device (phone or agent) with the server.</summary>
public sealed class PairingToken
{
    public string Code { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public bool Used { get; set; }
    public string? IssuedByUserId { get; set; }
    public string? ConsumedByDeviceId { get; set; }
}
