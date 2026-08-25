using System.Text.Json;
using System.Text.Json.Serialization;
using VisionMesh.Core.Models;

namespace VisionMesh.Agent.Core;

/// <summary>
/// What the agent remembers between runs: which server it belongs to and its device token.
///
/// The token is the agent's only credential, so the file is created with owner-only permissions
/// where the platform supports it. It is stored in plain text deliberately: encrypting it with a
/// key that must also sit on the same disk protects nothing, and pretending otherwise would be
/// worse than being clear about it.
/// </summary>
public sealed class AgentConfiguration
{
    [JsonPropertyName("serverUrl")] public string ServerUrl { get; set; } = "";
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("deviceToken")] public string DeviceToken { get; set; } = "";
    [JsonPropertyName("deviceName")] public string DeviceName { get; set; } = "";
    [JsonPropertyName("serverName")] public string? ServerName { get; set; }
    [JsonPropertyName("pairedUtc")] public DateTimeOffset? PairedUtc { get; set; }

    [JsonIgnore] public bool IsPaired => !string.IsNullOrEmpty(DeviceToken) && !string.IsNullOrEmpty(ServerUrl);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Default configuration location, per platform convention.</summary>
    public static string DefaultPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VisionMesh", "agent.json");
        }

        // Respect XDG_CONFIG_HOME when set, as a well-behaved Linux program should.
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(configHome)) return Path.Combine(configHome, "visionmesh", "agent.json");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // A system service runs without a home directory; fall back to the system location.
        return string.IsNullOrEmpty(home) || home == "/"
            ? "/etc/visionmesh/agent.json"
            : Path.Combine(home, ".config", "visionmesh", "agent.json");
    }

    public static AgentConfiguration Load(string path)
    {
        if (!File.Exists(path)) return new AgentConfiguration { DeviceName = Environment.MachineName };

        try
        {
            var configuration = JsonSerializer.Deserialize<AgentConfiguration>(File.ReadAllText(path));
            if (configuration is null) return new AgentConfiguration { DeviceName = Environment.MachineName };
            if (string.IsNullOrWhiteSpace(configuration.DeviceName)) configuration.DeviceName = Environment.MachineName;
            return configuration;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt config must not stop the agent starting: it simply becomes unpaired,
            // and the user can pair it again.
            return new AgentConfiguration { DeviceName = Environment.MachineName };
        }
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        RestrictToOwner(path);
    }

    public static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return;   // the per-user AppData path already limits access
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
    }

    /// <summary>The device kind this build reports to the server.</summary>
    public static DeviceKind CurrentDeviceKind =>
        OperatingSystem.IsWindows() ? DeviceKind.WindowsAgent : DeviceKind.LinuxAgent;
}
