using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using VisionMesh.Agent.Core;
using VisionMesh.Agent.Windows.Capture;

namespace VisionMesh.Agent.Windows;

/// <summary>
/// The VisionMesh Windows camera agent: turns this machine's webcams into VisionMesh cameras.
///
/// It is a plain console application on purpose. It runs happily in a terminal while someone is
/// setting it up, and the same executable installs as a Windows service for unattended use, with
/// no separate build and no tray application to go wrong.
/// </summary>
[SupportedOSPlatform("windows")]
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
                console.TimestampFormat = "HH:mm:ss ";
            });
            builder.SetMinimumLevel(args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Information);
        });

        var log = loggerFactory.CreateLogger("VisionMesh.Agent");
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

    private static async Task<int> RunCommandAsync(string[] args, string configurationPath, ILogger log)
    {
        switch (args[0].ToLowerInvariant())
        {
            case "pair":
                return await PairAsync(args, configurationPath, log);

            case "list":
                return ListCameras(log);

            case "status":
                return ShowStatus(configurationPath);

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
            Console.WriteLine("Start the agent with:  VisionMesh.Agent.Windows");
            Console.WriteLine("Then add this computer's cameras from the server's Add Camera screen.");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or ArgumentException or TaskCanceledException)
        {
            log.LogError("Pairing failed: {Message}", ex.Message);
            return 1;
        }
    }

    private static int ListCameras(ILogger log)
    {
        using var capture = new WindowsCameraCapture(log);
        var cameras = capture.Enumerate();

        if (cameras.Count == 0)
        {
            Console.WriteLine("No cameras were found on this computer.");
            Console.WriteLine();
            Console.WriteLine("Check that the camera is plugged in, and that Settings, Privacy and security,");
            Console.WriteLine("Camera allows desktop apps to use the camera.");
            return 0;
        }

        Console.WriteLine($"CAMERAS FOUND ({cameras.Count})");
        Console.WriteLine();

        foreach (var camera in cameras)
        {
            Console.WriteLine($"  {camera.Name}");

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
            Console.WriteLine($"    Device path: {Shorten(camera.SourceId)}");
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
            Console.WriteLine("Pair it with:  VisionMesh.Agent.Windows pair");
            return 0;
        }

        Console.WriteLine($"Server:       {configuration.ServerUrl}");
        Console.WriteLine($"Server name:  {configuration.ServerName ?? "unknown"}");
        Console.WriteLine($"This device:  {configuration.DeviceName} ({configuration.DeviceId})");
        Console.WriteLine($"Paired:       {configuration.PairedUtc?.ToLocalTime():g}");
        Console.WriteLine($"Settings:     {configurationPath}");
        return 0;
    }

    private static async Task<int> RunAgentAsync(string configurationPath, ILogger log)
    {
        var configuration = AgentConfiguration.Load(configurationPath);

        if (!configuration.IsPaired)
        {
            Console.WriteLine("This computer is not paired with a VisionMesh server yet.");
            Console.WriteLine();
            Console.WriteLine("On the server, open Devices and press Add Device to get a pairing code, then run:");
            Console.WriteLine();
            Console.WriteLine("    VisionMesh.Agent.Windows pair");
            Console.WriteLine();
            return 1;
        }

        using var capture = new WindowsCameraCapture(log);

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
            e.Cancel = true;   // shut down cleanly rather than being killed mid-frame
            log.LogInformation("Stopping.");
            shutdown.Cancel();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

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

    private static string Shorten(string value) => value.Length <= 64 ? value : value[..61] + "...";

    private static void PrintHelp() => Console.WriteLine($"""
        VisionMesh Windows Camera Agent {Version}
        Turns this computer's cameras into cameras on your VisionMesh server.

        Usage:
          VisionMesh.Agent.Windows                 Run the agent (this is the normal use).
          VisionMesh.Agent.Windows pair            Pair this computer with a server.
          VisionMesh.Agent.Windows list            Show the cameras attached to this computer.
          VisionMesh.Agent.Windows status          Show which server this computer is paired with.
          VisionMesh.Agent.Windows unpair          Forget the server and delete the stored token.

        Options:
          --server <url>     Server address, for pair.
          --code <code>      Pairing code, for pair.
          --name <name>      Name to show in the dashboard (default: this computer's name).
          --config <file>    Use a different settings file.
          --verbose          Log more detail.

        To run the agent automatically in the background, install it as a Windows service:
          sc.exe create VisionMeshAgent binPath= "<full path to this exe>" start= auto
          sc.exe start VisionMeshAgent
        """);
}
