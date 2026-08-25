using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VisionMesh.Core.Abstractions;
using VisionMesh.Core.Models;

namespace VisionMesh.Api.Realtime;

/// <summary>One push message to the dashboard. The payload shape depends on <see cref="Type"/>.</summary>
public sealed class RealtimeMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("cameraId")] public string? CameraId { get; set; }
    [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("recording")] public bool? Recording { get; set; }
    [JsonPropertyName("health")] public CameraHealth? Health { get; set; }
    [JsonPropertyName("camera")] public object? Camera { get; set; }
    [JsonPropertyName("event")] public object? Event { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("freeBytes")] public long? FreeBytes { get; set; }
    [JsonPropertyName("timestamp")] public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Pushes live state to every open dashboard over a WebSocket.
///
/// The dashboard is push-driven rather than polling: a camera going offline should show up
/// immediately, and twenty browsers polling a twenty-camera server every second is a real load
/// for no benefit. Each subscriber gets a bounded queue and is dropped if it cannot keep up,
/// because one stalled browser tab must never be able to hold the server's memory hostage.
/// </summary>
public sealed class DashboardHub(ILogger<DashboardHub> log) : IRealtimeNotifier
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConcurrentDictionary<Subscriber, byte> _subscribers = new();

    public int SubscriberCount => _subscribers.Count;

    /// <summary>Serves one dashboard WebSocket until the browser disconnects.</summary>
    public async Task HandleAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var subscriber = new Subscriber();
        _subscribers.TryAdd(subscriber, 0);

        try
        {
            var send = SendLoopAsync(socket, subscriber, cancellationToken);
            var receive = ReceiveLoopAsync(socket, cancellationToken);

            // Either direction ending means the connection is over.
            await Task.WhenAny(send, receive).ConfigureAwait(false);
            subscriber.Complete();
            await Task.WhenAll(SwallowAsync(send), SwallowAsync(receive)).ConfigureAwait(false);
        }
        finally
        {
            _subscribers.TryRemove(subscriber, out _);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
    }

    private static async Task SwallowAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) { }
    }

    private async Task SendLoopAsync(WebSocket socket, Subscriber subscriber, CancellationToken cancellationToken)
    {
        await foreach (var message in subscriber.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (socket.State != WebSocketState.Open) return;
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, Json));
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Drains inbound frames. The dashboard sends nothing meaningful, but a WebSocket that is
    /// never read will not observe the client's close frame, leaving the connection half-open.
    /// </summary>
    private static async Task ReceiveLoopAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return;
        }
    }

    public void Broadcast(RealtimeMessage message)
    {
        if (_subscribers.IsEmpty) return;

        foreach (var subscriber in _subscribers.Keys)
        {
            if (!subscriber.TryPost(message))
            {
                // Queue full: this client is not keeping up. Dropping it is better than growing
                // memory for a browser that has probably already gone away.
                log.LogDebug("Dropping a dashboard subscriber that fell behind.");
                subscriber.Complete();
                _subscribers.TryRemove(subscriber, out _);
            }
        }
    }

    // ---- IRealtimeNotifier -------------------------------------------------

    public void CameraStateChanged(string cameraId, CameraState state)
        => Broadcast(new RealtimeMessage { Type = "camera.state", CameraId = cameraId, State = state.ToString() });

    public void CameraHealthChanged(CameraHealth health)
        => Broadcast(new RealtimeMessage { Type = "camera.health", CameraId = health.CameraId, Health = health });

    public void CameraAdded(Camera camera)
        => Broadcast(new RealtimeMessage { Type = "camera.added", CameraId = camera.Id, Camera = camera });

    public void CameraRemoved(string cameraId)
        => Broadcast(new RealtimeMessage { Type = "camera.removed", CameraId = cameraId });

    public void DeviceStateChanged(string deviceId, DeviceState state)
        => Broadcast(new RealtimeMessage { Type = "device.state", DeviceId = deviceId, State = state.ToString() });

    public void EventRaised(CameraEvent cameraEvent)
        => Broadcast(new RealtimeMessage
        {
            Type = "event",
            CameraId = cameraEvent.CameraId,
            DeviceId = cameraEvent.DeviceId,
            Event = new
            {
                id = cameraEvent.Id,
                cameraId = cameraEvent.CameraId,
                type = cameraEvent.Type.ToString(),
                severity = cameraEvent.Severity.ToString(),
                timestampUtc = cameraEvent.TimestampUtc,
                detail = cameraEvent.Detail,
            },
        });

    public void RecordingChanged(string cameraId, bool recording)
        => Broadcast(new RealtimeMessage { Type = "camera.recording", CameraId = cameraId, Recording = recording });

    public void StorageWarning(string message, long freeBytes)
        => Broadcast(new RealtimeMessage { Type = "storage.warning", Message = message, FreeBytes = freeBytes });

    public void SystemChanged() => Broadcast(new RealtimeMessage { Type = "system.changed" });

    private sealed class Subscriber
    {
        // 64 messages is a couple of seconds of the busiest realistic update rate. A client that
        // cannot drain that has stopped reading altogether.
        private readonly Channel<RealtimeMessage> _channel = Channel.CreateBounded<RealtimeMessage>(
            new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            });

        public ChannelReader<RealtimeMessage> Reader => _channel.Reader;

        public bool TryPost(RealtimeMessage message) => _channel.Writer.TryWrite(message);

        public void Complete() => _channel.Writer.TryComplete();
    }
}
