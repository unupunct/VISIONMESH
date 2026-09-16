using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace VisionMesh.Streaming.Sources;

/// <summary>What ffmpeg will be asked to use for decoding, and how that was decided.</summary>
/// <param name="Name">The ffmpeg <c>-hwaccel</c> value, or null for plain software decoding.</param>
/// <param name="Device">The device argument some methods need, such as a VAAPI render node.</param>
/// <param name="Detail">A sentence for the settings page explaining what was found.</param>
public sealed record HardwareDecoder(string? Name, string? Device, string Detail)
{
    public static readonly HardwareDecoder Software =
        new(null, null, "Decoding in software. No usable hardware decoder was found.");

    public bool Available => Name is not null;
}

/// <summary>
/// Finds a hardware decoder that actually works on this machine.
///
/// Only the decode is offloaded. Recording copies the camera's stream and decodes nothing at all,
/// so there is nothing there to accelerate; the decode exists solely to produce the MJPEG a
/// browser can display, and on an H.264 camera that decode is the larger half of the cost.
///
/// Nothing here trusts a capability list. `ffmpeg -hwaccels` cheerfully reports methods that fail
/// the moment they are used - no render node, a driver that is not installed, a container without
/// the device passed through - and a camera that dies on start is far worse than one that uses
/// more processor than it might. Each candidate is therefore proved by decoding a real H.264
/// sample with it, once, and only a method that survives that is ever put in front of a camera.
/// </summary>
public sealed class HardwareAcceleration(ILogger<HardwareAcceleration> log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HardwareDecoder? _cached;

    /// <summary>
    /// Candidates in the order they are worth trying, each with the device argument it needs.
    ///
    /// VAAPI first because it is what a Linux mini PC or an Intel N100 box actually has, which is
    /// the machine this software is most often asked to run on.
    /// </summary>
    private static readonly (string Name, string? Device)[] Candidates =
    [
        ("vaapi", "/dev/dri/renderD128"),
        ("qsv", null),
        ("cuda", null),
        ("d3d11va", null),
        ("videotoolbox", null),
    ];

    public async Task<HardwareDecoder> DetectAsync(string ffmpegPath, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _cached is { } cached) return cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _cached is { } existing) return existing;

            var result = await ProbeAsync(ffmpegPath, cancellationToken).ConfigureAwait(false);
            _cached = result;

            if (result.Available) log.LogInformation("Using {Method} for hardware decoding.", result.Name);
            else log.LogInformation("{Detail}", result.Detail);

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HardwareDecoder> ProbeAsync(string ffmpegPath, CancellationToken cancellationToken)
    {
        var supported = await ListHwaccelsAsync(ffmpegPath, cancellationToken).ConfigureAwait(false);
        if (supported.Count == 0) return HardwareDecoder.Software;

        var sample = await CreateSampleAsync(ffmpegPath, cancellationToken).ConfigureAwait(false);
        if (sample is null)
        {
            return HardwareDecoder.Software with
            {
                Detail = "Decoding in software. A test clip could not be produced, so no hardware decoder was trusted.",
            };
        }

        try
        {
            foreach (var (name, device) in Candidates)
            {
                if (!supported.Contains(name)) continue;
                if (device is not null && !File.Exists(device))
                {
                    log.LogDebug("Skipping {Method}: {Device} does not exist.", name, device);
                    continue;
                }

                if (await CanDecodeAsync(ffmpegPath, name, device, sample, cancellationToken).ConfigureAwait(false))
                {
                    return new HardwareDecoder(name, device, $"Decoding with {name}, which was tested on this machine and worked.");
                }

                log.LogDebug("{Method} is advertised but could not decode a test clip; not using it.", name);
            }
        }
        finally
        {
            try { File.Delete(sample); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        return HardwareDecoder.Software with
        {
            Detail = supported.Count > 0
                ? $"Decoding in software. ffmpeg offers {string.Join(", ", supported)}, but none of them could decode a test clip here."
                : HardwareDecoder.Software.Detail,
        };
    }

    private async Task<HashSet<string>> ListHwaccelsAsync(string ffmpegPath, CancellationToken cancellationToken)
    {
        var output = await RunAsync(ffmpegPath, ["-hide_banner", "-hwaccels"], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        if (output is null) return [];

        // The output is a heading followed by one method per line.
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.EndsWith(':') && !line.Contains(' '))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Writes a few frames of real H.264, which is what a camera will actually send.</summary>
    private async Task<string?> CreateSampleAsync(string ffmpegPath, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"visionmesh-hwprobe-{Guid.NewGuid():N}.h264");

        var written = await RunAsync(ffmpegPath, [
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=10",
            "-frames:v", "20", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-y", path,
        ], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

        if (written is not null && File.Exists(path) && new FileInfo(path).Length > 0) return path;

        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private async Task<bool> CanDecodeAsync(string ffmpegPath, string method, string? device, string sample, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-hwaccel", method };
        if (device is not null) { arguments.Add("-hwaccel_device"); arguments.Add(device); }

        // Decoding straight to null proves the decode path without dragging an encoder into the
        // question, and the frames are downloaded to system memory exactly as the real command
        // line will need them for the MJPEG output.
        arguments.AddRange(["-i", sample, "-frames:v", "10", "-f", "null", "-"]);

        return await RunAsync(ffmpegPath, arguments, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>Runs ffmpeg and returns its output, or null when it fails, times out or is missing.</summary>
    private async Task<string?> RunAsync(string ffmpegPath, IEnumerable<string> arguments, TimeSpan limit, CancellationToken cancellationToken)
    {
        try
        {
            var info = new ProcessStartInfo(ffmpegPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = info };
            if (!process.Start()) return null;

            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(limit);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return null;
            }

            if (process.ExitCode != 0) return null;
            return await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
