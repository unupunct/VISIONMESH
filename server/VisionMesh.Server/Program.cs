using System.Globalization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;
using VisionMesh.Api;
using VisionMesh.Api.Endpoints;
using VisionMesh.Server;

// VisionMesh Server: the whole platform in one process.
//
// It hosts the REST API, the dashboard, the agent WebSocket, the stream gateway, the recorder
// and the integrations. One process is a deliberate choice for a self-hosted product: there is
// one thing to install, one thing to restart, one log to read, and no inter-service networking to
// misconfigure on someone's home network.

var options = ServerOptions.Parse(args);

if (options.ShowHelp)
{
    ServerOptions.PrintHelp();
    return 0;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Run correctly under both service managers without the user configuring anything.
builder.Host.UseWindowsService(service => service.ServiceName = "VisionMesh");
builder.Host.UseSystemd();

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "HH:mm:ss ";
});
if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService()) builder.Logging.AddEventLog();

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenAnyIP(options.Port);

    // A long-lived MJPEG stream must not be killed by a request timeout, and an agent pushing
    // frames for hours is not a slow client.
    kestrel.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
    kestrel.Limits.MinResponseDataRate = null;
    kestrel.Limits.MinRequestBodyDataRate = null;
    kestrel.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
    kestrel.AddServerHeader = false;
});

builder.Services.AddVisionMesh(options.DataDirectory);

builder.Services.AddSwaggerGen(swagger =>
{
    swagger.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "VisionMesh API",
        Version = BuildInfo.Version,
        Description = "Self-hosted camera and surveillance platform. "
                    + "Authenticate with POST /api/auth/login, then send the token as a bearer token or rely on the session cookie.",
        License = new Microsoft.OpenApi.Models.OpenApiLicense { Name = "MIT", Url = new Uri("https://github.com/unupunct/VISIONMESH/blob/main/LICENSE") },
    });

    swagger.AddSecurityDefinition("bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        Description = "Session token from POST /api/auth/login.",
    });
});

builder.Services.AddHostedService<MdnsAdvertiser>();

// Behind a reverse proxy, the real client address matters for audit entries and rate limiting.
builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    forwarded.KnownNetworks.Clear();
    forwarded.KnownProxies.Clear();
});

var app = builder.Build();

try
{
    app.Services.MigrateVisionMeshDatabase();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"VisionMesh could not open its database: {ex.Message}");
    Console.Error.WriteLine($"Data directory: {options.DataDirectory}");
    return 1;
}

app.Services.GetRequiredService<NetworkInfoService>().Port = options.Port;

app.UseForwardedHeaders();

app.UseWebSockets(new WebSocketOptions
{
    // Keeps intermediaries from dropping an idle agent connection that is only pinged every 15s.
    KeepAliveInterval = TimeSpan.FromSeconds(30),
});

app.Use(async (context, next) =>
{
    // A self-hosted surveillance dashboard should never be framed by another site, and its pages
    // must not be sniffed into another content type.
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    await next();
});

if (options.EnableSwagger)
{
    app.UseSwagger();
    app.UseSwaggerUI(ui =>
    {
        ui.SwaggerEndpoint("/swagger/v1/swagger.json", "VisionMesh API");
        ui.RoutePrefix = "api/docs";
        ui.DocumentTitle = "VisionMesh API";
    });
}

// Dashboard static files. In a published build they sit in wwwroot beside the executable.
var webRoot = ResolveWebRoot();
if (webRoot is not null)
{
    var provider = new PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = provider,
        OnPrepareResponse = context =>
        {
            // The dashboard is small and changes with every release; caching it aggressively
            // mostly produces stale UIs after an upgrade.
            context.Context.Response.Headers.CacheControl = "no-cache";
        },
    });
}
else
{
    app.Logger.LogWarning("The dashboard files were not found, so only the API is being served.");
}

app.MapVisionMesh();

// Anything not matched by the API or a static file is the single-page dashboard.
app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new { error = "No such API endpoint." });
        return;
    }

    if (webRoot is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync("The VisionMesh dashboard files are not installed.");
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(Path.Combine(webRoot, "index.html"));
});

PrintBanner(app, options);

try
{
    await app.RunAsync();
    return 0;
}
catch (IOException ex) when (ex.InnerException is System.Net.Sockets.SocketException)
{
    Console.Error.WriteLine($"VisionMesh could not listen on port {options.Port}: {ex.Message}");
    Console.Error.WriteLine("Another program may already be using that port. Start VisionMesh with --port to choose a different one.");
    return 1;
}

static string? ResolveWebRoot()
{
    // Environment.ProcessPath rather than Assembly.Location, which is empty in a single-file publish.
    var appDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    var candidates = new[]
    {
        Path.Combine(appDirectory, "wwwroot"),
        Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        // Running from the repository during development.
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "web", "dashboard")),
    };

    return candidates.FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, "index.html")));
}

void PrintBanner(WebApplication application, ServerOptions serverOptions)
{
    var network = application.Services.GetRequiredService<NetworkInfoService>();
    var urls = network.GetDashboardUrls();

    application.Logger.LogInformation("VisionMesh Server {Version} starting.", BuildInfo.Version);
    application.Logger.LogInformation("Data directory: {Directory}", serverOptions.DataDirectory);

    foreach (var url in urls.Take(3)) application.Logger.LogInformation("Dashboard: {Url}", url);

    if (serverOptions.EnableSwagger && urls.FirstOrDefault() is { } first)
    {
        application.Logger.LogInformation("API documentation: {Url}/api/docs", first);
    }
}

/// <summary>Command line options. Everything has a working default so plain startup needs no arguments.</summary>
internal sealed class ServerOptions
{
    public const int DefaultPort = 8088;

    public int Port { get; private set; } = DefaultPort;
    public string DataDirectory { get; private set; } = DefaultDataDirectory();
    public bool EnableSwagger { get; private set; } = true;
    public bool ShowHelp { get; private set; }

    public static string DefaultDataDirectory() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VisionMesh")
        : "/var/lib/visionmesh";

    public static ServerOptions Parse(string[] args)
    {
        var options = new ServerOptions();

        // Environment variables first, so a container or systemd unit can configure the server
        // without rewriting its command line.
        if (Environment.GetEnvironmentVariable("VISIONMESH_PORT") is { } portText &&
            int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var envPort))
        {
            options.Port = envPort;
        }
        if (Environment.GetEnvironmentVariable("VISIONMESH_DATA") is { Length: > 0 } dataDirectory)
        {
            options.DataDirectory = dataDirectory;
        }

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--port" or "-p" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536)
                        options.Port = port;
                    break;

                case "--data" or "-d" when i + 1 < args.Length:
                    options.DataDirectory = args[++i];
                    break;

                case "--no-api-docs":
                    options.EnableSwagger = false;
                    break;

                case "--help" or "-h" or "-?":
                    options.ShowHelp = true;
                    break;
            }
        }

        options.DataDirectory = Path.GetFullPath(options.DataDirectory);
        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine($"""
            VisionMesh Server {BuildInfo.Version}
            Self-hosted camera and surveillance platform.

            Usage:
              VisionMesh.Server [options]

            Options:
              -p, --port <number>    Port for the dashboard and API (default {DefaultPort}).
              -d, --data <folder>    Where the database and keys are kept
                                     (default {DefaultDataDirectory()}).
                  --no-api-docs      Do not serve the API documentation at /api/docs.
              -h, --help             Show this help.

            Environment variables:
              VISIONMESH_PORT        Same as --port.
              VISIONMESH_DATA        Same as --data.

            Once running, open the dashboard in a browser and follow the setup wizard.
            """);
    }
}
