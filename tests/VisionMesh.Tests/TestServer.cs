using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisionMesh.Api;

namespace VisionMesh.Tests;

/// <summary>
/// A real VisionMesh server, on a real port, with a real SQLite database in a temporary folder.
///
/// The integration tests deliberately avoid an in-memory test host: the parts most worth testing
/// here are WebSocket framing, streaming responses and background services, and all three behave
/// differently without an actual socket underneath them.
/// </summary>
public sealed class TestServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _dataDirectory;

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public HttpClient Client { get; }

    private TestServer(WebApplication app, int port, string dataDirectory)
    {
        _app = app;
        Port = port;
        _dataDirectory = dataDirectory;

        Client = new HttpClient(new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() })
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public static async Task<TestServer> StartAsync()
    {
        var port = FindFreePort();
        var dataDirectory = Path.Combine(Path.GetTempPath(), "visionmesh-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenLocalhost(port);
            kestrel.Limits.MinResponseDataRate = null;
        });

        builder.Services.AddVisionMesh(dataDirectory);

        var app = builder.Build();
        app.Services.MigrateVisionMeshDatabase();
        app.Services.GetRequiredService<NetworkInfoService>().Port = port;
        app.UseWebSockets();
        app.MapVisionMesh();

        await app.StartAsync();
        return new TestServer(app, port, dataDirectory);
    }

    /// <summary>Runs first-run setup and signs the HTTP client in as the administrator.</summary>
    public async Task<string> SetupAndSignInAsync(string username = "admin", string password = "correct-horse-battery")
    {
        var response = await Client.PostAsJsonAsync("/api/setup", new
        {
            serverName = "Test Server",
            adminUsername = username,
            adminPassword = password,
            recordingsPath = Path.Combine(_dataDirectory, "recordings"),
            retentionDays = 1,
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString()!;

        Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return token;
    }

    public async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await Client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonElement> PostJsonAsync(string path, object? body = null)
    {
        var response = body is null
            ? await Client.PostAsync(path, null)
            : await Client.PostAsJsonAsync(path, body);

        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"POST {path} returned {(int)response.StatusCode}: {text}");

        return string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Polls until a condition holds, so tests never depend on fixed sleeps.</summary>
    public static async Task<bool> WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout, TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(interval);
        }
        return false;
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try { using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await _app.StopAsync(stopTimeout.Token); }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
        await _app.DisposeAsync();

        // SQLite may still hold the file briefly on Windows; a failure to clean up a temp folder
        // must not fail the test that just passed.
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
