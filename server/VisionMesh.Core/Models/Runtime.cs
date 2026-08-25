namespace VisionMesh.Core.Models;

/// <summary>
/// Live, measured health of one camera. Every value here is derived from real observed
/// frames or connection state - nothing is estimated or synthesised.
/// </summary>
public sealed class CameraHealth
{
    public string CameraId { get; set; } = "";
    public CameraState State { get; set; } = CameraState.Offline;
    /// <summary>Frames per second measured over the last sliding window. Null until enough frames observed.</summary>
    public double? Fps { get; set; }
    /// <summary>Measured payload bitrate in bits per second. Null until enough frames observed.</summary>
    public double? BitrateBps { get; set; }
    /// <summary>Agent-to-server one way delay in ms, when the agent timestamps frames and clocks are comparable.</summary>
    public double? LatencyMs { get; set; }
    /// <summary>Frames the source reported as dropped, when the source reports it.</summary>
    public long? DroppedFrames { get; set; }
    public long FramesReceived { get; set; }
    public long BytesReceived { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTimeOffset? LastFrameUtc { get; set; }
    public DateTimeOffset? LastHeartbeatUtc { get; set; }
    public bool Recording { get; set; }
    public int ViewerCount { get; set; }
    public string? LastError { get; set; }
    /// <summary>Battery percent for phone cameras that report it. Null when not applicable.</summary>
    public int? BatteryPercent { get; set; }
    public bool? BatteryCharging { get; set; }
    /// <summary>Link quality description reported by a mobile camera, when available.</summary>
    public string? NetworkQuality { get; set; }
}

/// <summary>A capture device advertised by an agent (not yet necessarily added as a camera).</summary>
public sealed class CaptureDeviceInfo
{
    /// <summary>Stable per-host identifier, e.g. the DirectShow device path or /dev/video0.</summary>
    public string SourceId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Human readable bus/driver description, when the platform provides one.</summary>
    public string? Description { get; set; }
    public List<CaptureFormatInfo> Formats { get; set; } = new();
    /// <summary>False when the OS reports the device present but it cannot be opened (in use, no permission).</summary>
    public bool Available { get; set; } = true;
    public string? Unavailable { get; set; }
}

public sealed class CaptureFormatInfo
{
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    /// <summary>FourCC or media subtype name as reported by the OS, e.g. MJPG, YUY2, NV12.</summary>
    public string Format { get; set; } = "";
    /// <summary>True when frames arrive already JPEG encoded and need no re-encoding.</summary>
    public bool NativeJpeg { get; set; }
}

public sealed record StorageInfo
{
    public string Path { get; set; } = "";
    public bool Exists { get; set; }
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public long UsedByRecordingsBytes { get; set; }
    public int RetentionDays { get; set; }
    public string? Error { get; set; }
}

public sealed class SystemHealth
{
    public string ServerName { get; set; } = "";
    public string Version { get; set; } = "";
    public string Platform { get; set; } = "";
    public TimeSpan Uptime { get; set; }
    /// <summary>Process CPU percent measured across a real sampling interval. Null before the first sample completes.</summary>
    public double? CpuPercent { get; set; }
    public long ProcessMemoryBytes { get; set; }
    public long? MachineTotalMemoryBytes { get; set; }
    public long? MachineAvailableMemoryBytes { get; set; }
    public int CameraCount { get; set; }
    public int CamerasOnline { get; set; }
    public int CamerasRecording { get; set; }
    public int DeviceCount { get; set; }
    public int DevicesOnline { get; set; }
    public StorageInfo? Storage { get; set; }
    /// <summary>True when an ffmpeg binary was located; features that need it are disabled otherwise.</summary>
    public bool FfmpegAvailable { get; set; }
    public string? FfmpegPath { get; set; }
    public string? FfmpegVersion { get; set; }
}
