using VisionMesh.Core.Models;

namespace VisionMesh.Core.Abstractions;

/// <summary>
/// Pushes state changes to connected dashboards.
///
/// Declared here so background services can announce changes without depending on the web layer.
/// Implementations must never block the caller: a slow or dead browser connection cannot be
/// allowed to stall camera supervision or recording.
/// </summary>
public interface IRealtimeNotifier
{
    void CameraStateChanged(string cameraId, CameraState state);
    void CameraHealthChanged(CameraHealth health);
    void CameraAdded(Camera camera);
    void CameraRemoved(string cameraId);
    void DeviceStateChanged(string deviceId, DeviceState state);
    void EventRaised(CameraEvent cameraEvent);
    void RecordingChanged(string cameraId, bool recording);
    void StorageWarning(string message, long freeBytes);
    void SystemChanged();
}

/// <summary>No-op notifier for tests and for background jobs that run with no dashboard attached.</summary>
public sealed class NullRealtimeNotifier : IRealtimeNotifier
{
    public static readonly NullRealtimeNotifier Instance = new();
    public void CameraStateChanged(string cameraId, CameraState state) { }
    public void CameraHealthChanged(CameraHealth health) { }
    public void CameraAdded(Camera camera) { }
    public void CameraRemoved(string cameraId) { }
    public void DeviceStateChanged(string deviceId, DeviceState state) { }
    public void EventRaised(CameraEvent cameraEvent) { }
    public void RecordingChanged(string cameraId, bool recording) { }
    public void StorageWarning(string message, long freeBytes) { }
    public void SystemChanged() { }
}
