using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using VisionMesh.Core.Models;
using VisionMesh.Core.Util;
using VisionMesh.Database.Repositories;
using VisionMesh.Streaming.Fanout;

namespace VisionMesh.HomeAssistant;

/// <summary>
/// Publishes VisionMesh state to Home Assistant using MQTT discovery.
///
/// Scope is deliberate: state, availability and simple controls travel over MQTT, and live video
/// does not. MQTT can technically carry JPEG frames to an HA camera entity, but doing that turns
/// every frame into a broker round trip and makes the broker the bottleneck for the whole system.
/// Video is served over HTTP by the custom integration instead.
///
/// Entity IDs are built from the VisionMesh camera id, which never changes, so renaming a camera
/// or changing its IP address does not orphan the entity in Home Assistant.
/// </summary>
public sealed class MqttDiscoveryService(
    SettingsRepository settings,
    CameraRepository cameras,
    CameraRuntimeRegistry runtimes,
    SecretProtector secrets,
    ILogger<MqttDiscoveryService> log) : BackgroundService
{
    private static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private IMqttClient? _client;
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    private string _discoveryPrefix = "homeassistant";
    private string _nodeId = "visionmesh";

    /// <summary>Current connection state, surfaced on the Home Assistant settings page.</summary>
    public bool Connected => _client?.IsConnected ?? false;
    public string? LastError { get; private set; }
    public int PublishedEntities { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!settings.GetBool(SettingsRepository.Keys.MqttEnabled, false))
            {
                await DisconnectAsync().ConfigureAwait(false);
                await DelayAsync(ReconnectDelay, stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                if (!Connected)
                {
                    await ConnectAsync(stoppingToken).ConfigureAwait(false);
                    // Discovery must be re-sent after a reconnect: the broker may have restarted
                    // and dropped the retained configuration topics.
                    _announced.Clear();
                }

                if (Connected) await PublishAllAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                log.LogWarning("MQTT publishing failed: {Error}", ex.Message);
                await DisconnectAsync().ConfigureAwait(false);
                await DelayAsync(ReconnectDelay, stoppingToken).ConfigureAwait(false);
                continue;
            }

            await DelayAsync(PublishInterval, stoppingToken).ConfigureAwait(false);
        }

        await DisconnectAsync().ConfigureAwait(false);
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var host = settings.Get(SettingsRepository.Keys.MqttHost);
        if (string.IsNullOrWhiteSpace(host))
        {
            LastError = "No MQTT broker address is configured.";
            return;
        }

        _discoveryPrefix = settings.GetString(SettingsRepository.Keys.MqttDiscoveryPrefix, "homeassistant");
        var port = settings.GetInt(SettingsRepository.Keys.MqttPort, 1883);
        var username = settings.Get(SettingsRepository.Keys.MqttUsername);
        var password = secrets.Unprotect(settings.Get(SettingsRepository.Keys.MqttPasswordEnc));

        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId($"visionmesh-{Environment.MachineName}".ToLowerInvariant())
            .WithCleanSession()
            // The broker publishes this if we vanish, so Home Assistant shows entities as
            // unavailable rather than freezing on their last known value.
            .WithWillTopic(AvailabilityTopic)
            .WithWillPayload("offline"u8.ToArray())
            .WithWillRetain()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30));

        if (!string.IsNullOrEmpty(username)) builder = builder.WithCredentials(username, password ?? "");

        var result = await _client.ConnectAsync(builder.Build(), cancellationToken).ConfigureAwait(false);

        if (result.ResultCode != MqttClientConnectResultCode.Success)
        {
            LastError = $"The broker refused the connection: {result.ResultCode}.";
            log.LogWarning("MQTT connection refused: {Code}", result.ResultCode);
            return;
        }

        LastError = null;
        log.LogInformation("Connected to the MQTT broker at {Host}:{Port}.", host, port);

        _client.ApplicationMessageReceivedAsync += OnCommandReceivedAsync;
        await _client.SubscribeAsync($"{_nodeId}/+/set", MqttQualityOfServiceLevel.AtLeastOnce, cancellationToken).ConfigureAwait(false);
        await PublishAsync(AvailabilityTopic, "online", retain: true, cancellationToken).ConfigureAwait(false);
    }

    private string AvailabilityTopic => $"{_nodeId}/status";

    private async Task PublishAllAsync(CancellationToken cancellationToken)
    {
        var all = cameras.GetAll();
        var entityCount = 0;

        foreach (var camera in all)
        {
            var runtime = runtimes.Find(camera.Id);
            var health = runtime?.ToHealth(0);
            var objectId = ObjectId(camera.Id);

            if (_announced.Add(camera.Id))
            {
                await PublishCameraDiscoveryAsync(camera, objectId, cancellationToken).ConfigureAwait(false);
            }

            var state = camera.PrivacyMode ? "privacy" : camera.State.ToString().ToLowerInvariant();

            // One retained JSON state topic per camera; every entity reads a field from it via a
            // value template. That keeps the message count flat as cameras are added.
            var payload = JsonSerializer.Serialize(new
            {
                state,
                online = camera.State == CameraState.Online,
                recording = health?.Recording ?? false,
                privacy = camera.PrivacyMode,
                fps = health?.Fps,
                bitrate_kbps = health?.BitrateBps is { } bps ? Math.Round(bps / 1000.0, 1) : (double?)null,
                latency_ms = health?.LatencyMs,
                resolution = health is { Width: > 0 } ? $"{health.Width}x{health.Height}" : null,
                last_frame = health?.LastFrameUtc,
                battery = health?.BatteryPercent,
            }, Json);

            await PublishAsync($"{_nodeId}/{objectId}/state", payload, retain: true, cancellationToken).ConfigureAwait(false);
            entityCount += 6;
        }

        // Remove entities for cameras that no longer exist, so Home Assistant does not keep
        // showing an entity for a camera the user deleted.
        foreach (var stale in _announced.Where(id => all.All(c => c.Id != id)).ToArray())
        {
            await RemoveCameraDiscoveryAsync(stale, cancellationToken).ConfigureAwait(false);
            _announced.Remove(stale);
        }

        await PublishServerStateAsync(all, cancellationToken).ConfigureAwait(false);
        PublishedEntities = entityCount + 2;
    }

    private async Task PublishCameraDiscoveryAsync(Camera camera, string objectId, CancellationToken cancellationToken)
    {
        var device = new
        {
            identifiers = new[] { $"visionmesh_{camera.Id}" },
            name = camera.Name,
            manufacturer = "VisionMesh",
            model = camera.SourceKind.ToString(),
            via_device = "visionmesh_server",
        };

        var availability = new[] { new { topic = AvailabilityTopic } };
        var stateTopic = $"{_nodeId}/{objectId}/state";

        // binary_sensor: is the camera online
        await PublishDiscoveryAsync("binary_sensor", $"{objectId}_online", new
        {
            name = "Online",
            unique_id = $"visionmesh_{camera.Id}_online",
            state_topic = stateTopic,
            value_template = "{{ 'ON' if value_json.online else 'OFF' }}",
            device_class = "connectivity",
            availability,
            device,
        }, cancellationToken).ConfigureAwait(false);

        // binary_sensor: recording
        await PublishDiscoveryAsync("binary_sensor", $"{objectId}_recording", new
        {
            name = "Recording",
            unique_id = $"visionmesh_{camera.Id}_recording",
            state_topic = stateTopic,
            value_template = "{{ 'ON' if value_json.recording else 'OFF' }}",
            icon = "mdi:record-rec",
            availability,
            device,
        }, cancellationToken).ConfigureAwait(false);

        await PublishDiscoveryAsync("sensor", $"{objectId}_fps", new
        {
            name = "Frame rate",
            unique_id = $"visionmesh_{camera.Id}_fps",
            state_topic = stateTopic,
            value_template = "{{ value_json.fps }}",
            unit_of_measurement = "fps",
            state_class = "measurement",
            icon = "mdi:speedometer",
            availability,
            device,
        }, cancellationToken).ConfigureAwait(false);

        await PublishDiscoveryAsync("sensor", $"{objectId}_bitrate", new
        {
            name = "Bitrate",
            unique_id = $"visionmesh_{camera.Id}_bitrate",
            state_topic = stateTopic,
            value_template = "{{ value_json.bitrate_kbps }}",
            unit_of_measurement = "kbit/s",
            state_class = "measurement",
            icon = "mdi:transmission-tower",
            availability,
            device,
        }, cancellationToken).ConfigureAwait(false);

        await PublishDiscoveryAsync("sensor", $"{objectId}_state", new
        {
            name = "State",
            unique_id = $"visionmesh_{camera.Id}_state",
            state_topic = stateTopic,
            value_template = "{{ value_json.state }}",
            icon = "mdi:cctv",
            availability,
            device,
        }, cancellationToken).ConfigureAwait(false);

        // switch: privacy mode, the one control worth exposing over MQTT
        await PublishDiscoveryAsync("switch", $"{objectId}_privacy", new
        {
            name = "Privacy mode",
            unique_id = $"visionmesh_{camera.Id}_privacy",
            state_topic = stateTopic,
            value_template = "{{ 'ON' if value_json.privacy else 'OFF' }}",
            command_topic = $"{_nodeId}/{objectId}/set",
            payload_on = "privacy_on",
            payload_off = "privacy_off",
            icon = "mdi:eye-off",
            availability,
            device,
        }, cancellationToken).ConfigureAwait(false);

        log.LogInformation("Announced camera {Camera} to Home Assistant over MQTT.", camera.Name);
    }

    private async Task PublishServerStateAsync(List<Camera> all, CancellationToken cancellationToken)
    {
        const string objectId = "server";
        var stateTopic = $"{_nodeId}/{objectId}/state";

        if (_announced.Add("__server__"))
        {
            var device = new
            {
                identifiers = new[] { "visionmesh_server" },
                name = settings.GetString(SettingsRepository.Keys.ServerName, "VisionMesh"),
                manufacturer = "VisionMesh",
                model = "Server",
            };
            var availability = new[] { new { topic = AvailabilityTopic } };

            await PublishDiscoveryAsync("sensor", "cameras_online", new
            {
                name = "Cameras online",
                unique_id = "visionmesh_cameras_online",
                state_topic = stateTopic,
                value_template = "{{ value_json.cameras_online }}",
                state_class = "measurement",
                icon = "mdi:cctv",
                availability,
                device,
            }, cancellationToken).ConfigureAwait(false);

            await PublishDiscoveryAsync("sensor", "cameras_total", new
            {
                name = "Cameras total",
                unique_id = "visionmesh_cameras_total",
                state_topic = stateTopic,
                value_template = "{{ value_json.cameras_total }}",
                state_class = "measurement",
                icon = "mdi:cctv",
                availability,
                device,
            }, cancellationToken).ConfigureAwait(false);
        }

        var payload = JsonSerializer.Serialize(new
        {
            cameras_total = all.Count,
            cameras_online = all.Count(c => c.State == CameraState.Online),
            cameras_recording = all.Count(c => runtimes.Find(c.Id)?.Recording == true),
        }, Json);

        await PublishAsync(stateTopic, payload, retain: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveCameraDiscoveryAsync(string cameraId, CancellationToken cancellationToken)
    {
        var objectId = ObjectId(cameraId);
        // An empty retained payload on a discovery topic is how Home Assistant is told to forget
        // an entity. Anything else would leave a permanently unavailable entity behind.
        foreach (var (component, suffix) in new[]
                 {
                     ("binary_sensor", "online"), ("binary_sensor", "recording"),
                     ("sensor", "fps"), ("sensor", "bitrate"), ("sensor", "state"),
                     ("switch", "privacy"),
                 })
        {
            await PublishAsync($"{_discoveryPrefix}/{component}/{_nodeId}/{objectId}_{suffix}/config", "", retain: true, cancellationToken)
                .ConfigureAwait(false);
        }
        log.LogInformation("Removed camera {Camera} from Home Assistant discovery.", cameraId);
    }

    private Task PublishDiscoveryAsync(string component, string objectId, object configuration, CancellationToken cancellationToken)
        => PublishAsync($"{_discoveryPrefix}/{component}/{_nodeId}/{objectId}/config",
                        JsonSerializer.Serialize(configuration, Json), retain: true, cancellationToken);

    private async Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken)
    {
        if (_client is not { IsConnected: true }) return;

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(payload))
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Handles privacy switch commands coming back from Home Assistant.</summary>
    private Task OnCommandReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        try
        {
            var topic = args.ApplicationMessage.Topic;
            var payload = Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment);

            // topic looks like visionmesh/<objectId>/set
            var parts = topic.Split('/');
            if (parts.Length != 3 || parts[2] != "set") return Task.CompletedTask;

            var camera = cameras.GetAll().FirstOrDefault(c => ObjectId(c.Id) == parts[1]);
            if (camera is null) return Task.CompletedTask;

            switch (payload)
            {
                case "privacy_on":
                case "privacy_off":
                    camera.PrivacyMode = payload == "privacy_on";
                    cameras.Update(camera);
                    log.LogInformation("Home Assistant set privacy mode {State} on camera {Camera}.",
                        camera.PrivacyMode ? "on" : "off", camera.Name);
                    break;
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not handle an MQTT command.");
        }

        return Task.CompletedTask;
    }

    /// <summary>MQTT topics allow a narrow character set, so camera ids are normalised.</summary>
    private static string ObjectId(string cameraId)
        => new(cameraId.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray());

    private async Task DisconnectAsync()
    {
        if (_client is null) return;

        try
        {
            if (_client.IsConnected)
            {
                await PublishAsync(AvailabilityTopic, "offline", retain: true, CancellationToken.None).ConfigureAwait(false);
                await _client.DisconnectAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            log.LogDebug("MQTT disconnect was not clean: {Error}", ex.Message);
        }
        finally
        {
            _client.ApplicationMessageReceivedAsync -= OnCommandReceivedAsync;
            _client.Dispose();
            _client = null;
            _announced.Clear();
        }
    }
}
