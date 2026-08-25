using VisionMesh.Core.Models;

namespace VisionMesh.Database.Repositories;

public sealed class EventRepository(VisionMeshDatabase database)
{
    private const string SelectColumns = "id, camera_id, device_id, type, severity, timestamp_utc, detail, recording_id";

    public long Insert(CameraEvent e)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command(
            """
            INSERT INTO events (camera_id, device_id, type, severity, timestamp_utc, detail, recording_id)
            VALUES ($c, $d, $t, $s, $ts, $detail, $rec);
            SELECT last_insert_rowid();
            """)
            .With("$c", e.CameraId.OrNull()).With("$d", e.DeviceId.OrNull())
            .With("$t", (int)e.Type).With("$s", (int)e.Severity)
            .With("$ts", e.TimestampUtc.ToDb()).With("$detail", e.Detail.OrNull())
            .With("$rec", e.RecordingId.HasValue ? e.RecordingId.Value : DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>Newest-first event query with optional camera, type and time filters.</summary>
    public List<CameraEvent> Query(string? cameraId, EventType? type, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int limit, int offset)
    {
        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Max(0, offset);

        using var connection = database.OpenConnection();
        using var command = connection.Command(
            $"""
            SELECT {SelectColumns} FROM events
            WHERE ($camera IS NULL OR camera_id = $camera)
              AND ($type   IS NULL OR type = $type)
              AND ($from   IS NULL OR timestamp_utc >= $from)
              AND ($to     IS NULL OR timestamp_utc <= $to)
            ORDER BY timestamp_utc DESC, id DESC
            LIMIT $limit OFFSET $offset;
            """)
            .With("$camera", cameraId.OrNull())
            .With("$type", type.HasValue ? (int)type.Value : DBNull.Value)
            .With("$from", fromUtc.ToDbOrNull())
            .With("$to", toUtc.ToDbOrNull())
            .With("$limit", limit).With("$offset", offset);

        using var reader = command.ExecuteReader();
        var result = new List<CameraEvent>();
        while (reader.Read()) result.Add(Map(reader));
        return result;
    }

    public int Count(string? cameraId, EventType? type, DateTimeOffset? fromUtc, DateTimeOffset? toUtc)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command(
            """
            SELECT COUNT(*) FROM events
            WHERE ($camera IS NULL OR camera_id = $camera)
              AND ($type   IS NULL OR type = $type)
              AND ($from   IS NULL OR timestamp_utc >= $from)
              AND ($to     IS NULL OR timestamp_utc <= $to);
            """)
            .With("$camera", cameraId.OrNull())
            .With("$type", type.HasValue ? (int)type.Value : DBNull.Value)
            .With("$from", fromUtc.ToDbOrNull())
            .With("$to", toUtc.ToDbOrNull());
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>Drops events older than the retention window so the database cannot grow without bound.</summary>
    public int PurgeOlderThan(DateTimeOffset cutoffUtc)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command("DELETE FROM events WHERE timestamp_utc < $cutoff;").With("$cutoff", cutoffUtc.ToDb());
        return command.ExecuteNonQuery();
    }

    private static CameraEvent Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetLong(0),
        CameraId = r.GetStringOrNull(1),
        DeviceId = r.GetStringOrNull(2),
        Type = (EventType)r.GetInt(3),
        Severity = (EventSeverity)r.GetInt(4),
        TimestampUtc = r.GetTimestamp(5),
        Detail = r.GetStringOrNull(6),
        RecordingId = r.IsDBNull(7) ? null : r.GetLong(7),
    };
}
