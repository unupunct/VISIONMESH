using Makaretu.Dns;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VisionMesh.Api;
using VisionMesh.Api.Endpoints;
using VisionMesh.Database.Repositories;

namespace VisionMesh.Server;

/// <summary>
/// Advertises the server on the local network with mDNS/DNS-SD, so agents, phones and the
/// dashboard can find it without anyone typing an IP address.
///
/// This is what makes identity independent of addressing: a server whose DHCP lease changes keeps
/// the same service instance name, and clients that cached its address rediscover it. Agents
/// still store a device token rather than an address, so a moved server is found, not lost.
/// </summary>
public sealed class MdnsAdvertiser(
    SettingsRepository settings,
    NetworkInfoService network,
    ILogger<MdnsAdvertiser> log) : IHostedService
{
    /// <summary>DNS-SD service type. Agents browse for exactly this.</summary>
    public const string ServiceType = "_visionmesh._tcp";

    private ServiceDiscovery? _discovery;
    private MulticastService? _multicast;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var serverName = settings.GetString(SettingsRepository.Keys.ServerName, "VisionMesh");
            var instanceName = SanitiseInstanceName(serverName);

            _multicast = new MulticastService();
            _discovery = new ServiceDiscovery(_multicast);

            var profile = new ServiceProfile(instanceName, ServiceType, (ushort)network.Port);

            // TXT records let a client show a friendly name and pick the right API version before
            // it has authenticated with anything.
            profile.AddProperty("name", serverName);
            profile.AddProperty("version", BuildInfo.Version);
            profile.AddProperty("api", "/api");
            profile.AddProperty("agent", Core.Contracts.AgentProtocol.WebSocketPath);
            profile.AddProperty("protocol", Core.Contracts.AgentProtocol.Version.ToString());

            _discovery.Advertise(profile);
            _multicast.Start();

            log.LogInformation("Advertising this server on the local network as {Instance}.{Type}.local on port {Port}.",
                instanceName, ServiceType, network.Port);
        }
        catch (Exception ex)
        {
            // Discovery is a convenience. A server that cannot multicast (locked-down network,
            // container without host networking) must still serve the dashboard normally.
            log.LogWarning("Could not advertise the server over mDNS: {Error}. VisionMesh still works using its IP address.", ex.Message);
            Cleanup();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Cleanup();
        return Task.CompletedTask;
    }

    private void Cleanup()
    {
        try
        {
            _discovery?.Dispose();
            _multicast?.Stop();
            _multicast?.Dispose();
        }
        catch (Exception ex)
        {
            log.LogDebug("mDNS shutdown was not clean: {Error}", ex.Message);
        }
        finally
        {
            _discovery = null;
            _multicast = null;
        }
    }

    /// <summary>
    /// DNS-SD instance names allow most characters but not dots, which would split the name into
    /// extra labels and produce an unresolvable record.
    /// </summary>
    private static string SanitiseInstanceName(string name)
    {
        var cleaned = new string(name.Where(c => !char.IsControl(c) && c != '.').ToArray()).Trim();
        if (string.IsNullOrEmpty(cleaned)) cleaned = "VisionMesh";
        return cleaned.Length > 63 ? cleaned[..63] : cleaned;
    }
}
