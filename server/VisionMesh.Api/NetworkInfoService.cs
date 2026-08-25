using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace VisionMesh.Api;

public sealed record NetworkInterfaceInfo(
    string Name,
    string Description,
    string Type,
    bool Up,
    long? SpeedBitsPerSecond,
    IReadOnlyList<string> Addresses,
    string? Gateway,
    IReadOnlyList<string> DnsServers,
    string? MacAddress,
    bool IsLikelyVpn);

public sealed record NetworkStatus(
    string HostName,
    IReadOnlyList<NetworkInterfaceInfo> Interfaces,
    IReadOnlyList<string> DashboardUrls,
    int Port);

/// <summary>
/// Reports how the server is reachable, and on which interfaces.
///
/// This drives two things the user actually needs: the "open the dashboard at ..." list on the
/// setup screen, and the Network page that explains why a phone on another subnet cannot connect.
/// VPN interfaces are flagged by their reported type, never by matching hard-coded address
/// ranges - Tailscale, WireGuard and corporate VPNs all use different ones and they change.
/// </summary>
public sealed class NetworkInfoService(ILogger<NetworkInfoService> log)
{
    /// <summary>Port the server is listening on. Set at startup once Kestrel has bound.</summary>
    public int Port { get; set; } = 8088;

    public NetworkStatus GetNetworkStatus()
    {
        var interfaces = new List<NetworkInterfaceInfo>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var properties = nic.GetIPProperties();
                var addresses = properties.UnicastAddresses
                    .Where(a => a.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .Where(a => !a.Address.IsIPv6LinkLocal)
                    .Select(a => a.Address.ToString())
                    .ToList();

                if (addresses.Count == 0) continue;

                interfaces.Add(new NetworkInterfaceInfo(
                    nic.Name,
                    nic.Description,
                    nic.NetworkInterfaceType.ToString(),
                    nic.OperationalStatus == OperationalStatus.Up,
                    TryGetSpeed(nic),
                    addresses,
                    properties.GatewayAddresses.FirstOrDefault()?.Address.ToString(),
                    properties.DnsAddresses.Select(a => a.ToString()).ToList(),
                    FormatMac(nic.GetPhysicalAddress()),
                    IsLikelyVpn(nic)));
            }
        }
        catch (NetworkInformationException ex)
        {
            log.LogWarning("Could not enumerate network interfaces: {Error}", ex.Message);
        }

        return new NetworkStatus(Dns.GetHostName(), interfaces, GetDashboardUrls(), Port);
    }

    /// <summary>
    /// URLs the dashboard should be reachable on, best first.
    /// The address on the interface carrying the default route comes first, because that is the
    /// one a phone on the same network will be able to use.
    /// </summary>
    public List<string> GetDashboardUrls()
    {
        var urls = new List<string>();
        var preferred = TryGetPrimaryAddress();
        if (preferred is not null) urls.Add($"http://{preferred}:{Port}");

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(unicast.Address)) continue;

                    var bytes = unicast.Address.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254) continue;   // APIPA is never useful

                    var url = $"http://{unicast.Address}:{Port}";
                    if (!urls.Contains(url)) urls.Add(url);
                }
            }
        }
        catch (NetworkInformationException) { }

        var hostname = Dns.GetHostName();
        if (!string.IsNullOrWhiteSpace(hostname)) urls.Add($"http://{hostname}:{Port}");

        return urls;
    }

    /// <summary>
    /// The local address that would be used to reach the wider network.
    /// Uses a UDP socket "connect", which picks a route without sending a packet - far more
    /// reliable than guessing from the interface list on a multi-homed machine.
    /// </summary>
    public string? TryGetPrimaryAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("203.0.113.1", 9);   // TEST-NET-3: reserved, never routed, never contacted
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static long? TryGetSpeed(NetworkInterface nic)
    {
        try { return nic.Speed > 0 ? nic.Speed : null; }
        catch (PlatformNotSupportedException) { return null; }
    }

    private static string? FormatMac(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? null : string.Join(':', bytes.Select(b => b.ToString("x2")));
    }

    /// <summary>
    /// Heuristic for "this looks like a VPN or tunnel", used only to label the interface in the
    /// UI so the user understands which address a remote phone should use.
    /// </summary>
    private static bool IsLikelyVpn(NetworkInterface nic)
        => nic.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel
           || nic.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase)
           || nic.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
           || nic.Description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase)
           || nic.Name.Contains("tailscale", StringComparison.OrdinalIgnoreCase)
           || nic.Name.StartsWith("wg", StringComparison.OrdinalIgnoreCase)
           || nic.Name.StartsWith("tun", StringComparison.OrdinalIgnoreCase);
}
