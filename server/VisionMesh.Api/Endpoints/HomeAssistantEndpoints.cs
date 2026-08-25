using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using VisionMesh.Api.Auth;
using VisionMesh.Core.Util;
using VisionMesh.Database.Repositories;
using VisionMesh.HomeAssistant;

namespace VisionMesh.Api.Endpoints;

/// <summary>Home Assistant connection settings, connection testing and MQTT discovery.</summary>
public static class HomeAssistantEndpoints
{
    public static void MapHomeAssistantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/homeassistant").WithTags("Home Assistant");

        group.MapGet("/", (SettingsRepository settings, MqttDiscoveryService mqtt, CameraRepository cameras, NetworkInfoService network) =>
        {
            var url = settings.Get(SettingsRepository.Keys.HomeAssistantUrl);
            var cameraCount = cameras.GetAll().Count;

            return Results.Ok(new
            {
                enabled = settings.GetBool(SettingsRepository.Keys.HomeAssistantEnabled, false),
                url,
                hasToken = !string.IsNullOrEmpty(settings.Get(SettingsRepository.Keys.HomeAssistantTokenEnc)),
                mqtt = new
                {
                    enabled = settings.GetBool(SettingsRepository.Keys.MqttEnabled, false),
                    host = settings.Get(SettingsRepository.Keys.MqttHost),
                    port = settings.GetInt(SettingsRepository.Keys.MqttPort, 1883),
                    username = settings.Get(SettingsRepository.Keys.MqttUsername),
                    hasPassword = !string.IsNullOrEmpty(settings.Get(SettingsRepository.Keys.MqttPasswordEnc)),
                    discoveryPrefix = settings.GetString(SettingsRepository.Keys.MqttDiscoveryPrefix, "homeassistant"),
                    connected = mqtt.Connected,
                    lastError = mqtt.LastError,
                    publishedEntities = mqtt.PublishedEntities,
                },
                cameraCount,
                // What the user needs to type into the Home Assistant integration's config flow.
                integration = new
                {
                    serverUrl = network.GetDashboardUrls().FirstOrDefault(),
                    instructions = "In Home Assistant, add the VisionMesh integration and enter this server address plus a VisionMesh username and password.",
                },
            });
        })
        .RequireAdministrator()
        .WithName("GetHomeAssistantSettings");

        group.MapPut("/", (
            HttpContext http,
            HomeAssistantSettingsRequest request,
            SettingsRepository settings,
            SecretProtector secrets,
            AuthService auth) =>
        {
            if (request.Enabled is { } enabled) settings.SetBool(SettingsRepository.Keys.HomeAssistantEnabled, enabled);
            if (request.Url is not null) settings.Set(SettingsRepository.Keys.HomeAssistantUrl, request.Url.Trim().TrimEnd('/'));

            // Null means "keep the stored token"; empty string means "forget it".
            if (request.Token is not null)
            {
                if (request.Token.Length == 0) settings.Delete(SettingsRepository.Keys.HomeAssistantTokenEnc);
                else settings.Set(SettingsRepository.Keys.HomeAssistantTokenEnc, secrets.Protect(request.Token));
            }

            if (request.MqttEnabled is { } mqttEnabled) settings.SetBool(SettingsRepository.Keys.MqttEnabled, mqttEnabled);
            if (request.MqttHost is not null) settings.Set(SettingsRepository.Keys.MqttHost, request.MqttHost.Trim());
            if (request.MqttPort is { } port) settings.SetInt(SettingsRepository.Keys.MqttPort, Math.Clamp(port, 1, 65535));
            if (request.MqttUsername is not null) settings.Set(SettingsRepository.Keys.MqttUsername, request.MqttUsername.Trim());
            if (request.MqttPassword is not null)
            {
                if (request.MqttPassword.Length == 0) settings.Delete(SettingsRepository.Keys.MqttPasswordEnc);
                else settings.Set(SettingsRepository.Keys.MqttPasswordEnc, secrets.Protect(request.MqttPassword));
            }
            if (!string.IsNullOrWhiteSpace(request.MqttDiscoveryPrefix))
                settings.Set(SettingsRepository.Keys.MqttDiscoveryPrefix, request.MqttDiscoveryPrefix.Trim());

            auth.Audit(http.CurrentUser(), "homeassistant.settings", address: http.ClientAddress());
            return Results.Ok(new { ok = true });
        })
        .RequireAdministrator()
        .WithName("UpdateHomeAssistantSettings");

        group.MapPost("/test", async (
            HomeAssistantTestRequest request,
            SettingsRepository settings,
            SecretProtector secrets,
            HomeAssistantClient client,
            CancellationToken cancellationToken) =>
        {
            // Test what the user just typed if they supplied it, otherwise what is stored, so the
            // button works both before and after saving.
            var url = string.IsNullOrWhiteSpace(request.Url) ? settings.Get(SettingsRepository.Keys.HomeAssistantUrl) : request.Url;
            var token = string.IsNullOrWhiteSpace(request.Token)
                ? secrets.Unprotect(settings.Get(SettingsRepository.Keys.HomeAssistantTokenEnc))
                : request.Token;

            var status = await client.TestConnectionAsync(url ?? "", token ?? "", cancellationToken);
            return Results.Ok(status);
        })
        .RequireAdministrator()
        .WithName("TestHomeAssistantConnection");

        // Consumed by the Home Assistant custom integration to enumerate cameras in one call.
        group.MapGet("/entities", (CameraService service, NetworkInfoService network) =>
        {
            var baseUrl = network.GetDashboardUrls().FirstOrDefault() ?? "";
            return Results.Ok(service.GetAll().Select(camera => new
            {
                camera.Id,
                camera.Name,
                // Unique ID is the VisionMesh camera id, which is stable for the camera's whole
                // life. Anything derived from an IP address would break on a DHCP lease change.
                uniqueId = $"visionmesh_{camera.Id}",
                camera.SourceKind,
                camera.State,
                camera.PtzSupported,
                camera.GroupName,
                streamUrl = $"{baseUrl}/api/cameras/{camera.Id}/stream.mjpeg",
                snapshotUrl = $"{baseUrl}/api/cameras/{camera.Id}/snapshot.jpg",
                supports = new
                {
                    snapshot = true,
                    stream = true,
                    ptz = camera.PtzSupported,
                    privacy = true,
                    recording = true,
                    motion = camera.RecordingMode == "Motion",
                },
                health = camera.Health,
            }));
        })
        .RequireViewer()
        .WithName("ListHomeAssistantEntities")
        .WithSummary("Camera list in the shape the Home Assistant integration consumes.");
    }
}

public sealed class HomeAssistantSettingsRequest
{
    public bool? Enabled { get; set; }
    public string? Url { get; set; }
    public string? Token { get; set; }
    public bool? MqttEnabled { get; set; }
    public string? MqttHost { get; set; }
    public int? MqttPort { get; set; }
    public string? MqttUsername { get; set; }
    public string? MqttPassword { get; set; }
    public string? MqttDiscoveryPrefix { get; set; }
}

public sealed class HomeAssistantTestRequest
{
    public string? Url { get; set; }
    public string? Token { get; set; }
}
