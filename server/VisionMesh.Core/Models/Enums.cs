namespace VisionMesh.Core.Models;

/// <summary>How a camera's video reaches VisionMesh.</summary>
public enum CameraSourceKind
{
    /// <summary>USB / integrated camera exposed by a VisionMesh agent on a computer.</summary>
    AgentCamera = 0,
    /// <summary>Android phone running the VisionMesh mobile app in camera mode.</summary>
    AndroidPhone = 1,
    /// <summary>iPhone / iPad running the VisionMesh mobile app in camera mode.</summary>
    IosPhone = 2,
    /// <summary>Plain RTSP URL entered manually.</summary>
    Rtsp = 3,
    /// <summary>Camera discovered and configured over ONVIF.</summary>
    Onvif = 4,
}

/// <summary>Operational state of a camera as shown in the dashboard.</summary>
public enum CameraState
{
    Offline = 0,
    Online = 1,
    Degraded = 2,
    Paused = 3,
    Privacy = 4,
}

/// <summary>What kind of machine a registered device is.</summary>
public enum DeviceKind
{
    WindowsAgent = 0,
    LinuxAgent = 1,
    AndroidApp = 2,
    IosApp = 3,
    /// <summary>Cameras owned directly by the server (RTSP/ONVIF pullers).</summary>
    ServerLocal = 4,
}

public enum DeviceState
{
    Offline = 0,
    Online = 1,
}

public enum UserRole
{
    Viewer = 0,
    Operator = 1,
    Administrator = 2,
}

/// <summary>What causes a camera to record.</summary>
public enum RecordingMode
{
    Off = 0,
    Continuous = 1,
    Motion = 2,
    Scheduled = 3,
    Manual = 4,
}

public enum RecordingTrigger
{
    Manual = 0,
    Continuous = 1,
    Motion = 2,
    Schedule = 3,
    Event = 4,
}

public enum EventType
{
    Motion = 0,
    CameraOnline = 1,
    CameraOffline = 2,
    CameraDegraded = 3,
    RecordingStarted = 4,
    RecordingStopped = 5,
    DeviceConnected = 6,
    DeviceDisconnected = 7,
    StorageWarning = 8,
    AuthFailure = 9,
    PrivacyEnabled = 10,
    PrivacyDisabled = 11,
    SystemStarted = 12,
}

public enum EventSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>Transport preference for pulled RTSP streams.</summary>
public enum RtspTransport
{
    Auto = 0,
    Tcp = 1,
    Udp = 2,
}
