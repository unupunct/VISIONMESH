using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionMesh.Core.Models;

/// <summary>
/// Per-source settings stored in <see cref="Camera.ConfigJson"/>.
/// Password fields hold ciphertext produced by SecretProtector, never plaintext.
/// </summary>
public sealed class CameraSourceConfig
{
    [JsonPropertyName("rtspUrl")] public string? RtspUrl { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    /// <summary>Encrypted. Use SecretProtector.Unprotect before use; never return this over the API.</summary>
    [JsonPropertyName("passwordEnc")] public string? PasswordEnc { get; set; }
    [JsonPropertyName("transport")] public RtspTransport Transport { get; set; } = RtspTransport.Auto;

    [JsonPropertyName("onvifAddress")] public string? OnvifAddress { get; set; }
    [JsonPropertyName("onvifProfileToken")] public string? OnvifProfileToken { get; set; }
    [JsonPropertyName("onvifProfileName")] public string? OnvifProfileName { get; set; }
    [JsonPropertyName("snapshotUri")] public string? SnapshotUri { get; set; }
    [JsonPropertyName("ptzToken")] public string? PtzToken { get; set; }

    /// <summary>Manufacturer/model reported by the device, shown in the camera detail panel.</summary>
    [JsonPropertyName("manufacturer")] public string? Manufacturer { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("serial")] public string? Serial { get; set; }

    /// <summary>
    /// Recording schedule for <see cref="RecordingMode.Scheduled"/>, in the server's local time.
    /// Seven characters, Monday first, '1' meaning the schedule applies that day. Null means every day.
    /// </summary>
    [JsonPropertyName("scheduleDays")] public string? ScheduleDays { get; set; }
    /// <summary>Window start as HH:mm local. Null means from midnight.</summary>
    [JsonPropertyName("scheduleStart")] public string? ScheduleStart { get; set; }
    /// <summary>Window end as HH:mm local. A value earlier than the start means the window crosses midnight.</summary>
    [JsonPropertyName("scheduleEnd")] public string? ScheduleEnd { get; set; }

    /// <summary>Per-camera motion sensitivity 1-100. Null falls back to the server-wide setting.</summary>
    [JsonPropertyName("motionSensitivity")] public int? MotionSensitivity { get; set; }

    /// <summary>
    /// True when this schedule is active at <paramref name="localNow"/>.
    /// An unset schedule means "always", which is what a user expects after picking Scheduled
    /// without yet filling anything in.
    /// </summary>
    public bool IsScheduleActive(DateTimeOffset localNow)
    {
        if (!string.IsNullOrEmpty(ScheduleDays) && ScheduleDays.Length == 7)
        {
            // DayOfWeek starts at Sunday; the string starts at Monday.
            var index = ((int)localNow.DayOfWeek + 6) % 7;
            if (ScheduleDays[index] != '1') return false;
        }

        var start = ParseTime(ScheduleStart) ?? TimeSpan.Zero;
        var end = ParseTime(ScheduleEnd) ?? TimeSpan.FromDays(1);
        var now = localNow.TimeOfDay;

        // A window whose end is before its start wraps past midnight, e.g. 22:00 to 06:00.
        return start <= end ? now >= start && now < end : now >= start || now < end;
    }

    private static TimeSpan? ParseTime(string? value)
        => TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static CameraSourceConfig FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new CameraSourceConfig();
        try { return JsonSerializer.Deserialize<CameraSourceConfig>(json) ?? new CameraSourceConfig(); }
        catch (JsonException) { return new CameraSourceConfig(); }
    }

    /// <summary>
    /// Builds the RTSP URL with credentials embedded, for handing to ffmpeg.
    /// Only ever used in-process; the result must not be logged or returned by the API.
    /// </summary>
    public string? BuildAuthenticatedRtspUrl(string? plaintextPassword)
    {
        if (string.IsNullOrWhiteSpace(RtspUrl)) return null;
        if (string.IsNullOrEmpty(Username)) return RtspUrl;
        if (!Uri.TryCreate(RtspUrl, UriKind.Absolute, out var uri)) return RtspUrl;

        var builder = new UriBuilder(uri)
        {
            UserName = Uri.EscapeDataString(Username),
            Password = Uri.EscapeDataString(plaintextPassword ?? ""),
        };
        return builder.Uri.ToString();
    }
}

/// <summary>Redacts anything that looks like credentials before a URL reaches a log or an API response.</summary>
public static class UrlRedactor
{
    public static string Redact(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "(hidden)";
        if (string.IsNullOrEmpty(uri.UserInfo)) return url;
        var builder = new UriBuilder(uri) { UserName = "***", Password = "***" };
        return builder.Uri.ToString();
    }
}
