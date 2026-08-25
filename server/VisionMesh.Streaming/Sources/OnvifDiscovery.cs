using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace VisionMesh.Streaming.Sources;

/// <summary>A camera found on the local network by WS-Discovery.</summary>
public sealed class OnvifDiscoveryResult
{
    /// <summary>Stable device identity from the ONVIF endpoint reference, typically a urn:uuid.</summary>
    public string EndpointReference { get; init; } = "";
    /// <summary>Device service URLs advertised by the camera. The first reachable one is used.</summary>
    public List<string> ServiceAddresses { get; init; } = new();
    public string? Name { get; init; }
    public string? Hardware { get; init; }
    public string? Location { get; init; }
    public string? Address { get; init; }
    public List<string> Scopes { get; init; } = new();

    /// <summary>Best available label for the Add Camera list.</summary>
    public string DisplayName => Name ?? Hardware ?? Address ?? "ONVIF camera";
}

/// <summary>
/// Finds ONVIF cameras with WS-Discovery (SOAP-over-UDP multicast to 239.255.255.250:3702).
///
/// The probe is sent from every usable interface rather than the default route only, because a
/// server with a separate camera VLAN or a Docker/Tailscale interface would otherwise probe the
/// wrong network and report that the user has no cameras.
/// </summary>
public sealed class OnvifDiscovery(ILogger<OnvifDiscovery> log)
{
    private static readonly IPAddress MulticastGroup = IPAddress.Parse("239.255.255.250");
    private const int DiscoveryPort = 3702;

    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Addressing = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    private static readonly XNamespace Discovery = "http://schemas.xmlsoap.org/ws/2005/04/discovery";

    /// <summary>
    /// Probes for cameras and collects replies until <paramref name="timeout"/> elapses.
    /// Results are de-duplicated by endpoint reference, since a camera answers once per interface.
    /// </summary>
    public async Task<List<OnvifDiscoveryResult>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, OnvifDiscoveryResult>(StringComparer.OrdinalIgnoreCase);
        var sockets = new List<UdpClient>();

        try
        {
            foreach (var localAddress in GetUsableLocalAddresses())
            {
                try
                {
                    var client = new UdpClient(new IPEndPoint(localAddress, 0));
                    client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                    sockets.Add(client);
                }
                catch (SocketException ex)
                {
                    log.LogDebug("Skipping interface {Address} for ONVIF discovery: {Error}", localAddress, ex.Message);
                }
            }

            if (sockets.Count == 0)
            {
                log.LogWarning("No usable network interface was found for ONVIF discovery.");
                return new List<OnvifDiscoveryResult>();
            }

            var probe = BuildProbe(out _);
            var endpoint = new IPEndPoint(MulticastGroup, DiscoveryPort);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);

            var listeners = sockets.Select(socket => ListenAsync(socket, found, deadline.Token)).ToArray();

            foreach (var socket in sockets)
            {
                try
                {
                    // Cameras occasionally miss a single multicast datagram, so probe twice.
                    await socket.SendAsync(probe, probe.Length, endpoint).ConfigureAwait(false);
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                    await socket.SendAsync(probe, probe.Length, endpoint).ConfigureAwait(false);
                }
                catch (SocketException ex)
                {
                    log.LogDebug("ONVIF probe send failed on one interface: {Error}", ex.Message);
                }
            }

            await Task.WhenAll(listeners).ConfigureAwait(false);
        }
        finally
        {
            foreach (var socket in sockets) socket.Dispose();
        }

        log.LogInformation("ONVIF discovery finished: {Count} camera(s) responded.", found.Count);
        return found.Values.OrderBy(result => result.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task ListenAsync(UdpClient socket, Dictionary<string, OnvifDiscoveryResult> found, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                var result = TryParseProbeMatch(received.Buffer, received.RemoteEndPoint);
                if (result is null) continue;

                lock (found)
                {
                    var key = string.IsNullOrEmpty(result.EndpointReference)
                        ? result.ServiceAddresses.FirstOrDefault() ?? received.RemoteEndPoint.ToString()
                        : result.EndpointReference;
                    found.TryAdd(key, result);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Discovery window closed.
        }
        catch (SocketException ex)
        {
            log.LogDebug("ONVIF discovery listener stopped: {Error}", ex.Message);
        }
    }

    private static byte[] BuildProbe(out string messageId)
    {
        messageId = "uuid:" + Guid.NewGuid();
        var envelope = new XElement(Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soap", Soap),
            new XAttribute(XNamespace.Xmlns + "wsa", Addressing),
            new XAttribute(XNamespace.Xmlns + "wsd", Discovery),
            new XElement(Soap + "Header",
                new XElement(Addressing + "MessageID", messageId),
                new XElement(Addressing + "To", "urn:schemas-xmlsoap-org:ws:2005:04:discovery"),
                new XElement(Addressing + "Action", "http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe")),
            new XElement(Soap + "Body",
                new XElement(Discovery + "Probe",
                    // dn:NetworkVideoTransmitter is the ONVIF profile-S device type. Probing for it
                    // rather than for everything keeps printers and NAS boxes out of the results.
                    new XElement(Discovery + "Types",
                        new XAttribute(XNamespace.Xmlns + "dn", "http://www.onvif.org/ver10/network/wsdl"),
                        "dn:NetworkVideoTransmitter"))));

        return Encoding.UTF8.GetBytes(new XDocument(envelope).ToString(SaveOptions.DisableFormatting));
    }

    private static OnvifDiscoveryResult? TryParseProbeMatch(byte[] payload, IPEndPoint remote)
    {
        XDocument document;
        try { document = XDocument.Parse(Encoding.UTF8.GetString(payload)); }
        catch (Exception ex) when (ex is System.Xml.XmlException or ArgumentException) { return null; }

        var match = document.Descendants(Discovery + "ProbeMatch").FirstOrDefault();
        if (match is null) return null;

        var endpointReference = match.Descendants(Addressing + "Address").FirstOrDefault()?.Value?.Trim() ?? "";
        var xaddrs = (match.Element(Discovery + "XAddrs")?.Value ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var scopes = (match.Element(Discovery + "Scopes")?.Value ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        return new OnvifDiscoveryResult
        {
            EndpointReference = endpointReference,
            ServiceAddresses = xaddrs,
            Scopes = scopes,
            Name = ReadScope(scopes, "onvif://www.onvif.org/name/"),
            Hardware = ReadScope(scopes, "onvif://www.onvif.org/hardware/"),
            Location = ReadScope(scopes, "onvif://www.onvif.org/location/"),
            Address = remote.Address.ToString(),
        };
    }

    /// <summary>ONVIF scopes are URIs whose last path segment carries the value, percent-encoded.</summary>
    private static string? ReadScope(IEnumerable<string> scopes, string prefix)
    {
        var scope = scopes.FirstOrDefault(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (scope is null) return null;
        var value = scope[prefix.Length..].Trim('/');
        return string.IsNullOrWhiteSpace(value) ? null : Uri.UnescapeDataString(value).Replace('_', ' ');
    }

    /// <summary>IPv4 addresses of every interface that is up, not loopback and not link-local.</summary>
    private static IEnumerable<IPAddress> GetUsableLocalAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!nic.SupportsMulticast) continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(unicast.Address)) continue;

                var bytes = unicast.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue; // APIPA: no camera lives there

                yield return unicast.Address;
            }
        }
    }
}
