using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VisionMesh.Core.Abstractions;
using VisionMesh.Core.Contracts;
using VisionMesh.Core.Models;
using VisionMesh.Streaming.Fanout;

namespace VisionMesh.Streaming.Ingest;

/// <summary>
/// One live WebSocket link to one agent (a Windows/Linux machine or a phone in camera mode).
///
/// Owns the slot table that maps the fixed-size slot number in each binary frame header back
/// to a camera id, so the hot path never parses JSON or a string identifier per frame.
/// </summary>
public sealed class AgentConnection : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly WebSocket _socket;
    private readonly IFrameBus _frameBus;
    private readonly CameraRuntimeRegistry _runtimes;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>slot -&gt; camera id, for the binary frame hot path.</summary>
    private readonly ConcurrentDictionary<ushort, string> _slotToCamera = new();
    /// <summary>camera id -&gt; slot, so a stop command can find the slot again.</summary>
    private readonly ConcurrentDictionary<string, ushort> _cameraToSlot = new(StringComparer.Ordinal);
    private int _nextSlot;

    public string DeviceId { get; }
    public string DeviceName { get; private set; }
    public DeviceKind Kind { get; private set; }
    public string Platform { get; private set; } = "";
    public string AgentVersion { get; private set; } = "";
    public string? RemoteAddress { get; }
    public DateTimeOffset ConnectedUtc { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastMessageUtc { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>Capture devices the agent reported. Refreshed on hello and on demand.</summary>
    public IReadOnlyList<CaptureDeviceInfo> CaptureDevices { get; private set; } = Array.Empty<CaptureDeviceInfo>();

    public int? BatteryPercent { get; private set; }
    public bool? BatteryCharging { get; private set; }
    public string? NetworkQuality { get; private set; }

    public AgentConnection(
        string deviceId,
        string deviceName,
        DeviceKind kind,
        WebSocket socket,
        IFrameBus frameBus,
        CameraRuntimeRegistry runtimes,
        string? remoteAddress,
        ILogger log)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        Kind = kind;
        _socket = socket;
        _frameBus = frameBus;
        _runtimes = runtimes;
        RemoteAddress = remoteAddress;
        _log = log;
    }

    public IReadOnlyCollection<string> ActiveCameraIds => _cameraToSlot.Keys.ToArray();

    public bool IsCapturing(string cameraId) => _cameraToSlot.ContainsKey(cameraId);

    /// <summary>Fired when the agent reports a new capture device list, so the server can refresh its cache.</summary>
    public event Action<AgentConnection>? CaptureDevicesUpdated;

    /// <summary>Fired when the agent reports a capture error for a camera.</summary>
    public event Action<AgentConnection, string, string>? CaptureError;

    /// <summary>Fired on every telemetry message, so the server can push a dashboard update.</summary>
    public event Action<AgentConnection>? TelemetryReceived;

    // ---- outbound commands -------------------------------------------------

    /// <summary>
    /// Asks the agent to start capturing the camera's bound source and tag frames with a slot.
    /// Idempotent: starting an already-running camera is a no-op.
    /// </summary>
    public async Task<bool> StartCaptureAsync(Camera camera, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(camera.SourceId)) return false;
        if (_cameraToSlot.ContainsKey(camera.Id)) return true;

        var slot = (ushort)Interlocked.Increment(ref _nextSlot);
        _slotToCamera[slot] = camera.Id;
        _cameraToSlot[camera.Id] = slot;

        var sent = await SendJsonAsync(new ServerMessage
        {
            Type = ServerMessageType.StartCapture,
            Start = new StartCaptureCommand
            {
                Slot = slot,
                CameraId = camera.Id,
                SourceId = camera.SourceId,
                Width = camera.DesiredWidth,
                Height = camera.DesiredHeight,
                Fps = camera.DesiredFps,
                Quality = camera.DesiredQuality,
            },
        }, cancellationToken).ConfigureAwait(false);

        if (!sent)
        {
            _slotToCamera.TryRemove(slot, out _);
            _cameraToSlot.TryRemove(camera.Id, out _);
            return false;
        }

        _runtimes.Get(camera.Id).StartedUtc = DateTimeOffset.UtcNow;
        _log.LogInformation("Requested capture of camera {Camera} on device {Device} (slot {Slot}).", camera.Id, DeviceId, slot);
        return true;
    }

    public async Task StopCaptureAsync(string cameraId, CancellationToken cancellationToken)
    {
        if (!_cameraToSlot.TryRemove(cameraId, out var slot)) return;
        _slotToCamera.TryRemove(slot, out _);

        await SendJsonAsync(new ServerMessage { Type = ServerMessageType.StopCapture, Slot = slot }, cancellationToken).ConfigureAwait(false);

        var runtime = _runtimes.Find(cameraId);
        if (runtime is not null)
        {
            runtime.State = CameraState.Offline;
            runtime.ResetMeasurements();
        }
        _log.LogInformation("Stopped capture of camera {Camera} on device {Device}.", cameraId, DeviceId);
    }

    public Task RequestDeviceListAsync(CancellationToken cancellationToken)
        => SendJsonAsync(new ServerMessage { Type = ServerMessageType.ListDevices }, cancellationToken);

    public Task<bool> SendJsonAsync(ServerMessage message, CancellationToken cancellationToken)
        => SendTextAsync(JsonSerializer.Serialize(message, Json), cancellationToken);

    private async Task<bool> SendTextAsync(string payload, CancellationToken cancellationToken)
    {
        if (_socket.State != WebSocketState.Open) return false;

        // A WebSocket permits only one send at a time; this gate serialises control messages
        // that can otherwise be produced concurrently by API calls and background jobs.
        try
        {
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return false; }

        try
        {
            if (_socket.State != WebSocketState.Open) return false;
            await _socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    // ---- inbound loop ------------------------------------------------------

    /// <summary>Reads from the socket until it closes. Returns when the agent is gone.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        var pingTask = PingLoopAsync(token);

        // 1 MiB ceiling: a 4K JPEG is comfortably under this, and it bounds what a rogue
        // or buggy agent can make the server allocate for a single message.
        const int maxMessageBytes = 1024 * 1024;
        var buffer = new byte[64 * 1024];
        using var assembled = new MemoryStream(128 * 1024);

        try
        {
            while (!token.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                assembled.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _log.LogInformation("Agent {Device} closed the connection ({Status}).", DeviceId, result.CloseStatus);
                        return;
                    }

                    if (assembled.Length + result.Count > maxMessageBytes)
                    {
                        _log.LogWarning(
                            "Agent {Device} sent a message over the {Limit} byte ceiling ({Size} bytes so far); dropping the connection.",
                            DeviceId, maxMessageBytes, assembled.Length + result.Count);
                        return;
                    }
                    assembled.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                LastMessageUtc = DateTimeOffset.UtcNow;

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    HandleBinary(assembled.GetBuffer().AsSpan(0, (int)assembled.Length));
                }
                else
                {
                    HandleText(Encoding.UTF8.GetString(assembled.GetBuffer(), 0, (int)assembled.Length));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
        catch (WebSocketException ex)
        {
            _log.LogInformation("Agent {Device} disconnected: {Reason}", DeviceId, ex.Message);
        }
        finally
        {
            _lifetime.Cancel();
            try { await pingTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private void HandleBinary(ReadOnlySpan<byte> message)
    {
        if (!FrameHeader.TryRead(message, out var header)) return;
        if (header.Payload != FramePayload.Jpeg) return;

        // A slot we never assigned, or one already stopped: drop rather than guess.
        if (!_slotToCamera.TryGetValue(header.Slot, out var cameraId)) return;

        var payload = message[FrameHeader.Size..];
        if (payload.Length < 4) return;

        var frame = new VideoFrame
        {
            CameraId = cameraId,
            Jpeg = payload.ToArray(),
            ReceivedUtc = DateTimeOffset.UtcNow,
            CaptureUnixMs = header.TimestampUnixMs,
            Sequence = header.Sequence,
            Width = header.Width,
            Height = header.Height,
            Flags = header.Flags,
        };

        var runtime = _runtimes.Get(cameraId);
        runtime.RecordFrame(frame);
        if (runtime.State != CameraState.Online) runtime.State = CameraState.Online;

        _frameBus.Publish(frame);
    }

    private void HandleText(string json)
    {
        AgentMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<AgentMessage>(json, Json);
        }
        catch (JsonException ex)
        {
            _log.LogWarning("Agent {Device} sent malformed JSON: {Error}", DeviceId, ex.Message);
            return;
        }
        if (message is null) return;

        switch (message.Type)
        {
            case AgentMessageType.Hello when message.Hello is { } hello:
                DeviceName = string.IsNullOrWhiteSpace(hello.Name) ? DeviceName : hello.Name;
                Kind = hello.Kind;
                Platform = hello.Platform;
                AgentVersion = hello.Version;
                CaptureDevices = hello.Devices;
                CaptureDevicesUpdated?.Invoke(this);
                break;

            case AgentMessageType.Devices when message.Devices is { } devices:
                CaptureDevices = devices;
                CaptureDevicesUpdated?.Invoke(this);
                break;

            case AgentMessageType.Telemetry when message.Telemetry is { } telemetry:
                ApplyTelemetry(telemetry);
                TelemetryReceived?.Invoke(this);
                break;

            case AgentMessageType.CaptureError:
            {
                var cameraId = ResolveCamera(message);
                if (cameraId is not null)
                {
                    var detail = message.Message ?? "Capture failed.";
                    var runtime = _runtimes.Get(cameraId);
                    runtime.LastError = detail;
                    runtime.State = CameraState.Degraded;
                    _log.LogWarning("Capture error on {Device}/{Camera}: {Detail}", DeviceId, cameraId, detail);
                    CaptureError?.Invoke(this, cameraId, detail);
                }
                break;
            }

            case AgentMessageType.CaptureStopped:
            {
                var cameraId = ResolveCamera(message);
                if (cameraId is not null && _cameraToSlot.TryRemove(cameraId, out var slot))
                {
                    _slotToCamera.TryRemove(slot, out _);
                    var runtime = _runtimes.Get(cameraId);
                    runtime.State = CameraState.Offline;
                    runtime.ResetMeasurements();
                }
                break;
            }

            case AgentMessageType.CaptureStarted:
            case AgentMessageType.Pong:
                break;
        }
    }

    private string? ResolveCamera(AgentMessage message)
    {
        if (!string.IsNullOrEmpty(message.CameraId)) return message.CameraId;
        if (message.Slot is { } slot && _slotToCamera.TryGetValue(slot, out var cameraId)) return cameraId;
        return null;
    }

    private void ApplyTelemetry(AgentTelemetry telemetry)
    {
        BatteryPercent = telemetry.BatteryPercent;
        BatteryCharging = telemetry.BatteryCharging;
        NetworkQuality = telemetry.NetworkQuality;

        foreach (var camera in telemetry.Cameras)
        {
            if (!_slotToCamera.TryGetValue(camera.Slot, out var cameraId)) continue;
            var runtime = _runtimes.Get(cameraId);
            runtime.LastHeartbeatUtc = DateTimeOffset.UtcNow;
            runtime.AgentReportedFps = camera.Fps;
            runtime.DroppedFrames = camera.DroppedFrames;
            runtime.BatteryPercent = telemetry.BatteryPercent;
            runtime.BatteryCharging = telemetry.BatteryCharging;
            runtime.NetworkQuality = telemetry.NetworkQuality;
            if (!string.IsNullOrEmpty(camera.Error))
            {
                runtime.LastError = camera.Error;
                runtime.State = CameraState.Degraded;
            }
        }
    }

    private async Task PingLoopAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(15);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                if (!await SendJsonAsync(new ServerMessage { Type = ServerMessageType.Ping }, cancellationToken).ConfigureAwait(false)) return;

                // Three missed intervals with no traffic at all means the link is dead even
                // though TCP has not noticed yet, which is common on Wi-Fi and sleeping phones.
                if (DateTimeOffset.UtcNow - LastMessageUtc > interval * 3)
                {
                    _log.LogWarning("Agent {Device} stopped responding; closing the connection.", DeviceId);
                    _lifetime.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Connection is going away.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();

        foreach (var cameraId in _cameraToSlot.Keys)
        {
            var runtime = _runtimes.Find(cameraId);
            if (runtime is null) continue;
            runtime.State = CameraState.Offline;
            runtime.ResetMeasurements();
        }
        _cameraToSlot.Clear();
        _slotToCamera.Clear();

        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Best effort: the agent may already be gone.
            }
        }

        _lifetime.Dispose();
        _sendGate.Dispose();
    }
}
