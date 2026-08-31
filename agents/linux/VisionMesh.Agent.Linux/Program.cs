using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using VisionMesh.Agent.Core;
using VisionMesh.Agent.Linux.Capture;

namespace VisionMesh.Agent.Linux;

/// <summary>
/// The VisionMesh Linux camera agent: turns this machine's cameras into VisionMesh cameras.
///
/// The same executable runs in a terminal while somebody sets it up and under systemd afterwards,
/// so there is one thing to install and one thing to debug.
/// </summary>
public static class Program
{
    private static string Version => AgentVersion.Current;

    public static async Task<int> Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSimpleConsole(console =>
            {
                console.SingleLine = true;
                // systemd already timestamps the journal, so a second timestamp is noise there.
                console.TimestampFormat = Environment.GetEnvironmentVariable("INVOCATION_ID") is null ? "HH:mm:ss " : null;
            });
            builder.SetMinimumLevel(args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Information);
        });

        var log = loggerFactory.CreateLogger("VisionMesh.Agent");

        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("This is the Linux agent. Use VisionMesh.Agent.Windows on Windows.");
            return 1;
        }

        var configurationPath = GetArgument(args, "--config") ?? AgentConfiguration.DefaultPath();

        try
        {
            return args.Length > 0 && !args[0].StartsWith('-')
                ? await RunCommandAsync(args, configurationPath, log)
                : await RunAgentAsync(configurationPath, log);
        }
        catch (CameraCaptureException ex)
        {
            log.LogError("{Message}", ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "The agent stopped unexpectedly.");
            return 1;
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task<int> RunCommandAsync(string[] args, string configurationPath, ILogger log)
    {
        switch (args[0].ToLowerInvariant())
        {
            case "pair": return await PairAsync(args, configurationPath, log);
            case "list": return ListCameras(log);
            case "status": return ShowStatus(configurationPath);

            case "unpair":
                AgentConfiguration.Delete(configurationPath);
                Console.WriteLine("This computer is no longer paired with a VisionMesh server.");
                Console.WriteLine("Remove the device from the server's Devices page as well.");
                return 0;

            case "help":
            case "--help":
                PrintHelp();
                return 0;

            default:
                Console.Error.WriteLine($"'{args[0]}' is not a VisionMesh agent command.");
                PrintHelp();
                return 1;
        }
    }

    private static async Task<int> PairAsync(string[] args, string configurationPath, ILogger log)
    {
        var server = GetArgument(args, "--server") ?? Prompt("VisionMesh server address (for example http://192.168.1.10:8088): ");
        var code = GetArgument(args, "--code") ?? Prompt("Pairing code shown on the server: ");
        var name = GetArgument(args, "--name") ?? Environment.MachineName;

        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(code))
        {
            Console.Error.WriteLine("Both a server address and a pairing code are needed.");
            return 1;
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        try
        {
            var configuration = await AgentClient.PairAsync(httpClient, server, code, name, Version, CancellationToken.None);
            configuration.Save(configurationPath);

            Console.WriteLine();
            Console.WriteLine($"Paired with '{configuration.ServerName ?? "VisionMesh"}' as '{name}'.");
            Console.WriteLine($"Settings saved to {configurationPath}");
            Console.WriteLine();
            Console.WriteLine("Start the agent with:  visionmesh-agent");
            Console.WriteLine("Or run it in the background:  sudo systemctl enable --now visionmesh-agent");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or ArgumentException or TaskCanceledException)
        {
            log.LogError("Pairing failed: {Message}", ex.Message);
            return 1;
        }
    }

    [SupportedOSPlatform("linux")]
    private static int ListCameras(ILogger log)
    {
        var capture = new LinuxCameraCapture(log);
        var cameras = capture.Enumerate();

        if (cameras.Count == 0)
        {
            Console.WriteLine("No cameras were found on this computer.");
            Console.WriteLine();
            Console.WriteLine("Check that the camera is plugged in, and that the user running this agent");
            Console.WriteLine("is in the 'video' group:");
            Console.WriteLine();
            Console.WriteLine($"    sudo usermod -aG video {Environment.UserName}");
            Console.WriteLine();
            Console.WriteLine("Then sign out and back in for the change to take effect.");
            return 0;
        }

        Console.WriteLine($"CAMERAS FOUND ({cameras.Count})");
        Console.WriteLine();

        foreach (var camera in cameras)
        {
            Console.WriteLine($"  {camera.Name}");
            Console.WriteLine($"    {camera.SourceId}");

            if (!camera.Available)
            {
                Console.WriteLine($"    Unavailable: {camera.Unavailable}");
                Console.WriteLine();
                continue;
            }

            var best = camera.Formats.FirstOrDefault();
            if (best is not null)
            {
                Console.WriteLine($"    {best.Width} x {best.Height} at {best.Fps:0.#} fps, {best.Format}"
                                + (best.NativeJpeg ? " (forwarded without re-encoding)" : ""));
            }

            if (camera.Formats.Count > 1) Console.WriteLine($"    {camera.Formats.Count} formats available");
            if (!string.IsNullOrWhiteSpace(camera.Description)) Console.WriteLine($"    {camera.Description}");
            Console.WriteLine();
        }

        return 0;
    }

    private static int ShowStatus(string configurationPath)
    {
        var configuration = AgentConfiguration.Load(configurationPath);

        if (!configuration.IsPaired)
        {
            Console.WriteLine("This computer is not paired with a VisionMesh server.");
            Console.WriteLine();
            Console.WriteLine("Pair it with:  visionmesh-agent pair");
            return 0;
        }

        Console.WriteLine($"Server:       {configuration.ServerUrl}");
        Console.WriteLine($"Server name:  {configuration.ServerName ?? "unknown"}");
        Console.WriteLine($"This device:  {configuration.DeviceName} ({configuration.DeviceId})");
        Console.WriteLine($"Paired:       {configuration.PairedUtc?.ToLocalTime():g}");
        Console.WriteLine($"Settings:     {configurationPath}");
        return 0;
    }

    [SupportedOSPlatform("linux")]
    private static async Task<int> RunAgentAsync(string configurationPath, ILogger log)
    {
        var configuration = AgentConfiguration.Load(configurationPath);

        if (!configuration.IsPaired)
        {
            Console.WriteLine("This computer is not paired with a VisionMesh server yet.");
            Console.WriteLine();
            Console.WriteLine("On the server, open Devices and press Add device to get a pairing code, then run:");
            Console.WriteLine();
            Console.WriteLine("    visionmesh-agent pair");
            Console.WriteLine();
            return 1;
        }

        var capture = new LinuxCameraCapture(log);

        var cameras = capture.Enumerate();
        log.LogInformation("Found {Count} camera(s) on this computer.", cameras.Count);
        foreach (var camera in cameras.Where(c => !c.Available))
        {
            log.LogWarning("Camera '{Name}' is not usable: {Reason}", camera.Name, camera.Unavailable);
        }

        await using var client = new AgentClient(configuration, capture, Version, log);

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            log.LogInformation("Stopping.");
            shutdown.Cancel();
        };

        // systemd sends SIGTERM on stop; without handling it the service is killed mid-frame.
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            log.LogInformation("Stopping on SIGTERM.");
            shutdown.Cancel();
        });

        await client.RunAsync(shutdown.Token);
        log.LogInformation("Agent stopped.");
        return 0;
    }

    private static string? GetArgument(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string Prompt(string message)
    {
        Console.Write(message);
        return Console.ReadLine()?.Trim() ?? "";
    }

    private static void PrintHelp() => Console.WriteLine($"""
        VisionMesh Linux Camera Agent {Version}
        Turns this computer's cameras into cameras on your VisionMesh server.

        Usage:
          visionmesh-agent                 Run the agent (this is the normal use).
          visionmesh-agent pair            Pair this computer with a server.
          visionmesh-agent list            Show the cameras attached to this computer.
          visionmesh-agent status          Show which server this computer is paired with.
          visionmesh-agent unpair          Forget the server and delete the stored token.

        Options:
          --server <url>     Server address, for pair.
          --code <code>      Pairing code, for pair.
          --name <name>      Name to show in the dashboard (default: this computer's name).
          --config <file>    Use a different settings file.
          --verbose          Log more detail.

        The agent needs permission to use the camera. If no cameras are found, add the
        user running it to the 'video' group:

            sudo usermod -aG video $USER

        Then sign out and back in.
        """);
}
