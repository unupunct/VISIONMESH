using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using VisionMesh.Api.Auth;
using VisionMesh.Streaming.Sources;

namespace VisionMesh.Api.Endpoints;

/// <summary>Finding cameras on the network: ONVIF discovery and manual probing.</summary>
public static class DiscoveryEndpoints
{
    public static void MapDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/discovery").WithTags("Discovery").RequireAdministrator();

        group.MapPost("/onvif", async (OnvifDiscovery discovery, int? seconds, CancellationToken cancellationToken) =>
        {
            // Four seconds catches essentially every camera that is going to answer; longer just
            // makes the user wait. The probe is sent twice inside that window.
            var window = TimeSpan.FromSeconds(Math.Clamp(seconds ?? 4, 1, 15));
            var found = await discovery.DiscoverAsync(window, cancellationToken);

            return Results.Ok(found.Select(camera => new
            {
                camera.EndpointReference,
                camera.DisplayName,
                camera.Name,
                camera.Hardware,
                camera.Location,
                camera.Address,
                serviceAddress = camera.ServiceAddresses.FirstOrDefault(),
                allServiceAddresses = camera.ServiceAddresses,
            }));
        })
        .WithName("DiscoverOnvifCameras")
        .WithSummary("Searches the local network for ONVIF cameras.");

        group.MapPost("/onvif/probe", async (
            OnvifProbeRequest request,
            IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Address))
                return Results.BadRequest(new { error = "Enter the camera's ONVIF address." });

            var client = new OnvifClient(httpClientFactory.CreateClient("onvif"), loggerFactory.CreateLogger("VisionMesh.Onvif"));

            try
            {
                var capabilities = await client.GetCapabilitiesAsync(request.Address, request.Username, request.Password, cancellationToken);
                if (capabilities.MediaServiceUri is null)
                {
                    return Results.Json(
                        new { error = "The camera answered but does not offer a media service, so VisionMesh cannot stream it." },
                        statusCode: StatusCodes.Status502BadGateway);
                }

                var information = await SafeGetInformationAsync(client, request, cancellationToken);
                var profiles = await client.GetProfilesAsync(capabilities.MediaServiceUri, request.Username, request.Password, cancellationToken);

                // Resolve the stream URL per profile up front, so choosing one in the UI needs no
                // second round trip and a profile that cannot stream is visibly marked.
                var resolved = new List<object>();
                foreach (var profile in profiles)
                {
                    string? streamUri = null;
                    string? error = null;
                    try
                    {
                        streamUri = await client.GetStreamUriAsync(capabilities.MediaServiceUri, profile.Token, request.Username, request.Password, cancellationToken);
                    }
                    catch (Exception ex) when (ex is OnvifClient.OnvifRequestException or HttpRequestException or TaskCanceledException)
                    {
                        error = ex.Message;
                    }

                    resolved.Add(new
                    {
                        profile.Token,
                        profile.Name,
                        description = profile.Describe(),
                        profile.Encoding,
                        profile.Width,
                        profile.Height,
                        profile.FrameRate,
                        profile.BitrateKbps,
                        ptzSupported = capabilities.SupportsPtz && profile.PtzNodeToken is not null,
                        streamUri,
                        error,
                    });
                }

                var snapshotUri = profiles.Count > 0
                    ? await client.GetSnapshotUriAsync(capabilities.MediaServiceUri, profiles[0].Token, request.Username, request.Password, cancellationToken)
                    : null;

                return Results.Ok(new
                {
                    device = information,
                    capabilities = new
                    {
                        capabilities.MediaServiceUri,
                        capabilities.PtzServiceUri,
                        capabilities.ImagingServiceUri,
                        capabilities.EventsServiceUri,
                        capabilities.SupportsPtz,
                    },
                    snapshotUri,
                    profiles = resolved,
                });
            }
            catch (OnvifClient.OnvifAuthenticationException ex)
            {
                return Results.Json(
                    new { error = ex.Message, code = "camera_auth" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }
            catch (Exception ex) when (ex is OnvifClient.OnvifRequestException or HttpRequestException or TaskCanceledException or UriFormatException)
            {
                return Results.Json(
                    new { error = $"Could not talk to the camera: {ex.Message}", code = "camera_unreachable" },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        })
        .WithName("ProbeOnvifCamera")
        .WithSummary("Reads an ONVIF camera's profiles, stream URLs and capabilities.");
    }

    /// <summary>Device information is optional on some cameras; a failure here must not fail the probe.</summary>
    private static async Task<object?> SafeGetInformationAsync(OnvifClient client, OnvifProbeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var information = await client.GetDeviceInformationAsync(request.Address, request.Username, request.Password, cancellationToken);
            return new
            {
                information.Manufacturer,
                information.Model,
                information.FirmwareVersion,
                information.SerialNumber,
            };
        }
        catch (OnvifClient.OnvifAuthenticationException) { throw; }
        catch (Exception ex) when (ex is OnvifClient.OnvifRequestException or HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }
}
