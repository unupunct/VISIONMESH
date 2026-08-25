using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VisionMesh.HomeAssistant;

/// <summary>Result of checking a Home Assistant connection.</summary>
public sealed record HomeAssistantStatus(
    bool Connected,
    string? Version,
    string? LocationName,
    string? Error,
    string? ErrorCode);

/// <summary>
/// Talks to Home Assistant's REST API, only to verify the connection the user configured.
///
/// VisionMesh deliberately does not push entities into Home Assistant over this API. Home
/// Assistant's own model is that an integration living inside HA pulls from the device - that is
/// what makes entities appear with proper unique IDs, device registry entries, and a config flow
/// the user can manage. The custom integration under homeassistant/ does that; this client only
/// answers "is the address and token the user typed actually correct?".
/// </summary>
public sealed class HomeAssistantClient(HttpClient httpClient, ILogger<HomeAssistantClient> log)
{
    public async Task<HomeAssistantStatus> TestConnectionAsync(string baseUrl, string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return new HomeAssistantStatus(false, null, null, "Enter your Home Assistant address.", "no_url");

        if (!Uri.TryCreate(baseUrl.TrimEnd('/'), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new HomeAssistantStatus(false, null, null,
                "That address does not look right. It usually looks like http://homeassistant.local:8123", "bad_url");
        }

        if (string.IsNullOrWhiteSpace(token))
            return new HomeAssistantStatus(false, null, null, "Enter a Home Assistant long-lived access token.", "no_token");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "/api/"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return new HomeAssistantStatus(false, null, null,
                    "Home Assistant rejected that token. Create a new long-lived access token in your Home Assistant profile.", "bad_token");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new HomeAssistantStatus(false, null, null,
                    $"Home Assistant answered with HTTP {(int)response.StatusCode}.", "http_error");
            }

            // /api/ returns {"message": "API running."}; the version lives on /api/config.
            var configuration = await TryGetConfigAsync(uri, token, cancellationToken).ConfigureAwait(false);
            return new HomeAssistantStatus(true, configuration?.Version, configuration?.LocationName, null, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HomeAssistantStatus(false, null, null,
                "Home Assistant did not answer in time. Check the address and that it is running.", "timeout");
        }
        catch (HttpRequestException ex)
        {
            log.LogDebug("Home Assistant connection test failed: {Error}", ex.Message);
            return new HomeAssistantStatus(false, null, null,
                $"Could not reach Home Assistant: {ex.Message}", "unreachable");
        }
    }

    private async Task<HomeAssistantConfig?> TryGetConfigAsync(Uri baseUri, string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/api/config"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadFromJsonAsync<HomeAssistantConfig>(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // The connection already succeeded; version detail is a nicety.
            return null;
        }
    }

    private sealed class HomeAssistantConfig
    {
        [System.Text.Json.Serialization.JsonPropertyName("version")] public string? Version { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("location_name")] public string? LocationName { get; set; }
    }
}
