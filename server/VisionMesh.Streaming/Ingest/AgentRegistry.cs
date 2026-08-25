using System.Collections.Concurrent;
using VisionMesh.Core.Models;

namespace VisionMesh.Streaming.Ingest;

/// <summary>
/// Tracks which agents are connected right now.
///
/// Connection state lives in memory only. The database records that a device exists and when
/// it was last seen; whether its socket is up this second is a runtime fact, and persisting it
/// would leave stale "online" rows behind after a crash.
/// </summary>
public sealed class AgentRegistry
{
    private readonly ConcurrentDictionary<string, AgentConnection> _connections = new(StringComparer.Ordinal);

    /// <summary>Raised after a device's connection is added or removed, for dashboard push updates.</summary>
    public event Action<AgentConnection, bool>? ConnectionChanged;

    public int Count => _connections.Count;

    public IReadOnlyCollection<AgentConnection> All => _connections.Values.ToArray();

    public AgentConnection? Find(string deviceId)
        => _connections.TryGetValue(deviceId, out var connection) ? connection : null;

    public bool IsOnline(string deviceId) => _connections.ContainsKey(deviceId);

    /// <summary>
    /// Registers a connection, replacing any previous one for the same device.
    /// Returns the displaced connection so the caller can dispose it - a reconnecting agent
    /// (after a laptop sleeps, say) must not leave a zombie socket owning its camera slots.
    /// </summary>
    public AgentConnection? Add(AgentConnection connection)
    {
        AgentConnection? displaced = null;
        _connections.AddOrUpdate(
            connection.DeviceId,
            connection,
            (_, existing) =>
            {
                displaced = existing;
                return connection;
            });

        ConnectionChanged?.Invoke(connection, true);
        return displaced;
    }

    /// <summary>Removes a connection only if it is still the current one for that device.</summary>
    public bool Remove(AgentConnection connection)
    {
        // The value check matters: a slow disconnect must not evict the newer connection
        // that already replaced it.
        if (!_connections.TryGetValue(connection.DeviceId, out var current) || !ReferenceEquals(current, connection))
            return false;

        var removed = _connections.TryRemove(new KeyValuePair<string, AgentConnection>(connection.DeviceId, connection));
        if (removed) ConnectionChanged?.Invoke(connection, false);
        return removed;
    }

    /// <summary>Every capture device currently advertised by connected agents, grouped by device id.</summary>
    public Dictionary<string, IReadOnlyList<CaptureDeviceInfo>> GetAllCaptureDevices()
    {
        var result = new Dictionary<string, IReadOnlyList<CaptureDeviceInfo>>(StringComparer.Ordinal);
        foreach (var connection in _connections.Values) result[connection.DeviceId] = connection.CaptureDevices;
        return result;
    }
}
