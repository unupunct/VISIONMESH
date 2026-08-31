using System.Reflection;
using VisionMesh.Agent.Core;
using Xunit;

namespace VisionMesh.Tests;

/// <summary>
/// The version an agent introduces itself with.
///
/// This exists because it was a hand-written constant and it drifted: the 1.0.1 agent told the
/// server it was 1.0.0, so the Devices page — the one place a user looks to decide whether an
/// agent needs updating — showed a version that was not the one running. Found by pairing a
/// released agent with a released server.
/// </summary>
public class AgentVersionTests
{
    [Fact]
    public void TheReportedVersionComesFromTheAssembly()
    {
        var expected = Assembly.GetEntryAssembly()!
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        var plus = expected.IndexOf('+');
        if (plus > 0) expected = expected[..plus];

        Assert.Equal(expected, AgentVersion.Current);
    }

    [Fact]
    public void TheCommitSuffixIsNotPartOfTheVersion()
    {
        // Informational versions carry "+<sha>". That belongs in a log, not in a column a user
        // reads to compare two version numbers.
        Assert.DoesNotContain('+', AgentVersion.Current);
        Assert.NotEmpty(AgentVersion.Current);
    }

    [Fact]
    public void NoAgentHardCodesItsOwnVersion()
    {
        // The failure mode was a literal that nobody remembered to bump. If one comes back, this
        // catches it at build time rather than at the next release.
        var repository = FindRepositoryRoot();

        foreach (var program in new[]
                 {
                     Path.Combine(repository, "agents", "windows", "VisionMesh.Agent.Windows", "Program.cs"),
                     Path.Combine(repository, "agents", "linux", "VisionMesh.Agent.Linux", "Program.cs"),
                 })
        {
            Assert.True(File.Exists(program), $"{program} is missing.");

            var source = File.ReadAllText(program);
            Assert.DoesNotContain("const string Version", source);
            Assert.Contains("AgentVersion.Current", source);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VisionMesh.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
