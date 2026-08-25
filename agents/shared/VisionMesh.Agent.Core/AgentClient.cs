using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using VisionMesh.Core.Contracts;
using VisionMesh.Core.Models;

namespace VisionMesh.Agent.Core;

/// <summary>
/// The agent side of the VisionMesh protocol: stays connected to the server, answers capture
/// commands, and pushes JPEG frames.
///
/// Reconnection is the normal case, not an error case. Laptops sleep, Wi-Fi drops, servers get
/// restarted for updates. The client therefore reconnects forever with a capped backoff and
/// restores whatever it was capturing, so an agent left running keeps working without anyone
/// touching it.
/// </summary>
public sealed class AgentClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TelemetryInterval = TimeSpan.FromSeconds(5);

    private readonly AgentConfiguration _configuration;
    private readonly ICameraCapture _capture;
    private readonly ILogger _log;
    private readonly string _version;

    private readonly ConcurrentDictionary<ushort, CaptureWorker> _workers = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private ClientWebSocket? _socket;

    public AgentClient(AgentConfiguration configuration, ICameraCapture capture, string version, ILogger log)
    {
        _configuration = configuration;
        _capture = capture;
        _version = version;
        _log = log;
    }

    /// <summary>True while the WebSocket to the server is up.</summary>
    public bool Connected => _socket?.State == WebSocketState.Open;

    /// <summary>Cameras currently being captured, for the agent's own status display.</summary>
    public IReadOnlyCollection<string> ActiveCameras => _workers.Values.Select(w => w.CameraName).ToArray();

    /// <summary>Raised whenever the connection state changes, so a UI can reflect it.</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>Reports battery state on devices that have one. Null on machines without a battery.</summary>
    public Func<(int Percent, bool Charging)?>? BatteryProvider { get; set; }

    /// <summary>Runs until cancelled, reconnecting as needed.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var backoff = MinBackoff;

        while (!cancellationToken.IsCancellationRequested)
        {
            var connectedAt = DateTimeOffset.UtcNow;
            try
            {
                await ConnectAndPumpAsync(cancellationToken).ConfigureAwait(false);

                // A session that lasted a while was healthy, so the next hiccup should retry fast
                // rather than inheriting a long backoff from an outage hours ago.
                if (DateTimeOffset.UtcNow - connectedAt > TimeSpan.FromMinutes(1)) backoff = MinBackoff;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning("Lost the connection to the server: {Error}", ex.Message);
            }
            finally
            {
                await StopAllCapturesAsync().ConfigureAwait(false);
                ConnectionChanged?.Invoke(false);
            }

            if (cancellationToken.IsCancellationRequested) break;

            _log.LogInformation("Reconnecting in {Seconds} seconds.", (int)backoff.TotalSeconds);
            try { await Task.Delay(backoff, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }

        await StopAllCapturesAsync().ConfigureAwait(false);
    }

    private async Task ConnectAndPumpAsync(CancellationToken cancellationToken)
    {
        var uri = BuildWebSocketUri(_configuration.ServerUrl);

        _socket = new ClientWebSocket();
        _socket.Options.SetRequestHeader("Authorization", $"Bearer {_configuration.DeviceToken}");
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        _log.LogInformation("Connecting to {Server}.", uri);
        await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        _log.LogInformation("Connected to the VisionMesh server.");
        ConnectionChanged?.Invoke(true);

        await SendMessageAsync(new AgentMessage
        {
            Type = AgentMessageType.Hello,
            Hello = new AgentHello
            {
                DeviceId = _configuration.DeviceId,
                Name = _configuration.DeviceName,
                Kind = AgentConfiguration.CurrentDeviceKind,
                Platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                Version = _version,
                Devices = EnumerateSafely(),
            },
        }, cancellationToken).ConfigureAwait(false);

        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var telemetry = TelemetryLoopAsync(sessionCancellation.Token);

        try
        {
            await ReceiveLoopAsync(sessionCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            sessionCancellation.Cancel();
            try { await telemetry.ConfigureAwait(false); } catch (OperationCanceledException) { }

            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Agent stopping", timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }

            _socket.Dispose();
            _socket = null;
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        using var assembled = new MemoryStream(64 * 1024);

        while (!cancellationToken.IsCancellationRequested && _socket?.State == WebSocketState.Open)
        {
            assembled.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                assembled.Write(buffer, 0, result.Count);

                // The server only ever sends small JSON control messages here.
                if (assembled.Length > 256 * 1024) return;
            }
            while (!result.EndOfMessage);

            var json = Encoding.UTF8.GetString(assembled.GetBuffer(), 0, (int)assembled.Length);
            await HandleServerMessageAsync(json, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleServerMessageAsync(string json, CancellationToken cancellationToken)
    {
        ServerMessage? message;
        try { message = JsonSerializer.Deserialize<ServerMessage>(json, Json); }
        catch (JsonException ex)
        {
            _log.LogWarning("The server sent a message the agent could not read: {Error}", ex.Message);
            return;
        }
        if (message is null) return;

        switch (message.Type)
        {
            case ServerMessageType.Welcome:
                _log.LogInformation("Registered with server '{Server}' ({Version}).",
                    message.Welcome?.ServerName ?? "VisionMesh", message.Welcome?.ServerVersion ?? "unknown");
                break;

            case ServerMessageType.StartCapture when message.Start is { } start:
                await StartCaptureAsync(start, cancellationToken).ConfigureAwait(false);
                break;

            case ServerMessageType.StopCapture when message.Slot is { } slot:
                await StopCaptureAsync(slot).ConfigureAwait(false);
                break;

            case ServerMessageType.ListDevices:
                await SendMessageAsync(new AgentMessage
                {
                    Type = AgentMessageType.Devices,
                    Devices = EnumerateSafely(),
                }, cancellationToken).ConfigureAwait(false);
                break;

            case ServerMessageType.Ping:
                await SendMessageAsync(new AgentMessage { Type = AgentMessageType.Pong }, cancellationToken).ConfigureAwait(false);
                break;

            case ServerMessageType.Error:
                _log.LogError("The server reported an error: {Message}", message.Message);
                break;
        }
    }

    private async Task StartCaptureAsync(StartCaptureCommand command, CancellationToken cancellationToken)
    {
        if (_workers.ContainsKey(command.Slot)) return;

        ICaptureSession session;
        try
        {
            session = _capture.Open(command.SourceId, command.Width, command.Height, command.Fps, command.Quality);
        }
        catch (CameraCaptureException ex)
        {
            _log.LogError("Could not open camera {Source}: {Error}", command.SourceId, ex.Message);
            await SendMessageAsync(new AgentMessage
            {
                Type = AgentMessageType.CaptureError,
                Slot = command.Slot,
                CameraId = command.CameraId,
                Message = ex.Message,
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var worker = new CaptureWorker(command, session, this, _log);
        if (!_workers.TryAdd(command.Slot, worker))
        {
            worker.Dispose();
            return;
        }

        worker.Start();
        _log.LogInformation("Capturing {Source} at {Width}x{Height} for camera {Camera}.",
            command.SourceId, session.Width, session.Height, command.CameraId);

        await SendMessageAsync(new AgentMessage
        {
            Type = AgentMessageType.CaptureStarted,
            Slot = command.Slot,
            CameraId = command.CameraId,
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task StopCaptureAsync(ushort slot)
    {
        if (!_workers.TryRemove(slot, out var worker)) return;
        await worker.DisposeAsync().ConfigureAwait(false);
        _log.LogInformation("Stopped capturing for slot {Slot}.", slot);
    }

    private async Task StopAllCapturesAsync()
    {
        foreach (var slot in _workers.Keys.ToArray()) await StopCaptureAsync(slot).ConfigureAwait(false);
    }

    private async Task TelemetryLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TelemetryInterval, cancellationToken).ConfigureAwait(false);

                var battery = BatteryProvider?.Invoke();
                var telemetry = new AgentTelemetry
                {
                    BatteryPercent = battery?.Percent,
                    BatteryCharging = battery?.Charging,
                    Cameras = _workers.Values.Select(w => w.BuildTelemetry()).ToList(),
                };

                await SendMessageAsync(new AgentMessage { Type = AgentMessageType.Telemetry, Telemetry = telemetry }, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Session ended.
        }
    }

    private List<CaptureDeviceInfo> EnumerateSafely()
    {
        try
        {
            return _capture.Enumerate().ToList();
        }
        catch (Exception ex)
        {
            // A driver that throws during enumeration must not stop the agent connecting: the
            // server should still see the machine, just with no cameras on it.
            _log.LogError(ex, "Could not list the cameras on this machine.");
            return new List<CaptureDeviceInfo>();
        }
    }

    internal async Task SendFrameAsync(ushort slot, uint sequence, CapturedFrame frame, CancellationToken cancellationToken)
    {
        if (_socket is not { State: WebSocketState.Open }) return;

        var header = new FrameHeader(
            FramePayload.Jpeg,
            frame.NativeJpeg ? FrameFlags.NativeJpeg : FrameFlags.None,
            slot,
            sequence,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            (ushort)Math.Clamp(frame.Width, 0, ushort.MaxValue),
            (ushort)Math.Clamp(frame.Height, 0, ushort.MaxValue));

        var payload = new byte[FrameHeader.Size + frame.Jpeg.Length];
        header.WriteTo(payload);
        frame.Jpeg.Span.CopyTo(payload.AsSpan(FrameHeader.Size));

        await SendRawAsync(payload, WebSocketMessageType.Binary, cancellationToken).ConfigureAwait(false);
    }

    internal Task SendMessageAsync(AgentMessage message, CancellationToken cancellationToken)
        => SendRawAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, Json)), WebSocketMessageType.Text, cancellationToken);

    private async Task SendRawAsync(byte[] payload, WebSocketMessageType type, CancellationToken cancellationToken)
    {
        if (_socket is not { State: WebSocketState.Open }) return;

        // One send at a time. Several capture workers plus the telemetry loop all write to this
        // socket, and interleaving their frames would corrupt the stream.
        try { await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        try
        {
            if (_socket is not { State: WebSocketState.Open }) return;
            await _socket.SendAsync(payload, type, true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The reconnect loop notices and handles it.
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Turns an http(s) server URL into the ws(s) agent endpoint.</summary>
    public static Uri BuildWebSocketUri(string serverUrl)
    {
        var trimmed = serverUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            throw new ArgumentException($"'{serverUrl}' is not a valid server address.", nameof(serverUrl));

        var scheme = uri.Scheme switch
        {
            "https" or "wss" => "wss",
            "http" or "ws" => "ws",
            _ => throw new ArgumentException($"'{uri.Scheme}' is not a supported server address scheme.", nameof(serverUrl)),
        };

        return new UriBuilder(uri) { Scheme = scheme, Path = AgentProtocol.WebSocketPath, Query = "" }.Uri;
    }

    /// <summary>
    /// Exchanges a pairing code for a permanent device token. Called once, when the user pairs
    /// this machine with a server.
    /// </summary>
    public static async Task<AgentConfiguration> PairAsync(
        HttpClient httpClient,
        string serverUrl,
        string pairingCode,
        string deviceName,
        string version,
        CancellationToken cancellationToken)
    {
        var trimmed = serverUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            throw new ArgumentException($"'{serverUrl}' is not a valid server address.", nameof(serverUrl));

        var request = new
        {
            code = pairingCode.Trim().ToUpperInvariant(),
            name = deviceName,
            kind = AgentConfiguration.CurrentDeviceKind.ToString(),
            platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            version,
        };

        using var response = await httpClient.PostAsJsonAsync(new Uri(uri, "/api/pairing/claim"), request, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var message = TryReadError(body) ?? $"The server rejected the pairing request (HTTP {(int)response.StatusCode}).";
            throw new InvalidOperationException(message);
        }

        var result = JsonSerializer.Deserialize<PairingResponse>(body, Json)
                     ?? throw new InvalidOperationException("The server returned an unreadable pairing response.");

        return new AgentConfiguration
        {
            ServerUrl = trimmed,
            DeviceId = result.DeviceId,
            DeviceToken = result.DeviceToken,
            DeviceName = deviceName,
            ServerName = result.ServerName,
            PairedUtc = DateTimeOffset.UtcNow,
        };
    }

    private static string? TryReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private sealed class PairingResponse
    {
        [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "";
        [JsonPropertyName("deviceToken")] public string DeviceToken { get; set; } = "";
        [JsonPropertyName("serverName")] public string? ServerName { get; set; }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllCapturesAsync().ConfigureAwait(false);
        _socket?.Dispose();
        _sendGate.Dispose();
    }

    /// <summary>Pumps frames from one camera to the server, measuring what it actually achieved.</summary>
    private sealed class CaptureWorker : IAsyncDisposable, IDisposable
    {
        private readonly StartCaptureCommand _command;
        private readonly ICaptureSession _session;
        private readonly AgentClient _client;
        private readonly ILogger _log;
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();

        private Task? _worker;
        private uint _sequence;
        private int _framesSinceLastReport;
        private DateTimeOffset _lastReport = DateTimeOffset.UtcNow;
        private double? _measuredFps;
        private string? _error;

        public CaptureWorker(StartCaptureCommand command, ICaptureSession session, AgentClient client, ILogger log)
        {
            _command = command;
            _session = session;
            _client = client;
            _log = log;
        }

        public string CameraName => _command.CameraId;

        public void Start() => _worker ??= Task.Run(() => RunAsync(_stop.Token));

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            // Pace frames to the requested rate. The camera may deliver faster; sending more than
            // asked for wastes bandwidth on every viewer and every recording.
            var interval = _command.Fps > 0 ? TimeSpan.FromSeconds(1.0 / _command.Fps) : TimeSpan.Zero;
            var nextDue = DateTimeOffset.UtcNow;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var frame = await _session.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
                    if (frame is null)
                    {
                        SetError("The camera stopped sending frames.");
                        break;
                    }

                    var now = DateTimeOffset.UtcNow;
                    if (interval > TimeSpan.Zero && now < nextDue) continue;   // drop, do not queue
                    nextDue = (now > nextDue + interval) ? now + interval : nextDue + interval;

                    await _client.SendFrameAsync(_command.Slot, unchecked(_sequence++), frame.Value, cancellationToken)
                        .ConfigureAwait(false);

                    lock (_gate)
                    {
                        _framesSinceLastReport++;
                        var elapsed = now - _lastReport;
                        if (elapsed > TimeSpan.FromSeconds(2))
                        {
                            _measuredFps = Math.Round(_framesSinceLastReport / elapsed.TotalSeconds, 1);
                            _framesSinceLastReport = 0;
                            _lastReport = now;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped on request.
            }
            catch (Exception ex)
            {
                SetError(ex.Message);
                _log.LogError(ex, "Capture failed for camera {Camera}.", _command.CameraId);

                try
                {
                    await _client.SendMessageAsync(new AgentMessage
                    {
                        Type = AgentMessageType.CaptureError,
                        Slot = _command.Slot,
                        CameraId = _command.CameraId,
                        Message = ex.Message,
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception sendFailure)
                {
                    _log.LogDebug("Could not report the capture failure: {Error}", sendFailure.Message);
                }
            }
        }

        private void SetError(string message)
        {
            lock (_gate) _error = message;
        }

        public CameraTelemetry BuildTelemetry()
        {
            lock (_gate)
            {
                return new CameraTelemetry
                {
                    Slot = _command.Slot,
                    Fps = _measuredFps,
                    DroppedFrames = _session.DroppedFrames,
                    Width = _session.Width,
                    Height = _session.Height,
                    Error = _error,
                };
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (_worker is not null)
            {
                try { await _worker.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            _session.Dispose();
            _stop.Dispose();
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
