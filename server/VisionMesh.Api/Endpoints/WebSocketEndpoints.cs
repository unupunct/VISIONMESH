using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using VisionMesh.Api.Auth;
using VisionMesh.Api.Realtime;
using VisionMesh.Core.Abstractions;
using VisionMesh.Core.Contracts;
using VisionMesh.Core.Models;
using VisionMesh.Database.Repositories;
using VisionMesh.Streaming;
using VisionMesh.Streaming.Fanout;
using VisionMesh.Streaming.Ingest;

namespace VisionMesh.Api.Endpoints;

/// <summary>The two WebSocket endpoints: agents pushing video in, dashboards receiving state out.</summary>
public static class WebSocketEndpoints
{
    public static void MapWebSocketEndpoints(this IEndpointRouteBuilder app)
    {
        MapAgentSocket(app);
        MapDashboardSocket(app);
    }

    private static void MapAgentSocket(IEndpointRouteBuilder app)
    {
        app.Map(AgentProtocol.WebSocketPath, async (
            HttpContext http,
            DeviceRepository devices,
            CameraRepository cameras,
            EventRepository events,
            AgentRegistry registry,
            FrameBus frameBus,
            CameraRuntimeRegistry runtimes,
            CameraSupervisor supervisor,
            SettingsRepository settings,
            IRealtimeNotifier notifier,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var log = loggerFactory.CreateLogger("VisionMesh.Agent");

            if (!http.WebSockets.IsWebSocketRequest)
            {
                return Results.BadRequest(new { error = "This endpoint expects a WebSocket connection." });
            }

            // Agents authenticate with their device token. The header is preferred; the query
            // parameter exists for WebSocket clients that cannot set headers, which is common on
            // mobile platforms.
            var token = ExtractAgentToken(http);
            var device = token is null ? null : devices.GetByToken(token);

            if (device is null)
            {
                log.LogWarning("Rejected an agent connection from {Address}: unknown device token.", http.ClientAddress());
                return Results.Json(new { error = "Unknown or revoked device token. Pair the device again." },
                                    statusCode: StatusCodes.Status401Unauthorized);
            }

            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            var address = http.ClientAddress();

            var connection = new AgentConnection(device.Id, device.Name, device.Kind, socket, frameBus, runtimes, address, log);

            // Persist whatever the agent tells us about itself, so the devices page stays accurate
            // across restarts and version upgrades.
            connection.CaptureDevicesUpdated += updated =>
            {
                try
                {
                    var stored = devices.GetById(updated.DeviceId);
                    if (stored is null) return;
                    stored.Name = updated.DeviceName;
                    stored.Platform = updated.Platform;
                    stored.AgentVersion = updated.AgentVersion;
                    stored.LastSeenUtc = DateTimeOffset.UtcNow;
                    stored.State = DeviceState.Online;
                    stored.LastAddress = address;
                    devices.Update(stored);
                    notifier.SystemChanged();
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Could not persist device details for {Device}.", updated.DeviceId);
                }
            };

            connection.CaptureError += (_, cameraId, detail) =>
            {
                var cameraEvent = new CameraEvent
                {
                    CameraId = cameraId,
                    DeviceId = device.Id,
                    Type = EventType.CameraDegraded,
                    Severity = EventSeverity.Warning,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Detail = detail,
                };
                cameraEvent.Id = events.Insert(cameraEvent);
                notifier.EventRaised(cameraEvent);
            };

            connection.TelemetryReceived += updated =>
            {
                foreach (var cameraId in updated.ActiveCameraIds)
                {
                    var runtime = runtimes.Find(cameraId);
                    if (runtime is not null) notifier.CameraHealthChanged(runtime.ToHealth(frameBus.GetSubscriberCount(cameraId)));
                }
            };

            // A reconnecting agent replaces its previous connection; the old socket is torn down
            // so it cannot keep publishing frames for camera slots it no longer owns.
            var displaced = registry.Add(connection);
            if (displaced is not null)
            {
                log.LogInformation("Device {Device} reconnected; closing the previous connection.", device.Id);
                await displaced.DisposeAsync();
            }

            devices.SetState(device.Id, DeviceState.Online, DateTimeOffset.UtcNow, address);
            notifier.DeviceStateChanged(device.Id, DeviceState.Online);
            RaiseDeviceEvent(events, notifier, device.Id, connected: true, device.Name);

            await connection.SendJsonAsync(new ServerMessage
            {
                Type = ServerMessageType.Welcome,
                Welcome = new ServerWelcome
                {
                    DeviceId = device.Id,
                    ServerName = settings.GetString(SettingsRepository.Keys.ServerName, "VisionMesh"),
                    ServerVersion = BuildInfo.Version,
                },
            }, cancellationToken);

            try
            {
                // Bring up any cameras that are already meant to be running on this device.
                await supervisor.ReconcileNowAsync(cancellationToken);
                await connection.RunAsync(cancellationToken);
            }
            finally
            {
                registry.Remove(connection);
                await connection.DisposeAsync();

                devices.SetState(device.Id, DeviceState.Offline, DateTimeOffset.UtcNow, address);
                notifier.DeviceStateChanged(device.Id, DeviceState.Offline);
                RaiseDeviceEvent(events, notifier, device.Id, connected: false, device.Name);

                // Mark this device's cameras offline immediately rather than waiting for the
                // supervisor: the dashboard should reflect a disconnect the moment it happens.
                foreach (var camera in cameras.GetByDevice(device.Id))
                {
                    var runtime = runtimes.Find(camera.Id);
                    if (runtime is null) continue;
                    runtime.State = CameraState.Offline;
                    runtime.ResetMeasurements();
                    notifier.CameraStateChanged(camera.Id, CameraState.Offline);
                }
            }

            return Results.Empty;
        })
        .ExcludeFromDescription();
    }

    private static void MapDashboardSocket(IEndpointRouteBuilder app)
    {
        app.Map("/api/ws", async (HttpContext http, DashboardHub hub, AuthService auth, CancellationToken cancellationToken) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
                return Results.BadRequest(new { error = "This endpoint expects a WebSocket connection." });

            // Browsers cannot set an Authorization header on a WebSocket, so the session cookie
            // carries the credential here.
            var user = auth.Authenticate(http);
            if (user is null)
                return Results.Json(new { error = "Sign in to receive live updates." }, statusCode: StatusCodes.Status401Unauthorized);

            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            await hub.HandleAsync(socket, cancellationToken);
            return Results.Empty;
        })
        .ExcludeFromDescription();
    }

    private static string? ExtractAgentToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var value = header["Bearer ".Length..].Trim();
            if (value.Length > 0) return value;
        }

        var query = http.Request.Query["token"].ToString();
        return string.IsNullOrEmpty(query) ? null : query;
    }

    private static void RaiseDeviceEvent(EventRepository events, IRealtimeNotifier notifier, string deviceId, bool connected, string name)
    {
        var cameraEvent = new CameraEvent
        {
            DeviceId = deviceId,
            Type = connected ? EventType.DeviceConnected : EventType.DeviceDisconnected,
            Severity = connected ? EventSeverity.Info : EventSeverity.Warning,
            TimestampUtc = DateTimeOffset.UtcNow,
            Detail = name,
        };
        cameraEvent.Id = events.Insert(cameraEvent);
        notifier.EventRaised(cameraEvent);
    }
}
