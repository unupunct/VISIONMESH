using VisionMesh.Core.Models;

namespace VisionMesh.Database.Repositories;

/// <summary>Index over recording files on disk. The video itself is never stored in SQLite.</summary>
public sealed class RecordingRepository(VisionMeshDatabase database)
{
    private const string SelectColumns = "id, camera_id, file_path, start_utc, end_utc, size_bytes, trigger, closed";

    public long Insert(RecordingSegment segment)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command(
            """
            INSERT INTO recordings (camera_id, file_path, start_utc, end_utc, size_bytes, trigger, closed)
            VALUES ($c, $p, $s, $e, $size, $t, $closed);
            SELECT last_insert_rowid();
            """)
            .With("$c", segment.CameraId).With("$p", segment.FilePath)
            .With("$s", segment.StartUtc.ToDb()).With("$e", segment.EndUtc.ToDbOrNull())
            .With("$size", segment.SizeBytes).With("$t", (int)segment.Trigger)
            .With("$closed", segment.Closed ? 1 : 0);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Close(long id, DateTimeOffset endUtc, long sizeBytes)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command("UPDATE recordings SET end_utc = $e, size_bytes = $size, closed = 1 WHERE id = $id;")
            .With("$id", id).With("$e", endUtc.ToDb()).With("$size", sizeBytes);
        command.ExecuteNonQuery();
    }

    public RecordingSegment? GetById(long id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command($"SELECT {SelectColumns} FROM recordings WHERE id = $id;").With("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public List<RecordingSegment> Query(string? cameraId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int limit, int offset)
    {
        limit = Math.Clamp(limit, 1, 1000);
        using var connection = database.OpenConnection();
        using var command = connection.Command(
            $"""
            SELECT {SelectColumns} FROM recordings
            WHERE ($camera IS NULL OR camera_id = $camera)
              AND ($from   IS NULL OR COALESCE(end_utc, start_utc) >= $from)
              AND ($to     IS NULL OR start_utc <= $to)
            ORDER BY start_utc DESC, id DESC
            LIMIT $limit OFFSET $offset;
            """)
            .With("$camera", cameraId.OrNull())
            .With("$from", fromUtc.ToDbOrNull()).With("$to", toUtc.ToDbOrNull())
            .With("$limit", limit).With("$offset", Math.Max(0, offset));
        using var reader = command.ExecuteReader();
        var result = new List<RecordingSegment>();
        while (reader.Read()) result.Add(Map(reader));
        return result;
    }

    /// <summary>Segments left open by an unclean shutdown, so they can be closed or repaired at startup.</summary>
    public List<RecordingSegment> GetOpenSegments()
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command($"SELECT {SelectColumns} FROM recordings WHERE closed = 0;");
        using var reader = command.ExecuteReader();
        var result = new List<RecordingSegment>();
        while (reader.Read()) result.Add(Map(reader));
        return result;
    }

    /// <summary>Segments past the retention window for one camera, oldest first, for the cleanup job.</summary>
    public List<RecordingSegment> GetExpired(string cameraId, DateTimeOffset cutoffUtc)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command(
            $"SELECT {SelectColumns} FROM recordings WHERE camera_id = $c AND closed = 1 AND start_utc < $cutoff ORDER BY start_utc ASC;")
            .With("$c", cameraId).With("$cutoff", cutoffUtc.ToDb());
        using var reader = command.ExecuteReader();
        var result = new List<RecordingSegment>();
        while (reader.Read()) result.Add(Map(reader));
        return result;
    }

    /// <summary>Oldest closed segments across all cameras, used when a storage cap forces early deletion.</summary>
    public List<RecordingSegment> GetOldest(int limit)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command($"SELECT {SelectColumns} FROM recordings WHERE closed = 1 ORDER BY start_utc ASC LIMIT $l;")
            .With("$l", Math.Clamp(limit, 1, 5000));
        using var reader = command.ExecuteReader();
        var result = new List<RecordingSegment>();
        while (reader.Read()) result.Add(Map(reader));
        return result;
    }

    public void Delete(long id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command("DELETE FROM recordings WHERE id = $id;").With("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Total bytes indexed, per camera or across all cameras. Real file sizes, not estimates.</summary>
    public long GetTotalBytes(string? cameraId = null)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command("SELECT COALESCE(SUM(size_bytes), 0) FROM recordings WHERE ($c IS NULL OR camera_id = $c);")
            .With("$c", cameraId.OrNull());
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static RecordingSegment Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetLong(0),
        CameraId = r.GetString(1),
        FilePath = r.GetString(2),
        StartUtc = r.GetTimestamp(3),
        EndUtc = r.GetTimestampOrNull(4),
        SizeBytes = r.GetLong(5),
        Trigger = (RecordingTrigger)r.GetInt(6),
        Closed = r.GetBool(7),
    };
}
