using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using VisionMesh.Api.Auth;
using VisionMesh.Core.Models;
using VisionMesh.Core.Util;
using VisionMesh.Database.Repositories;
using VisionMesh.Streaming.Ingest;

namespace VisionMesh.Api.Endpoints;

/// <summary>Registered machines and phones, and the pairing flow that adds them.</summary>
public static class DeviceEndpoints
{
    /// <summary>
    /// How long a pairing code stays valid. Long enough to walk to another room with a phone,
    /// short enough that a code left on screen is not a standing invitation.
    /// </summary>
    private static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(10);

    public static void MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var devices = app.MapGroup("/api/devices").WithTags("Devices");

        devices.MapGet("/", (CameraService service) => Results.Ok(service.GetDevices()))
            .RequireViewer()
            .WithName("ListDevices");

        devices.MapGet("/{id}", (string id, CameraService service) =>
        {
            var device = service.GetDevices().FirstOrDefault(d => d.Id == id);
            return device is null ? DeviceNotFound(id) : Results.Ok(device);
        })
        .RequireViewer()
        .WithName("GetDevice");

        devices.MapGet("/{id}/cameras", (string id, CameraService service, AgentRegistry agents) =>
        {
            if (!agents.IsOnline(id))
            {
                return Results.Json(
                    new { error = "That device is not connected, so its cameras cannot be listed.", code = "device_offline" },
                    statusCode: StatusCodes.Status409Conflict);
            }
            return Results.Ok(service.GetUnusedCaptureDevices(id));
        })
        .RequireViewer()
        .WithName("ListDeviceCameras")
        .WithSummary("Capture devices on a machine that are not already added as cameras.");

        devices.MapPost("/{id}/refresh", async (string id, AgentRegistry agents, CancellationToken cancellationToken) =>
        {
            var connection = agents.Find(id);
            if (connection is null) return Results.Json(new { error = "That device is not connected." }, statusCode: StatusCodes.Status409Conflict);

            await connection.RequestDeviceListAsync(cancellationToken);
            // The agent replies asynchronously; the dashboard picks the new list up over the
            // realtime channel rather than blocking this request on it.
            return Results.Accepted(value: new { ok = true });
        })
        .RequireOperator()
        .WithName("RefreshDeviceCameras");

        devices.MapPatch("/{id}", (HttpContext http, string id, DeviceRenameRequest request, DeviceRepository repository, AuthService auth) =>
        {
            var device = repository.GetById(id);
            if (device is null) return DeviceNotFound(id);
            if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { error = "Enter a name for the device." });

            device.Name = request.Name.Trim();
            repository.Update(device);
            auth.Audit(http.CurrentUser(), "device.rename", id, http.ClientAddress(), device.Name);
            return Results.Ok(new { ok = true });
        })
        .RequireAdministrator()
        .WithName("RenameDevice");

        devices.MapDelete("/{id}", (HttpContext http, string id, DeviceRepository repository, CameraRepository cameras, AuthService auth) =>
        {
            var device = repository.GetById(id);
            if (device is null) return DeviceNotFound(id);

            var affected = cameras.GetByDevice(id).Count;

            // Cameras cascade with the device row: a camera bound to a machine that is gone has
            // no source and would sit permanently offline in the dashboard.
            repository.Delete(id);
            auth.Audit(http.CurrentUser(), "device.delete", id, http.ClientAddress(), $"{device.Name}, {affected} camera(s) removed");

            return Results.Ok(new { ok = true, camerasRemoved = affected });
        })
        .RequireAdministrator()
        .WithName("DeleteDevice");

        MapPairing(app);
    }

    private static void MapPairing(IEndpointRouteBuilder app)
    {
        var pairing = app.MapGroup("/api/pairing").WithTags("Pairing");

        pairing.MapPost("/", (HttpContext http, PairingRepository tokens, SettingsRepository settings, NetworkInfoService network, AuthService auth) =>
        {
            tokens.PurgeExpired();

            var token = new PairingToken
            {
                Code = Ids.NewPairingCode(),
                CreatedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.Add(PairingLifetime),
                IssuedByUserId = http.CurrentUser().Id,
            };
            tokens.Insert(token);
            auth.Audit(http.CurrentUser(), "pairing.create", token.Code, http.ClientAddress());

            var urls = network.GetDashboardUrls();

            // The QR payload carries the code and where to reach the server. The code is
            // single-use and short-lived, so a photographed QR grants nothing for long, and no
            // permanent credential ever appears in it.
            return Results.Ok(new
            {
                code = token.Code,
                expiresUtc = token.ExpiresUtc,
                serverName = settings.GetString(SettingsRepository.Keys.ServerName, "VisionMesh"),
                serverUrl = urls.FirstOrDefault(),
                alternateUrls = urls,
                // The QR carries an ordinary URL so a phone's built-in camera app can open it
                // directly. That is what makes "scan and start streaming" work with no app to
                // install: the link lands on the browser camera page with the code already filled in.
                qrPayload = BuildCameraUrl(urls.FirstOrDefault(), token.Code),
                cameraUrl = BuildCameraUrl(urls.FirstOrDefault(), token.Code),
                // Kept for a future native app, which can register this scheme and be launched by it.
                deepLink = BuildDeepLink(token.Code, urls.FirstOrDefault(), settings.GetString(SettingsRepository.Keys.ServerName, "VisionMesh")),
            });
        })
        .RequireAdministrator()
        .WithName("CreatePairingCode")
        .WithSummary("Issues a short-lived, single-use code for pairing a new device.");

        pairing.MapGet("/", (PairingRepository tokens) =>
        {
            tokens.PurgeExpired();
            return Results.Ok(tokens.GetActive().Select(t => new { t.Code, t.CreatedUtc, t.ExpiresUtc }));
        })
        .RequireAdministrator()
        .WithName("ListPairingCodes");

        // Unauthenticated by design: this is how a device with no credentials gets them.
        // Security comes from the code being unguessable, single-use and short-lived.
        pairing.MapPost("/claim", (
            HttpContext http,
            ClaimPairingRequest request,
            PairingRepository tokens,
            DeviceRepository devices,
            SettingsRepository settings,
            AuditRepository audit) =>
        {
            var code = (request.Code ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(code))
                return Results.BadRequest(new { error = "Enter the pairing code shown on the server." });

            if (!Enum.TryParse<DeviceKind>(request.Kind, ignoreCase: true, out var kind))
                return Results.BadRequest(new { error = $"'{request.Kind}' is not a device kind VisionMesh understands." });

            var deviceId = Ids.NewId("dev");

            // Consume first: the update is atomic, so two devices racing on one code cannot both win.
            if (!tokens.TryConsume(code, deviceId))
            {
                audit.Write(new AuditEntry
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Action = "pairing.failed",
                    Address = http.ClientAddress(),
                    Detail = "invalid, expired or already used code",
                });
                return Results.Json(
                    new { error = "That pairing code is not valid any more. Generate a new one on the server." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var deviceToken = Ids.NewSecret();
            var device = new Device
            {
                Id = deviceId,
                Name = string.IsNullOrWhiteSpace(request.Name) ? $"{kind}" : request.Name.Trim(),
                Kind = kind,
                Platform = request.Platform ?? "",
                AgentVersion = request.Version ?? "",
                CreatedUtc = DateTimeOffset.UtcNow,
                State = DeviceState.Offline,
                TokenHash = TokenHasher.Hash(deviceToken),
                LastAddress = http.ClientAddress(),
            };
            devices.Insert(device);

            audit.Write(new AuditEntry
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Action = "pairing.success",
                Target = device.Id,
                Address = http.ClientAddress(),
                Detail = device.Name,
            });

            // The token is returned exactly once. Only its hash is stored, so it cannot be recovered.
            return Results.Ok(new
            {
                deviceId = device.Id,
                deviceToken,
                serverName = settings.GetString(SettingsRepository.Keys.ServerName, "VisionMesh"),
                agentWebSocketPath = Core.Contracts.AgentProtocol.WebSocketPath,
            });
        })
        .WithName("ClaimPairingCode")
        .WithSummary("Exchanges a pairing code for a permanent device token.");
    }

    /// <summary>
    /// The browser camera page with the pairing code in the fragment.
    ///
    /// The code goes in the fragment rather than the query string on purpose: a fragment is never
    /// sent to the server in a request line, so the single-use code cannot end up in an access log
    /// or a proxy log on its way to being redeemed.
    /// </summary>
    private static string BuildCameraUrl(string? serverUrl, string code)
        => $"{(serverUrl ?? "").TrimEnd('/')}/camera.html#code={Uri.EscapeDataString(code)}";

    /// <summary>A custom scheme a native app can register, so the same QR works once one exists.</summary>
    private static string BuildDeepLink(string code, string? serverUrl, string serverName)
    {
        var parts = new List<string> { $"code={Uri.EscapeDataString(code)}" };
        if (!string.IsNullOrEmpty(serverUrl)) parts.Add($"url={Uri.EscapeDataString(serverUrl)}");
        parts.Add($"name={Uri.EscapeDataString(serverName)}");
        return "visionmesh://pair?" + string.Join('&', parts);
    }

    private static IResult DeviceNotFound(string id)
        => Results.Json(new { error = "That device does not exist.", deviceId = id }, statusCode: StatusCodes.Status404NotFound);
}

public sealed class DeviceRenameRequest
{
    public string Name { get; set; } = "";
}

public sealed class ClaimPairingRequest
{
    public string? Code { get; set; }
    public string? Name { get; set; }
    public string Kind { get; set; } = "";
    public string? Platform { get; set; }
    public string? Version { get; set; }
}
