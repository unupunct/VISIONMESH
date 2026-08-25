using System.Globalization;

namespace VisionMesh.Database.Repositories;

/// <summary>Key/value server configuration that the user can change from the dashboard.</summary>
public sealed class SettingsRepository(VisionMeshDatabase database)
{
    public static class Keys
    {
        public const string ServerName = "server.name";
        public const string SetupComplete = "server.setupComplete";
        public const string RecordingsPath = "storage.recordingsPath";
        public const string RetentionDays = "storage.retentionDays";
        public const string StorageLimitGb = "storage.limitGb";
        public const string AdvancedMode = "ui.advancedMode";
        public const string HomeAssistantUrl = "homeassistant.url";
        public const string HomeAssistantTokenEnc = "homeassistant.tokenEnc";
        public const string HomeAssistantEnabled = "homeassistant.enabled";
        public const string MqttHost = "mqtt.host";
        public const string MqttPort = "mqtt.port";
        public const string MqttUsername = "mqtt.username";
        public const string MqttPasswordEnc = "mqtt.passwordEnc";
        public const string MqttEnabled = "mqtt.enabled";
        public const string MqttDiscoveryPrefix = "mqtt.discoveryPrefix";
        public const string FfmpegPath = "media.ffmpegPath";
        public const string MotionSensitivity = "motion.sensitivity";
        public const string FloorPlanImage = "ui.floorPlanImage";
    }

    public string? Get(string key)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command("SELECT value FROM settings WHERE key = $k;").With("$k", key);
        return command.ExecuteScalar() as string;
    }

    public Dictionary<string, string> GetAll()
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command("SELECT key, value FROM settings;");
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    public void Set(string key, string value)
    {
        using var connection = database.OpenConnection();
        using var command = connection
            .Command("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;")
            .With("$k", key).With("$v", value);
        command.ExecuteNonQuery();
    }

    public void Delete(string key)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command("DELETE FROM settings WHERE key = $k;").With("$k", key);
        command.ExecuteNonQuery();
    }

    public string GetString(string key, string fallback) => Get(key) is { Length: > 0 } v ? v : fallback;

    public int GetInt(string key, int fallback)
        => int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public bool GetBool(string key, bool fallback)
        => Get(key) is { } v ? v is "1" or "true" or "True" : fallback;

    public void SetInt(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));
    public void SetBool(string key, bool value) => Set(key, value ? "1" : "0");
}
