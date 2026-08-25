using VisionMesh.Core.Models;

namespace VisionMesh.Database.Repositories;

public sealed class CameraRepository(VisionMeshDatabase database)
{
    private const string SelectColumns =
        "id, name, source_kind, device_id, source_id, group_name, enabled, state, recording_mode, retention_days, " +
        "privacy_mode, audio_enabled, ptz_supported, desired_width, desired_height, desired_fps, desired_quality, " +
        "created_utc, config_json, floorplan_x, floorplan_y";

    public List<Camera> GetAll()
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command($"SELECT {SelectColumns} FROM cameras ORDER BY group_name COLLATE NOCASE, name COLLATE NOCASE;");
        using var reader = command.ExecuteReader();
        var result = new List<Camera>();
        while (reader.Read()) result.Add(Map(reader));
        return result;
    }

    public List<Camera> GetByDevice(string deviceId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command($"SELECT {SelectColumns} FROM cameras WHERE device_id = $d ORDER BY name COLLATE NOCASE;").With("$d", deviceId);
        using var reader = command.ExecuteReader();
        var result = new List<Camera>();
        while (reader.Read()) result.Add(Map(reader));
        return result;
    }

    public Camera? GetById(string id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command($"SELECT {SelectColumns} FROM cameras WHERE id = $id;").With("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>Finds an existing camera bound to the same capture device, so re-adding does not duplicate it.</summary>
    public Camera? GetByDeviceSource(string deviceId, string sourceId)
    {
        using var connection = database.OpenConnection();
        using var command = connection
            .Command($"SELECT {SelectColumns} FROM cameras WHERE device_id = $d AND source_id = $s;")
            .With("$d", deviceId).With("$s", sourceId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public void Insert(Camera camera)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command(
            """
            INSERT INTO cameras (id, name, source_kind, device_id, source_id, group_name, enabled, state, recording_mode,
                                 retention_days, privacy_mode, audio_enabled, ptz_supported, desired_width, desired_height,
                                 desired_fps, desired_quality, created_utc, config_json, floorplan_x, floorplan_y)
            VALUES ($id, $name, $kind, $device, $source, $group, $enabled, $state, $mode, $retention, $privacy, $audio,
                    $ptz, $w, $h, $fps, $q, $created, $config, $fx, $fy);
            """);
        Bind(command, camera);
        command.With("$created", camera.CreatedUtc.ToDb());
        command.ExecuteNonQuery();
    }

    public void Update(Camera camera)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command(
            """
            UPDATE cameras SET name = $name, source_kind = $kind, device_id = $device, source_id = $source,
                   group_name = $group, enabled = $enabled, state = $state, recording_mode = $mode,
                   retention_days = $retention, privacy_mode = $privacy, audio_enabled = $audio, ptz_supported = $ptz,
                   desired_width = $w, desired_height = $h, desired_fps = $fps, desired_quality = $q,
                   config_json = $config, floorplan_x = $fx, floorplan_y = $fy
            WHERE id = $id;
            """);
        Bind(command, camera);
        command.ExecuteNonQuery();
    }

    public void SetState(string cameraId, CameraState state)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command("UPDATE cameras SET state = $s WHERE id = $id;")
            .With("$id", cameraId).With("$s", (int)state);
        command.ExecuteNonQuery();
    }

    /// <summary>Resets transient states at startup. Paused and Privacy are user intent and are preserved.</summary>
    public void MarkAllOffline()
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command("UPDATE cameras SET state = 0 WHERE state IN (1, 2);");
        command.ExecuteNonQuery();
    }

    public bool Delete(string id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command("DELETE FROM cameras WHERE id = $id;").With("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    public List<string> GetGroups()
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command("SELECT DISTINCT group_name FROM cameras WHERE group_name IS NOT NULL AND group_name <> '' ORDER BY group_name COLLATE NOCASE;");
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private static void Bind(Microsoft.Data.Sqlite.SqliteCommand command, Camera c) => command
        .With("$id", c.Id)
        .With("$name", c.Name)
        .With("$kind", (int)c.SourceKind)
        .With("$device", c.DeviceId.OrNull())
        .With("$source", c.SourceId.OrNull())
        .With("$group", c.GroupName.OrNull())
        .With("$enabled", c.Enabled ? 1 : 0)
        .With("$state", (int)c.State)
        .With("$mode", (int)c.RecordingMode)
        .With("$retention", c.RetentionDays)
        .With("$privacy", c.PrivacyMode ? 1 : 0)
        .With("$audio", c.AudioEnabled ? 1 : 0)
        .With("$ptz", c.PtzSupported ? 1 : 0)
        .With("$w", c.DesiredWidth)
        .With("$h", c.DesiredHeight)
        .With("$fps", c.DesiredFps)
        .With("$q", c.DesiredQuality)
        .With("$config", c.ConfigJson.OrNull())
        .With("$fx", c.FloorPlanX.OrNull())
        .With("$fy", c.FloorPlanY.OrNull());

    private static Camera Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Name = r.GetString(1),
        SourceKind = (CameraSourceKind)r.GetInt(2),
        DeviceId = r.GetStringOrNull(3),
        SourceId = r.GetStringOrNull(4),
        GroupName = r.GetStringOrNull(5),
        Enabled = r.GetBool(6),
        State = (CameraState)r.GetInt(7),
        RecordingMode = (RecordingMode)r.GetInt(8),
        RetentionDays = r.GetInt(9),
        PrivacyMode = r.GetBool(10),
        AudioEnabled = r.GetBool(11),
        PtzSupported = r.GetBool(12),
        DesiredWidth = r.GetInt(13),
        DesiredHeight = r.GetInt(14),
        DesiredFps = r.GetInt(15),
        DesiredQuality = r.GetInt(16),
        CreatedUtc = r.GetTimestamp(17),
        ConfigJson = r.GetStringOrNull(18),
        FloorPlanX = r.GetDoubleOrNull(19),
        FloorPlanY = r.GetDoubleOrNull(20),
    };
}
