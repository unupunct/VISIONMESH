using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace VisionMesh.Streaming.Sources;

/// <summary>Result of looking for an ffmpeg binary on this machine.</summary>
public sealed record FfmpegInfo(bool Available, string? Path, string? Version, string? FfprobePath)
{
    public static readonly FfmpegInfo NotFound = new(false, null, null, null);
}

/// <summary>
/// Finds ffmpeg, which VisionMesh needs for RTSP and ONVIF cameras and for writing recordings.
///
/// ffmpeg is deliberately not bundled: its licensing and per-distribution builds make shipping
/// it inside the installer a legal and packaging problem. Features that need it are disabled
/// and clearly labelled when it is missing, rather than failing at the moment a user tries them.
/// </summary>
public sealed partial class FfmpegLocator(ILogger<FfmpegLocator> log)
{
    private FfmpegInfo? _cached;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Locates ffmpeg, preferring an explicit configured path. The result is cached; pass
    /// <paramref name="forceRefresh"/> after the user installs ffmpeg or changes the setting.
    /// </summary>
    public async Task<FfmpegInfo> LocateAsync(string? configuredPath, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _cached is { } cached) return cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _cached is { } existing) return existing;
            var result = await ProbeAsync(configuredPath, cancellationToken).ConfigureAwait(false);
            _cached = result;

            if (result.Available) log.LogInformation("Using ffmpeg at {Path} ({Version}).", result.Path, result.Version);
            else log.LogWarning("ffmpeg was not found. RTSP, ONVIF and recording features are unavailable until it is installed.");

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<FfmpegInfo> ProbeAsync(string? configuredPath, CancellationToken cancellationToken)
    {
        foreach (var candidate in EnumerateCandidates(configuredPath))
        {
            var version = await TryGetVersionAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (version is null) continue;

            var probe = FindSibling(candidate, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            return new FfmpegInfo(true, candidate, version, probe);
        }
        return FfmpegInfo.NotFound;
    }

    private static IEnumerable<string> EnumerateCandidates(string? configuredPath)
    {
        var executable = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            // A configured path may point at the binary itself or at the folder holding it.
            yield return Directory.Exists(configuredPath) ? Path.Combine(configuredPath, executable) : configuredPath;
        }

        // Bundled alongside the server, which is how the Windows portable build ships it if the
        // user drops one in. Environment.ProcessPath is used because Assembly.Location is empty
        // in a single-file publish.
        var appDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        yield return Path.Combine(appDirectory, executable);
        yield return Path.Combine(appDirectory, "tools", executable);
        yield return Path.Combine(appDirectory, "ffmpeg", "bin", executable);

        // Bare name, resolved through PATH by the OS.
        yield return executable;

        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Path.Combine(programFiles, "ffmpeg", "bin", executable);
            yield return Path.Combine(@"C:\ffmpeg\bin", executable);
        }
        else
        {
            yield return "/usr/bin/ffmpeg";
            yield return "/usr/local/bin/ffmpeg";
            yield return "/snap/bin/ffmpeg";
            yield return "/var/lib/flatpak/exports/bin/ffmpeg";
        }
    }

    private static string? FindSibling(string ffmpegPath, string siblingName)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(ffmpegPath));
            if (string.IsNullOrEmpty(directory)) return null;
            var sibling = Path.Combine(directory, siblingName);
            return File.Exists(sibling) ? sibling : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task<string?> TryGetVersionAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(path, "-version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start()) return null;

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return null;
            }

            if (process.ExitCode != 0) return null;

            var output = await outputTask.ConfigureAwait(false);
            var match = VersionPattern().Match(output);
            return match.Success ? match.Groups[1].Value : "unknown";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Not present at this path, or not executable.
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) { }
    }

    [GeneratedRegex(@"^ffmpeg version (\S+)", RegexOptions.Multiline)]
    private static partial Regex VersionPattern();
}
