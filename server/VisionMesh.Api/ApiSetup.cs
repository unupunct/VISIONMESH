using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisionMesh.Api.Auth;
using VisionMesh.Api.Endpoints;
using VisionMesh.Api.Realtime;
using VisionMesh.Core.Abstractions;
using VisionMesh.Core.Util;
using VisionMesh.Database;
using VisionMesh.Database.Repositories;
using VisionMesh.HomeAssistant;
using VisionMesh.Recording;
using VisionMesh.Streaming;
using VisionMesh.Streaming.Fanout;
using VisionMesh.Streaming.Ingest;
using VisionMesh.Streaming.Sources;

namespace VisionMesh.Api;

/// <summary>Registers every VisionMesh service and maps the whole HTTP surface.</summary>
public static class ApiSetup
{
    /// <summary>
    /// Registers the full server: database, repositories, streaming, recording and integrations.
    /// </summary>
    /// <param name="dataDirectory">Where the database and the secret key live.</param>
    public static IServiceCollection AddVisionMesh(this IServiceCollection services, string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);

        // ---- database and secrets ----
        services.AddSingleton(provider => new VisionMeshDatabase(
            Path.Combine(dataDirectory, "visionmesh.db"),
            provider.GetRequiredService<ILogger<VisionMeshDatabase>>()));

        services.AddSingleton(_ => SecretProtector.LoadOrCreate(Path.Combine(dataDirectory, "secret.key")));

        services.AddSingleton<DeviceRepository>();
        services.AddSingleton<CameraRepository>();
        services.AddSingleton<UserRepository>();
        services.AddSingleton<EventRepository>();
        services.AddSingleton<RecordingRepository>();
        services.AddSingleton<AuditRepository>();
        services.AddSingleton<PairingRepository>();
        services.AddSingleton<SettingsRepository>();

        // ---- streaming ----
        services.AddSingleton<FrameBus>();
        services.AddSingleton<IFrameBus>(provider => provider.GetRequiredService<FrameBus>());
        services.AddSingleton<CameraRuntimeRegistry>();
        services.AddSingleton<AgentRegistry>();
        services.AddSingleton<FfmpegLocator>();
        services.AddSingleton<OnvifDiscovery>();

        // ---- realtime ----
        services.AddSingleton<DashboardHub>();
        services.AddSingleton<IRealtimeNotifier>(provider => provider.GetRequiredService<DashboardHub>());

        // ---- application services ----
        services.AddSingleton<AuthService>();
        services.AddSingleton<CameraService>();
        services.AddSingleton<StorageManager>();
        services.AddSingleton<NetworkInfoService>();
        services.AddSingleton<SystemMetricsService>();
        services.AddSingleton<HomeAssistantClient>();

        // ---- background services ----
        // Registered as singletons first so endpoints can inject them directly, then hosted so
        // they actually run. Without the second registration they would be constructed twice.
        services.AddSingleton<CameraSupervisor>();
        services.AddHostedService(provider => provider.GetRequiredService<CameraSupervisor>());

        services.AddSingleton<RecordingEngine>();
        services.AddHostedService(provider => provider.GetRequiredService<RecordingEngine>());

        services.AddSingleton<RecordingIndexer>();
        services.AddHostedService(provider => provider.GetRequiredService<RecordingIndexer>());

        services.AddSingleton<MqttDiscoveryService>();
        services.AddHostedService(provider => provider.GetRequiredService<MqttDiscoveryService>());

        services.AddHostedService<MaintenanceService>();

        // ---- HTTP clients ----
        services.AddHttpClient("onvif", client =>
        {
            // Cameras that are powered but wedged will accept a connection and never answer.
            // A short timeout keeps a discovery sweep from stalling on one bad device.
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddHttpClient<HomeAssistantClient>(client => client.Timeout = TimeSpan.FromSeconds(10));

        // Enums travel as their names, not their numbers. A client reading state: 0 has to keep
        // a copy of our enum ordering in sync forever; state: "Offline" explains itself and
        // survives values being inserted into the enum later.
        services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        services.AddEndpointsApiExplorer();
        return services;
    }

    /// <summary>Applies migrations. Call once at startup before serving traffic.</summary>
    public static void MigrateVisionMeshDatabase(this IServiceProvider services)
    {
        var database = services.GetRequiredService<VisionMeshDatabase>();
        database.Migrate();

        // Nothing can be connected yet, so clear any state a previous run left marked online.
        services.GetRequiredService<DeviceRepository>().MarkAllOffline();
        services.GetRequiredService<CameraRepository>().MarkAllOffline();
    }

    /// <summary>Maps the entire HTTP and WebSocket surface.</summary>
    public static void MapVisionMesh(this IEndpointRouteBuilder app)
    {
        app.MapSystemEndpoints();
        app.MapCameraEndpoints();
        app.MapDeviceEndpoints();
        app.MapDiscoveryEndpoints();
        app.MapArchiveEndpoints();
        app.MapUserEndpoints();
        app.MapDiagnosticsEndpoints();
        app.MapHomeAssistantEndpoints();
        app.MapWebSocketEndpoints();

        // A liveness probe for service managers and container health checks. Deliberately open:
        // it reveals nothing beyond "the process is answering".
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok", version = BuildInfo.Version }))
            .ExcludeFromDescription();
    }
}

/// <summary>
/// Periodic housekeeping that does not belong to any one subsystem: expiring sessions and
/// pairing codes, and pushing a health tick to open dashboards.
/// </summary>
public sealed class MaintenanceService(
    UserRepository users,
    PairingRepository pairing,
    CameraRepository cameras,
    CameraRuntimeRegistry runtimes,
    FrameBus frameBus,
    IRealtimeNotifier notifier,
    ILogger<MaintenanceService> log) : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastPurge = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Health is pushed every few seconds so the dashboard's fps and bitrate readouts
                // stay live without the browser polling for them.
                foreach (var camera in cameras.GetAll())
                {
                    var runtime = runtimes.Find(camera.Id);
                    if (runtime is null) continue;
                    notifier.CameraHealthChanged(runtime.ToHealth(frameBus.GetSubscriberCount(camera.Id)));
                }

                if (DateTimeOffset.UtcNow - lastPurge > TimeSpan.FromMinutes(30))
                {
                    lastPurge = DateTimeOffset.UtcNow;
                    var sessions = users.PurgeExpiredSessions();
                    var codes = pairing.PurgeExpired();
                    if (sessions > 0 || codes > 0)
                    {
                        log.LogDebug("Housekeeping removed {Sessions} expired session(s) and {Codes} pairing code(s).", sessions, codes);
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Housekeeping pass failed.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
