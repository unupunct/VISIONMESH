using VisionMesh.Core.Models;

namespace VisionMesh.Database.Repositories;

/// <summary>Append-only record of security-relevant actions. Never exposed to non-administrators.</summary>
public sealed class AuditRepository(VisionMeshDatabase database)
{
    public void Write(AuditEntry entry)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command(
            "INSERT INTO audit (timestamp_utc, user_id, username, action, target, address, detail) VALUES ($ts, $uid, $un, $a, $t, $addr, $d);")
            .With("$ts", entry.TimestampUtc.ToDb()).With("$uid", entry.UserId.OrNull())
            .With("$un", entry.Username.OrNull()).With("$a", entry.Action)
            .With("$t", entry.Target.OrNull()).With("$addr", entry.Address.OrNull())
            .With("$d", entry.Detail.OrNull());
        command.ExecuteNonQuery();
    }

    public List<AuditEntry> Query(int limit, int offset)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command(
            "SELECT id, timestamp_utc, user_id, username, action, target, address, detail FROM audit ORDER BY timestamp_utc DESC, id DESC LIMIT $l OFFSET $o;")
            .With("$l", Math.Clamp(limit, 1, 1000)).With("$o", Math.Max(0, offset));
        using var reader = command.ExecuteReader();
        var result = new List<AuditEntry>();
        while (reader.Read())
        {
            result.Add(new AuditEntry
            {
                Id = reader.GetLong(0),
                TimestampUtc = reader.GetTimestamp(1),
                UserId = reader.GetStringOrNull(2),
                Username = reader.GetStringOrNull(3),
                Action = reader.GetString(4),
                Target = reader.GetStringOrNull(5),
                Address = reader.GetStringOrNull(6),
                Detail = reader.GetStringOrNull(7),
            });
        }
        return result;
    }

    public int PurgeOlderThan(DateTimeOffset cutoffUtc)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command("DELETE FROM audit WHERE timestamp_utc < $c;").With("$c", cutoffUtc.ToDb());
        return command.ExecuteNonQuery();
    }
}
