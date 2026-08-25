using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace VisionMesh.Streaming.Sources;

/// <summary>Device identity as reported by ONVIF GetDeviceInformation.</summary>
public sealed record OnvifDeviceInformation(string? Manufacturer, string? Model, string? FirmwareVersion, string? SerialNumber, string? HardwareId);

/// <summary>One ONVIF media profile: a named combination of encoder and stream settings.</summary>
public sealed class OnvifProfile
{
    public string Token { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Encoding { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double? FrameRate { get; init; }
    public int? BitrateKbps { get; init; }
    /// <summary>PTZ node token when this profile has a PTZ configuration. Null means no PTZ on this profile.</summary>
    public string? PtzNodeToken { get; init; }

    public string Describe()
    {
        var parts = new List<string>();
        if (Width > 0 && Height > 0) parts.Add($"{Width}x{Height}");
        if (!string.IsNullOrEmpty(Encoding)) parts.Add(Encoding);
        if (FrameRate is > 0) parts.Add($"{FrameRate:0.#} fps");
        return parts.Count == 0 ? Name : $"{Name} ({string.Join(", ", parts)})";
    }
}

/// <summary>What a particular camera actually supports, so the UI can hide controls it does not have.</summary>
public sealed class OnvifCapabilities
{
    public string? MediaServiceUri { get; init; }
    public string? PtzServiceUri { get; init; }
    public string? ImagingServiceUri { get; init; }
    public string? EventsServiceUri { get; init; }
    public bool SupportsPtz => !string.IsNullOrEmpty(PtzServiceUri);
}

/// <summary>
/// A minimal ONVIF Profile S client, speaking SOAP 1.2 directly over HTTP.
///
/// This is hand-written rather than generated from the WSDLs on purpose: the generated stack
/// pulls in WCF, and real cameras deviate from their own schemas often enough that a forgiving
/// XML reader is more reliable than a strict one. Every accessor below tolerates missing
/// elements, because a camera that omits a field is far more common than one that is spec-perfect.
/// </summary>
public sealed class OnvifClient(HttpClient httpClient, ILogger log)
{
    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Device = "http://www.onvif.org/ver10/device/wsdl";
    private static readonly XNamespace Media = "http://www.onvif.org/ver10/media/wsdl";
    private static readonly XNamespace Ptz = "http://www.onvif.org/ver20/ptz/wsdl";
    private static readonly XNamespace Schema = "http://www.onvif.org/ver10/schema";
    private static readonly XNamespace Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    private static readonly XNamespace Wsu = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";

    /// <summary>Raised when the camera rejects the credentials, so callers can report it precisely.</summary>
    public sealed class OnvifAuthenticationException(string message) : Exception(message);

    public sealed class OnvifRequestException(string message) : Exception(message);

    public async Task<OnvifDeviceInformation> GetDeviceInformationAsync(
        string deviceServiceUri, string? username, string? password, CancellationToken cancellationToken)
    {
        var response = await CallAsync(deviceServiceUri, new XElement(Device + "GetDeviceInformation"),
            "http://www.onvif.org/ver10/device/wsdl/GetDeviceInformation", username, password, cancellationToken).ConfigureAwait(false);

        return new OnvifDeviceInformation(
            Value(response, Device + "Manufacturer") ?? Value(response, Schema + "Manufacturer"),
            Value(response, Device + "Model") ?? Value(response, Schema + "Model"),
            Value(response, Device + "FirmwareVersion") ?? Value(response, Schema + "FirmwareVersion"),
            Value(response, Device + "SerialNumber") ?? Value(response, Schema + "SerialNumber"),
            Value(response, Device + "HardwareId") ?? Value(response, Schema + "HardwareId"));
    }

    /// <summary>
    /// Resolves the media and PTZ service endpoints. Tries GetServices first (the modern call)
    /// and falls back to GetCapabilities, which older Profile S cameras still only implement.
    /// </summary>
    public async Task<OnvifCapabilities> GetCapabilitiesAsync(
        string deviceServiceUri, string? username, string? password, CancellationToken cancellationToken)
    {
        try
        {
            var services = await CallAsync(deviceServiceUri,
                new XElement(Device + "GetServices", new XElement(Device + "IncludeCapability", "false")),
                "http://www.onvif.org/ver10/device/wsdl/GetServices", username, password, cancellationToken).ConfigureAwait(false);

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var service in services.Descendants(Device + "Service"))
            {
                var ns = service.Element(Device + "Namespace")?.Value;
                var address = service.Element(Device + "XAddr")?.Value;
                if (!string.IsNullOrEmpty(ns) && !string.IsNullOrEmpty(address)) map[ns] = address;
            }

            if (map.Count > 0)
            {
                return new OnvifCapabilities
                {
                    MediaServiceUri = Lookup(map, "http://www.onvif.org/ver10/media/wsdl"),
                    PtzServiceUri = Lookup(map, "http://www.onvif.org/ver20/ptz/wsdl"),
                    ImagingServiceUri = Lookup(map, "http://www.onvif.org/ver20/imaging/wsdl"),
                    EventsServiceUri = Lookup(map, "http://www.onvif.org/ver10/events/wsdl"),
                };
            }
        }
        catch (OnvifAuthenticationException) { throw; }
        catch (Exception ex)
        {
            log.LogDebug("GetServices was not usable on {Uri} ({Error}); falling back to GetCapabilities.", deviceServiceUri, ex.Message);
        }

        var capabilities = await CallAsync(deviceServiceUri,
            new XElement(Device + "GetCapabilities", new XElement(Device + "Category", "All")),
            "http://www.onvif.org/ver10/device/wsdl/GetCapabilities", username, password, cancellationToken).ConfigureAwait(false);

        return new OnvifCapabilities
        {
            MediaServiceUri = capabilities.Descendants(Schema + "Media").Select(e => e.Element(Schema + "XAddr")?.Value).FirstOrDefault(v => !string.IsNullOrEmpty(v)),
            PtzServiceUri = capabilities.Descendants(Schema + "PTZ").Select(e => e.Element(Schema + "XAddr")?.Value).FirstOrDefault(v => !string.IsNullOrEmpty(v)),
            ImagingServiceUri = capabilities.Descendants(Schema + "Imaging").Select(e => e.Element(Schema + "XAddr")?.Value).FirstOrDefault(v => !string.IsNullOrEmpty(v)),
            EventsServiceUri = capabilities.Descendants(Schema + "Events").Select(e => e.Element(Schema + "XAddr")?.Value).FirstOrDefault(v => !string.IsNullOrEmpty(v)),
        };

        static string? Lookup(Dictionary<string, string> map, string key) => map.TryGetValue(key, out var value) ? value : null;
    }

    public async Task<List<OnvifProfile>> GetProfilesAsync(
        string mediaServiceUri, string? username, string? password, CancellationToken cancellationToken)
    {
        var response = await CallAsync(mediaServiceUri, new XElement(Media + "GetProfiles"),
            "http://www.onvif.org/ver10/media/wsdl/GetProfiles", username, password, cancellationToken).ConfigureAwait(false);

        var profiles = new List<OnvifProfile>();
        foreach (var element in response.Descendants(Media + "Profiles").Concat(response.Descendants(Schema + "Profiles")))
        {
            var token = element.Attribute("token")?.Value;
            if (string.IsNullOrEmpty(token)) continue;

            var encoder = element.Element(Schema + "VideoEncoderConfiguration");
            var resolution = encoder?.Element(Schema + "Resolution");
            var rateControl = encoder?.Element(Schema + "RateControl");

            profiles.Add(new OnvifProfile
            {
                Token = token,
                Name = element.Element(Schema + "Name")?.Value ?? token,
                Encoding = encoder?.Element(Schema + "Encoding")?.Value,
                Width = ParseInt(resolution?.Element(Schema + "Width")?.Value) ?? 0,
                Height = ParseInt(resolution?.Element(Schema + "Height")?.Value) ?? 0,
                FrameRate = ParseDouble(rateControl?.Element(Schema + "FrameRateLimit")?.Value),
                BitrateKbps = ParseInt(rateControl?.Element(Schema + "BitrateLimit")?.Value),
                PtzNodeToken = element.Element(Schema + "PTZConfiguration")?.Element(Schema + "NodeToken")?.Value,
            });
        }
        return profiles;
    }

    /// <summary>Returns the RTSP URL for a profile. The camera returns it without credentials.</summary>
    public async Task<string?> GetStreamUriAsync(
        string mediaServiceUri, string profileToken, string? username, string? password, CancellationToken cancellationToken)
    {
        var body = new XElement(Media + "GetStreamUri",
            new XElement(Media + "StreamSetup",
                new XElement(Schema + "Stream", "RTP-Unicast"),
                new XElement(Schema + "Transport",
                    new XElement(Schema + "Protocol", "RTSP"))),
            new XElement(Media + "ProfileToken", profileToken));

        var response = await CallAsync(mediaServiceUri, body,
            "http://www.onvif.org/ver10/media/wsdl/GetStreamUri", username, password, cancellationToken).ConfigureAwait(false);

        return Value(response, Schema + "Uri") ?? Value(response, Media + "Uri");
    }

    /// <summary>Returns the JPEG snapshot URL, or null when the camera does not offer one.</summary>
    public async Task<string?> GetSnapshotUriAsync(
        string mediaServiceUri, string profileToken, string? username, string? password, CancellationToken cancellationToken)
    {
        try
        {
            var response = await CallAsync(mediaServiceUri,
                new XElement(Media + "GetSnapshotUri", new XElement(Media + "ProfileToken", profileToken)),
                "http://www.onvif.org/ver10/media/wsdl/GetSnapshotUri", username, password, cancellationToken).ConfigureAwait(false);

            return Value(response, Schema + "Uri") ?? Value(response, Media + "Uri");
        }
        catch (OnvifAuthenticationException) { throw; }
        catch (Exception ex)
        {
            // Snapshot is optional in Profile S, so a failure here is informational, not an error.
            log.LogDebug("Camera does not provide a snapshot URI: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Starts a continuous pan/tilt/zoom move. Values are normalised to -1..1 as ONVIF defines;
    /// the caller must send <see cref="StopAsync"/> to end the movement.
    /// </summary>
    public async Task ContinuousMoveAsync(
        string ptzServiceUri, string profileToken, double pan, double tilt, double zoom,
        string? username, string? password, CancellationToken cancellationToken)
    {
        var velocity = new XElement(Ptz + "Velocity");
        if (pan != 0 || tilt != 0)
        {
            velocity.Add(new XElement(Schema + "PanTilt",
                new XAttribute("x", Format(pan)),
                new XAttribute("y", Format(tilt))));
        }
        if (zoom != 0)
        {
            velocity.Add(new XElement(Schema + "Zoom", new XAttribute("x", Format(zoom))));
        }

        var body = new XElement(Ptz + "ContinuousMove",
            new XElement(Ptz + "ProfileToken", profileToken),
            velocity);

        await CallAsync(ptzServiceUri, body, "http://www.onvif.org/ver20/ptz/wsdl/ContinuousMove",
            username, password, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(
        string ptzServiceUri, string profileToken, string? username, string? password, CancellationToken cancellationToken)
    {
        var body = new XElement(Ptz + "Stop",
            new XElement(Ptz + "ProfileToken", profileToken),
            new XElement(Ptz + "PanTilt", "true"),
            new XElement(Ptz + "Zoom", "true"));

        await CallAsync(ptzServiceUri, body, "http://www.onvif.org/ver20/ptz/wsdl/Stop",
            username, password, cancellationToken).ConfigureAwait(false);
    }

    // ---- SOAP plumbing -----------------------------------------------------

    private async Task<XElement> CallAsync(
        string serviceUri, XElement body, string action, string? username, string? password, CancellationToken cancellationToken)
    {
        var envelope = new XElement(Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", Soap),
            new XAttribute(XNamespace.Xmlns + "tds", Device),
            new XAttribute(XNamespace.Xmlns + "trt", Media),
            new XAttribute(XNamespace.Xmlns + "tptz", Ptz),
            new XAttribute(XNamespace.Xmlns + "tt", Schema),
            BuildSecurityHeader(username, password),
            new XElement(Soap + "Body", body));

        var xml = new XDocument(new XDeclaration("1.0", "utf-8", null), envelope).ToString(SaveOptions.DisableFormatting);

        using var request = new HttpRequestMessage(HttpMethod.Post, serviceUri)
        {
            Content = new StringContent(xml, Encoding.UTF8),
        };
        // SOAP 1.2 carries the action inside the content type, not in a SOAPAction header.
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
        request.Content.Headers.ContentType.Parameters.Add(new NameValueHeaderValue("action", $"\"{action}\""));

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new OnvifAuthenticationException("The camera rejected the username or password.");

        XDocument document;
        try { document = XDocument.Parse(text); }
        catch (System.Xml.XmlException)
        {
            throw new OnvifRequestException($"The camera returned a non-XML response ({(int)response.StatusCode}).");
        }

        var fault = document.Descendants(Soap + "Fault").FirstOrDefault();
        if (fault is not null)
        {
            var reason = fault.Descendants(Soap + "Text").FirstOrDefault()?.Value
                         ?? fault.Descendants(Soap + "Value").LastOrDefault()?.Value
                         ?? "Unknown SOAP fault";

            if (reason.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("Sender not Authorized", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("auth", StringComparison.OrdinalIgnoreCase))
            {
                throw new OnvifAuthenticationException("The camera rejected the username or password.");
            }
            throw new OnvifRequestException(reason);
        }

        if (!response.IsSuccessStatusCode)
            throw new OnvifRequestException($"The camera returned HTTP {(int)response.StatusCode}.");

        var responseBody = document.Root?.Element(Soap + "Body");
        return responseBody ?? throw new OnvifRequestException("The camera returned a SOAP envelope with no body.");
    }

    /// <summary>
    /// Builds a WS-Security UsernameToken with a password digest.
    /// The password itself never crosses the wire: the digest is SHA-1 over nonce, timestamp and
    /// password, which is what ONVIF mandates and what every Profile S camera expects.
    /// </summary>
    private static XElement? BuildSecurityHeader(string? username, string? password)
    {
        if (string.IsNullOrEmpty(username)) return null;

        var nonce = RandomNumberGenerator.GetBytes(16);
        var created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        var passwordBytes = Encoding.UTF8.GetBytes(password ?? "");
        var createdBytes = Encoding.UTF8.GetBytes(created);
        var material = new byte[nonce.Length + createdBytes.Length + passwordBytes.Length];
        nonce.CopyTo(material, 0);
        createdBytes.CopyTo(material, nonce.Length);
        passwordBytes.CopyTo(material, nonce.Length + createdBytes.Length);

        var digest = Convert.ToBase64String(SHA1.HashData(material));

        return new XElement(Soap + "Header",
            new XElement(Wsse + "Security",
                new XAttribute(XNamespace.Xmlns + "wsse", Wsse),
                new XAttribute(XNamespace.Xmlns + "wsu", Wsu),
                new XAttribute(Soap + "mustUnderstand", "1"),
                new XElement(Wsse + "UsernameToken",
                    new XElement(Wsse + "Username", username),
                    new XElement(Wsse + "Password",
                        new XAttribute("Type", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest"),
                        digest),
                    new XElement(Wsse + "Nonce",
                        new XAttribute("EncodingType", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary"),
                        Convert.ToBase64String(nonce)),
                    new XElement(Wsu + "Created", created))));
    }

    private static string? Value(XElement root, XName name) => root.Descendants(name).FirstOrDefault()?.Value?.Trim() is { Length: > 0 } v ? v : null;

    private static int? ParseInt(string? text)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static double? ParseDouble(string? text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string Format(double value)
        => Math.Clamp(value, -1.0, 1.0).ToString("0.###", CultureInfo.InvariantCulture);
}
