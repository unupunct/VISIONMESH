using VisionMesh.Core.Models;
using VisionMesh.Core.Util;
using VisionMesh.Database.Repositories;
using VisionMesh.Recording;
using VisionMesh.Streaming.Fanout;
using VisionMesh.Streaming.Ingest;

namespace VisionMesh.Api;

/// <summary>
/// Assembles the camera view the API returns: the stored record, the live measurements, and the
/// connection details with every secret stripped out.
///
/// Centralised so that "what a camera looks like to a client" is defined exactly once, whether
/// the caller is the dashboard, the mobile app or the Home Assistant integration.
/// </summary>
public sealed class CameraService(
    CameraRepository cameras,
    DeviceRepository devices,
    CameraRuntimeRegistry runtimes,
    AgentRegistry agents,
    FrameBus frameBus,
    RecordingEngine recordingEngine)
{
    public List<CameraDto> GetAll()
    {
        var deviceNames = devices.GetAll().ToDictionary(d => d.Id, d => d.Name, StringComparer.Ordinal);
        return cameras.GetAll()
            .Select(camera => Build(camera, camera.DeviceId is null ? null : deviceNames.GetValueOrDefault(camera.DeviceId)))
            .ToList();
    }

    public CameraDto? GetById(string id)
    {
        var camera = cameras.GetById(id);
        if (camera is null) return null;
        var deviceName = camera.DeviceId is null ? null : devices.GetById(camera.DeviceId)?.Name;
        return Build(camera, deviceName);
    }

    public CameraDto Build(Camera camera, string? deviceName)
    {
        var runtime = runtimes.Find(camera.Id);
        var health = runtime?.ToHealth(frameBus.GetSubscriberCount(camera.Id));

        if (health is not null)
        {
            // The stored state is authoritative for user intent (paused, privacy); the runtime is
            // authoritative for everything else.
            health.State = camera.PrivacyMode ? CameraState.Privacy
                         : camera.State == CameraState.Paused ? CameraState.Paused
                         : health.State;
            health.Recording = recordingEngine.IsRecording(camera.Id);
        }

        var config = CameraSourceConfig.FromJson(camera.ConfigJson);
        var connection = camera.SourceKind is CameraSourceKind.Rtsp or CameraSourceKind.Onvif
            ? CameraConnectionDto.From(config)
            : new CameraConnectionDto(null, null, false, config.Transport.ToString(), null, null, null,
                                      config.ScheduleDays, config.ScheduleStart, config.ScheduleEnd, config.MotionSensitivity);

        return CameraDto.From(camera, deviceName, health, connection);
    }

    /// <summary>Whether a camera's owning device is currently connected. Always true for server-pulled cameras.</summary>
    public bool IsSourceReachable(Camera camera)
        => camera.SourceKind switch
        {
            CameraSourceKind.Rtsp or CameraSourceKind.Onvif => true,
            _ => camera.DeviceId is not null && agents.IsOnline(camera.DeviceId),
        };

    public List<DeviceDto> GetDevices()
    {
        var cameraCounts = cameras.GetAll()
            .Where(c => c.DeviceId is not null)
            .GroupBy(c => c.DeviceId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return devices.GetAll().Select(device =>
        {
            var connection = agents.Find(device.Id);
            return new DeviceDto(
                device.Id,
                connection?.DeviceName ?? device.Name,
                device.Kind.ToString(),
                connection?.Platform is { Length: > 0 } platform ? platform : device.Platform,
                connection?.AgentVersion is { Length: > 0 } version ? version : device.AgentVersion,
                connection is not null ? DeviceState.Online.ToString() : DeviceState.Offline.ToString(),
                connection is not null,
                device.CreatedUtc,
                connection is not null ? DateTimeOffset.UtcNow : device.LastSeenUtc,
                device.LastAddress,
                cameraCounts.GetValueOrDefault(device.Id, 0),
                connection?.BatteryPercent,
                connection?.BatteryCharging,
                connection?.CaptureDevices ?? Array.Empty<CaptureDeviceInfo>());
        }).ToList();
    }

    /// <summary>
    /// Capture devices on a machine that are not yet added as cameras.
    /// This is what the Add Camera flow shows, so a device already in use does not appear twice.
    /// </summary>
    public List<CaptureDeviceInfo> GetUnusedCaptureDevices(string deviceId)
    {
        var connection = agents.Find(deviceId);
        if (connection is null) return new List<CaptureDeviceInfo>();

        var used = new HashSet<string>(
            cameras.GetByDevice(deviceId).Select(c => c.SourceId ?? ""),
            StringComparer.OrdinalIgnoreCase);

        return connection.CaptureDevices.Where(d => !used.Contains(d.SourceId)).ToList();
    }

    /// <summary>Generates a unique camera name, so adding three webcams does not produce three "Camera".</summary>
    public string MakeUniqueName(string requested)
    {
        var baseName = string.IsNullOrWhiteSpace(requested) ? "Camera" : requested.Trim();
        var existing = new HashSet<string>(cameras.GetAll().Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseName)) return baseName;

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = $"{baseName} {suffix}";
            if (!existing.Contains(candidate)) return candidate;
        }
        return $"{baseName} {Ids.NewId("")[..4]}";
    }
}
