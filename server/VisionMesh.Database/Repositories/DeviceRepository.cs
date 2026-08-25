using VisionMesh.Core.Models;
using VisionMesh.Core.Util;

namespace VisionMesh.Database.Repositories;

public sealed class DeviceRepository(VisionMeshDatabase database)
{
    private const string SelectColumns =
        "id, name, kind, platform, agent_version, last_address, created_utc, last_seen_utc, state, token_hash, battery_json";

    public List<Device> GetAll()
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command($"SELECT {SelectColumns} FROM devices ORDER BY name COLLATE NOCASE;");
        using var reader = command.ExecuteReader();
        var result = new List<Device>();
        while (reader.Read()) result.Add(Map(reader));
        return result;
    }

    public Device? GetById(string id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command($"SELECT {SelectColumns} FROM devices WHERE id = $id;").With("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>Looks a device up by its bearer token. The token is hashed before comparison.</summary>
    public Device? GetByToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        using var connection = database.OpenConnection();
        using var command = connection
            .Command($"SELECT {SelectColumns} FROM devices WHERE token_hash = $hash;")
            .With("$hash", TokenHasher.Hash(token));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public void Insert(Device device)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command(
            """
            INSERT INTO devices (id, name, kind, platform, agent_version, last_address, created_utc, last_seen_utc, state, token_hash, battery_json)
            VALUES ($id, $name, $kind, $platform, $version, $address, $created, $lastSeen, $state, $token, $battery);
            """)
            .With("$id", device.Id)
            .With("$name", device.Name)
            .With("$kind", (int)device.Kind)
            .With("$platform", device.Platform)
            .With("$version", device.AgentVersion)
            .With("$address", device.LastAddress.OrNull())
            .With("$created", device.CreatedUtc.ToDb())
            .With("$lastSeen", device.LastSeenUtc.ToDbOrNull())
            .With("$state", (int)device.State)
            .With("$token", device.TokenHash)
            .With("$battery", device.BatteryJson.OrNull());
        command.ExecuteNonQuery();
    }

    public void Update(Device device)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command(
            """
            UPDATE devices SET name = $name, kind = $kind, platform = $platform, agent_version = $version,
                   last_address = $address, last_seen_utc = $lastSeen, state = $state, battery_json = $battery
            WHERE id = $id;
            """)
            .With("$id", device.Id)
            .With("$name", device.Name)
            .With("$kind", (int)device.Kind)
            .With("$platform", device.Platform)
            .With("$version", device.AgentVersion)
            .With("$address", device.LastAddress.OrNull())
            .With("$lastSeen", device.LastSeenUtc.ToDbOrNull())
            .With("$state", (int)device.State)
            .With("$battery", device.BatteryJson.OrNull());
        command.ExecuteNonQuery();
    }

    public void SetState(string deviceId, DeviceState state, DateTimeOffset seenUtc, string? address)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command(
            "UPDATE devices SET state = $state, last_seen_utc = $seen, last_address = COALESCE($address, last_address) WHERE id = $id;")
            .With("$id", deviceId)
            .With("$state", (int)state)
            .With("$seen", seenUtc.ToDb())
            .With("$address", address.OrNull());
        command.ExecuteNonQuery();
    }

    /// <summary>Marks every device offline. Run at startup so stale "online" rows never survive a restart.</summary>
    public void MarkAllOffline()
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command("UPDATE devices SET state = 0;");
        command.ExecuteNonQuery();
    }

    public bool Delete(string id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command("DELETE FROM devices WHERE id = $id;").With("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    private static Device Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Name = r.GetString(1),
        Kind = (DeviceKind)r.GetInt(2),
        Platform = r.GetString(3),
        AgentVersion = r.GetString(4),
        LastAddress = r.GetStringOrNull(5),
        CreatedUtc = r.GetTimestamp(6),
        LastSeenUtc = r.GetTimestampOrNull(7),
        State = (DeviceState)r.GetInt(8),
        TokenHash = r.GetString(9),
        BatteryJson = r.GetStringOrNull(10),
    };
}
