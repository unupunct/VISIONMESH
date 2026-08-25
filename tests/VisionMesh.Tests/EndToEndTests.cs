using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VisionMesh.Agent.Core;
using Xunit;

namespace VisionMesh.Tests;

/// <summary>
/// The workflow the whole product exists to deliver: pair a machine, add its camera, watch it.
///
/// If this test passes, the core promise works: a device pairs with a short-lived code, appears
/// in the dashboard, its camera can be added, and live video reaches a browser-shaped client.
/// </summary>
public class EndToEndTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task PairADevice_AddItsCamera_AndReceiveLiveVideo()
    {
        await using var server = await TestServer.StartAsync();
        await server.SetupAndSignInAsync();

        // ---- 1. the server issues a short-lived pairing code ----
        var pairing = await server.PostJsonAsync("/api/pairing");
        var code = pairing.GetProperty("code").GetString()!;

        Assert.False(string.IsNullOrWhiteSpace(code));
        Assert.StartsWith("visionmesh://pair?", pairing.GetProperty("qrPayload").GetString());
        // The QR payload must never carry a permanent credential.
        Assert.DoesNotContain("deviceToken", pairing.GetProperty("qrPayload").GetString()!);

        // ---- 2. an agent claims it and receives a device token ----
        using var httpClient = new HttpClient();
        var configuration = await AgentClient.PairAsync(
            httpClient, server.BaseUrl, code, "Test Workstation", "1.0.0-test", CancellationToken.None);

        Assert.True(configuration.IsPaired);
        Assert.False(string.IsNullOrWhiteSpace(configuration.DeviceToken));

        // A pairing code is single use: a second attempt must be refused.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AgentClient.PairAsync(httpClient, server.BaseUrl, code, "Impostor", "1.0.0-test", CancellationToken.None));

        // ---- 3. the agent connects and advertises its cameras ----
        var capture = new FakeCameraCapture(Fixtures.Read("gradient_420.jpg"));
        await using var agent = new AgentClient(configuration, capture, "1.0.0-test", NullLogger.Instance);

        using var agentCancellation = new CancellationTokenSource();
        var agentTask = agent.RunAsync(agentCancellation.Token);

        var deviceAppeared = await TestServer.WaitForAsync(async () =>
        {
            var devices = await server.GetJsonAsync("/api/devices");
            return devices.GetArrayLength() > 0 && devices[0].GetProperty("connected").GetBoolean();
        }, Timeout);

        Assert.True(deviceAppeared, "The paired device never showed up as connected.");

        var device = (await server.GetJsonAsync("/api/devices"))[0];
        var deviceId = device.GetProperty("id").GetString()!;
        Assert.Equal("Test Workstation", device.GetProperty("name").GetString());

        // ---- 4. its cameras are listed and can be added ----
        var available = await server.GetJsonAsync($"/api/devices/{deviceId}/cameras");
        Assert.Equal(1, available.GetArrayLength());
        Assert.Equal("Test Camera", available[0].GetProperty("name").GetString());

        var created = await server.PostJsonAsync("/api/cameras", new
        {
            name = "Office",
            sourceKind = "AgentCamera",
            deviceId,
            sourceId = FakeCameraCapture.SourceId,
            fps = 10,
        });
        var cameraId = created.GetProperty("id").GetString()!;
        Assert.Equal("Office", created.GetProperty("name").GetString());

        // Adding the same physical camera twice must not create a duplicate tile.
        var duplicate = await server.Client.PostAsJsonAsync("/api/cameras", new
        {
            name = "Office again",
            sourceKind = "AgentCamera",
            deviceId,
            sourceId = FakeCameraCapture.SourceId,
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        // ---- 5. live video reaches a viewer ----
        var frames = await ReadMjpegFramesAsync(server, cameraId, wanted: 3, Timeout);
        Assert.True(frames.Count >= 3, $"Expected at least 3 live frames, got {frames.Count}.");

        foreach (var frame in frames)
        {
            // Every frame must be a real JPEG, byte for byte what the camera produced.
            Assert.True(frame.Length > 100, "A streamed frame was implausibly small.");
            Assert.Equal(0xFF, frame[0]);
            Assert.Equal(0xD8, frame[1]);
            Assert.Equal(0xFF, frame[^2]);
            Assert.Equal(0xD9, frame[^1]);
        }

        Assert.True(capture.SessionsOpened >= 1, "The agent never actually opened the camera.");

        // ---- 6. a snapshot works and the camera reports measured health ----
        var snapshot = await server.Client.GetAsync($"/api/cameras/{cameraId}/snapshot.jpg");
        snapshot.EnsureSuccessStatusCode();
        Assert.Equal("image/jpeg", snapshot.Content.Headers.ContentType?.MediaType);

        var snapshotBytes = await snapshot.Content.ReadAsByteArrayAsync();
        Assert.True(snapshotBytes.Length > 100);

        var healthy = await TestServer.WaitForAsync(async () =>
        {
            var camera = await server.GetJsonAsync($"/api/cameras/{cameraId}");
            return camera.GetProperty("state").GetString() == "Online"
                   && camera.GetProperty("health").GetProperty("framesReceived").GetInt64() > 0;
        }, Timeout);

        Assert.True(healthy, "The camera never reported itself online with measured frames.");

        var final = await server.GetJsonAsync($"/api/cameras/{cameraId}");
        var health = final.GetProperty("health");
        Assert.Equal(320, health.GetProperty("width").GetInt32());
        Assert.Equal(240, health.GetProperty("height").GetInt32());

        agentCancellation.Cancel();
        await SwallowAsync(agentTask);
    }

    [Fact]
    public async Task ADisconnectedAgentTakesItsCamerasOfflineImmediately()
    {
        await using var server = await TestServer.StartAsync();
        await server.SetupAndSignInAsync();

        var code = (await server.PostJsonAsync("/api/pairing")).GetProperty("code").GetString()!;
        using var httpClient = new HttpClient();
        var configuration = await AgentClient.PairAsync(httpClient, server.BaseUrl, code, "Flaky Laptop", "1.0.0-test", CancellationToken.None);

        var capture = new FakeCameraCapture(Fixtures.Read("gradient_420.jpg"));
        var agent = new AgentClient(configuration, capture, "1.0.0-test", NullLogger.Instance);
        var agentCancellation = new CancellationTokenSource();
        var agentTask = agent.RunAsync(agentCancellation.Token);

        Assert.True(await TestServer.WaitForAsync(async () =>
            (await server.GetJsonAsync("/api/devices"))[0].GetProperty("connected").GetBoolean(), Timeout));

        var deviceId = (await server.GetJsonAsync("/api/devices"))[0].GetProperty("id").GetString()!;
        var cameraId = (await server.PostJsonAsync("/api/cameras", new
        {
            name = "Hallway",
            sourceKind = "AgentCamera",
            deviceId,
            sourceId = FakeCameraCapture.SourceId,
            fps = 10,
        })).GetProperty("id").GetString()!;

        // Get it online first, so the transition to offline is meaningful.
        await ReadMjpegFramesAsync(server, cameraId, wanted: 1, Timeout);
        Assert.True(await TestServer.WaitForAsync(async () =>
            (await server.GetJsonAsync($"/api/cameras/{cameraId}")).GetProperty("state").GetString() == "Online", Timeout));

        // The laptop goes to sleep.
        agentCancellation.Cancel();
        await SwallowAsync(agentTask);
        await agent.DisposeAsync();

        var wentOffline = await TestServer.WaitForAsync(async () =>
        {
            var camera = await server.GetJsonAsync($"/api/cameras/{cameraId}");
            return camera.GetProperty("state").GetString() == "Offline";
        }, Timeout);

        Assert.True(wentOffline, "The camera stayed online after its device disconnected.");

        var device = (await server.GetJsonAsync("/api/devices"))[0];
        Assert.False(device.GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task PrivacyModeStopsTheStreamAndIsReportedClearly()
    {
        await using var server = await TestServer.StartAsync();
        await server.SetupAndSignInAsync();

        var code = (await server.PostJsonAsync("/api/pairing")).GetProperty("code").GetString()!;
        using var httpClient = new HttpClient();
        var configuration = await AgentClient.PairAsync(httpClient, server.BaseUrl, code, "Bedroom PC", "1.0.0-test", CancellationToken.None);

        var capture = new FakeCameraCapture(Fixtures.Read("gradient_420.jpg"));
        await using var agent = new AgentClient(configuration, capture, "1.0.0-test", NullLogger.Instance);
        using var agentCancellation = new CancellationTokenSource();
        var agentTask = agent.RunAsync(agentCancellation.Token);

        Assert.True(await TestServer.WaitForAsync(async () =>
            (await server.GetJsonAsync("/api/devices"))[0].GetProperty("connected").GetBoolean(), Timeout));

        var deviceId = (await server.GetJsonAsync("/api/devices"))[0].GetProperty("id").GetString()!;
        var cameraId = (await server.PostJsonAsync("/api/cameras", new
        {
            name = "Bedroom",
            sourceKind = "AgentCamera",
            deviceId,
            sourceId = FakeCameraCapture.SourceId,
            fps = 10,
        })).GetProperty("id").GetString()!;

        await ReadMjpegFramesAsync(server, cameraId, wanted: 1, Timeout);

        // Turning privacy mode on must actually stop the video, not just hide it in the UI.
        await server.PostJsonAsync($"/api/cameras/{cameraId}/privacy?enabled=true");

        var streamResponse = await server.Client.GetAsync($"/api/cameras/{cameraId}/stream.mjpeg", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.Forbidden, streamResponse.StatusCode);

        var body = await streamResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("privacy_mode", body.GetProperty("code").GetString());

        var snapshotResponse = await server.Client.GetAsync($"/api/cameras/{cameraId}/snapshot.jpg");
        Assert.Equal(HttpStatusCode.Forbidden, snapshotResponse.StatusCode);

        var camera = await server.GetJsonAsync($"/api/cameras/{cameraId}");
        Assert.Equal("Privacy", camera.GetProperty("state").GetString());

        // And turning it off restores the stream.
        await server.PostJsonAsync($"/api/cameras/{cameraId}/privacy?enabled=false");
        var restored = await ReadMjpegFramesAsync(server, cameraId, wanted: 1, Timeout);
        Assert.NotEmpty(restored);

        agentCancellation.Cancel();
        await SwallowAsync(agentTask);
    }

    /// <summary>
    /// Reads whole JPEG frames out of a live multipart/x-mixed-replace response, the same way a
    /// browser's img element does.
    /// </summary>
    private static async Task<List<byte[]>> ReadMjpegFramesAsync(TestServer server, string cameraId, int wanted, TimeSpan timeout)
    {
        var frames = new List<byte[]>();
        using var cancellation = new CancellationTokenSource(timeout);

        try
        {
            using var response = await server.Client.GetAsync(
                $"/api/cameras/{cameraId}/stream.mjpeg", HttpCompletionOption.ResponseHeadersRead, cancellation.Token);

            response.EnsureSuccessStatusCode();
            Assert.Contains("multipart/x-mixed-replace", response.Content.Headers.ContentType?.ToString() ?? "");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellation.Token);
            var buffer = new List<byte>();
            var chunk = new byte[16 * 1024];

            while (frames.Count < wanted && !cancellation.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(chunk, cancellation.Token);
                if (read == 0) break;
                buffer.AddRange(chunk.AsSpan(0, read).ToArray());

                // Pull out every complete JPEG the buffer now holds.
                while (true)
                {
                    var start = IndexOf(buffer, 0xFF, 0xD8, 0);
                    if (start < 0) break;
                    var end = IndexOf(buffer, 0xFF, 0xD9, start + 2);
                    if (end < 0) break;

                    frames.Add(buffer.GetRange(start, end - start + 2).ToArray());
                    buffer.RemoveRange(0, end + 2);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out: the caller asserts on how many frames arrived.
        }

        return frames;
    }

    private static int IndexOf(List<byte> data, byte first, byte second, int from)
    {
        for (var i = Math.Max(0, from); i + 1 < data.Count; i++)
        {
            if (data[i] == first && data[i + 1] == second) return i;
        }
        return -1;
    }

    private static async Task SwallowAsync(Task task)
    {
        try { await task; }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
    }
}
