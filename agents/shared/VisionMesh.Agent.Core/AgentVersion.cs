using System.Reflection;

namespace VisionMesh.Agent.Core;

/// <summary>
/// The version an agent reports to the server, taken from the assembly rather than written out
/// by hand.
///
/// It was a hand-written constant, which drifted: a 1.0.1 build introduced itself as 1.0.0, so
/// the Devices page — the one place a user looks to check whether an agent needs updating —
/// showed a version that was not the one running. A stale version there is worse than none,
/// because it is believed.
/// </summary>
public static class AgentVersion
{
    /// <summary>The running assembly's version, without the build metadata suffix.</summary>
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var informational = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // "1.0.1+412f382..." — the commit is useful in a log, but not as a version.
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";
    }
}
