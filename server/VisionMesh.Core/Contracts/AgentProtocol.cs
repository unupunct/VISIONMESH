using System.Text.Json.Serialization;
using VisionMesh.Core.Models;

namespace VisionMesh.Core.Contracts;

/// <summary>
/// Wire contract between a VisionMesh agent (Windows/Linux/phone) and the server.
///
/// Transport: one WebSocket at /agent/ws authenticated with the device token.
///  - Text messages carry JSON envelopes (<see cref="AgentMessage"/> / <see cref="ServerMessage"/>).
///  - Binary messages carry video frames prefixed with <see cref="FrameHeader"/>.
///
/// The split matters: control is small and infrequent, frames are large and constant,
/// so frames never pay JSON or base64 overhead.
/// </summary>
public static class AgentProtocol
{
    public const string WebSocketPath = "/agent/ws";
    public const int Version = 1;
}

public static class AgentMessageType
{
    public const string Hello = "hello";
    public const string Devices = "devices";
    public const string Telemetry = "telemetry";
    public const string CaptureStarted = "capture-started";
    public const string CaptureStopped = "capture-stopped";
    public const string CaptureError = "capture-error";
    public const string Pong = "pong";
}

public static class ServerMessageType
{
    public const string Welcome = "welcome";
    public const string StartCapture = "start-capture";
    public const string StopCapture = "stop-capture";
    public const string ListDevices = "list-devices";
    public const string Ping = "ping";
    public const string Error = "error";
}

/// <summary>Envelope sent agent -> server.</summary>
public sealed class AgentMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("hello")] public AgentHello? Hello { get; set; }
    [JsonPropertyName("devices")] public List<CaptureDeviceInfo>? Devices { get; set; }
    [JsonPropertyName("telemetry")] public AgentTelemetry? Telemetry { get; set; }
    [JsonPropertyName("slot")] public ushort? Slot { get; set; }
    [JsonPropertyName("cameraId")] public string? CameraId { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

/// <summary>Envelope sent server -> agent.</summary>
public sealed class ServerMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("welcome")] public ServerWelcome? Welcome { get; set; }
    [JsonPropertyName("start")] public StartCaptureCommand? Start { get; set; }
    [JsonPropertyName("slot")] public ushort? Slot { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class AgentHello
{
    [JsonPropertyName("protocol")] public int Protocol { get; set; } = AgentProtocol.Version;
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("kind")] public DeviceKind Kind { get; set; }
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("devices")] public List<CaptureDeviceInfo> Devices { get; set; } = new();
}

public sealed class ServerWelcome
{
    [JsonPropertyName("protocol")] public int Protocol { get; set; } = AgentProtocol.Version;
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("serverName")] public string ServerName { get; set; } = "";
    [JsonPropertyName("serverVersion")] public string ServerVersion { get; set; } = "";
    /// <summary>Seconds between server pings; the agent should treat 3 missed pings as a dead link.</summary>
    [JsonPropertyName("pingSeconds")] public int PingSeconds { get; set; } = 15;
}

/// <summary>
/// Tells an agent to begin capturing one capture device and to tag its frames with <see cref="Slot"/>.
/// Slots exist so the binary frame header stays fixed-size instead of carrying a string camera id.
/// </summary>
public sealed class StartCaptureCommand
{
    [JsonPropertyName("slot")] public ushort Slot { get; set; }
    [JsonPropertyName("cameraId")] public string CameraId { get; set; } = "";
    [JsonPropertyName("sourceId")] public string SourceId { get; set; } = "";
    [JsonPropertyName("width")] public int Width { get; set; } = 1280;
    [JsonPropertyName("height")] public int Height { get; set; } = 720;
    [JsonPropertyName("fps")] public int Fps { get; set; } = 15;
    /// <summary>JPEG quality 1-100, used only when the source does not already emit JPEG.</summary>
    [JsonPropertyName("quality")] public int Quality { get; set; } = 75;
}

public sealed class AgentTelemetry
{
    [JsonPropertyName("cameras")] public List<CameraTelemetry> Cameras { get; set; } = new();
    [JsonPropertyName("batteryPercent")] public int? BatteryPercent { get; set; }
    [JsonPropertyName("batteryCharging")] public bool? BatteryCharging { get; set; }
    [JsonPropertyName("networkQuality")] public string? NetworkQuality { get; set; }
}

public sealed class CameraTelemetry
{
    [JsonPropertyName("slot")] public ushort Slot { get; set; }
    [JsonPropertyName("fps")] public double? Fps { get; set; }
    [JsonPropertyName("droppedFrames")] public long? DroppedFrames { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}
