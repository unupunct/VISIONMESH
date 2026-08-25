using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using VisionMesh.Api.Auth;
using VisionMesh.Core.Models;
using VisionMesh.Database.Repositories;
using VisionMesh.Recording;

namespace VisionMesh.Api.Endpoints;

/// <summary>Events, recordings, playback and storage.</summary>
public static class ArchiveEndpoints
{
    public static void MapArchiveEndpoints(this IEndpointRouteBuilder app)
    {
        MapEvents(app);
        MapRecordings(app);
        MapStorage(app);
    }

    private static void MapEvents(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/events").WithTags("Events").RequireViewer();

        group.MapGet("/", (
            EventRepository events,
            CameraRepository cameras,
            string? cameraId,
            string? type,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? limit,
            int? offset) =>
        {
            EventType? parsedType = null;
            if (!string.IsNullOrWhiteSpace(type))
            {
                if (!Enum.TryParse<EventType>(type, ignoreCase: true, out var value))
                    return Results.BadRequest(new { error = $"'{type}' is not an event type." });
                parsedType = value;
            }

            var names = cameras.GetAll().ToDictionary(c => c.Id, c => c.Name, StringComparer.Ordinal);
            var rows = events.Query(cameraId, parsedType, from, to, limit ?? 100, offset ?? 0);

            return Results.Ok(new
            {
                total = events.Count(cameraId, parsedType, from, to),
                items = rows.Select(e => new EventDto(
                    e.Id,
                    e.CameraId,
                    e.CameraId is null ? null : names.GetValueOrDefault(e.CameraId),
                    e.DeviceId,
                    e.Type.ToString(),
                    e.Severity.ToString(),
                    e.TimestampUtc,
                    e.Detail)),
            });
        })
        .WithName("ListEvents");

        group.MapGet("/types", () => Results.Ok(Enum.GetNames<EventType>()))
            .WithName("ListEventTypes");
    }

    private static void MapRecordings(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/recordings").WithTags("Recordings");

        group.MapGet("/", (
            RecordingRepository recordings,
            CameraRepository cameras,
            string? cameraId,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? limit,
            int? offset) =>
        {
            var names = cameras.GetAll().ToDictionary(c => c.Id, c => c.Name, StringComparer.Ordinal);
            var rows = recordings.Query(cameraId, from, to, limit ?? 200, offset ?? 0);
            return Results.Ok(rows.Select(r => RecordingDto.From(r, names.GetValueOrDefault(r.CameraId))));
        })
        .RequireViewer()
        .WithName("ListRecordings");

        group.MapGet("/timeline", (
            RecordingRepository recordings,
            EventRepository events,
            string cameraId,
            DateTimeOffset from,
            DateTimeOffset to) =>
        {
            // The timeline needs both the spans that have footage and the moments worth jumping
            // to, so it returns them together rather than making the client stitch two queries.
            var segments = recordings.Query(cameraId, from, to, 1000, 0);
            var marks = events.Query(cameraId, null, from, to, 500, 0);

            return Results.Ok(new
            {
                cameraId,
                from,
                to,
                segments = segments.Select(s => new
                {
                    s.Id,
                    s.StartUtc,
                    endUtc = s.EndUtc ?? s.StartUtc,
                    s.SizeBytes,
                    trigger = s.Trigger.ToString(),
                }),
                events = marks.Select(e => new
                {
                    e.Id,
                    e.TimestampUtc,
                    type = e.Type.ToString(),
                    severity = e.Severity.ToString(),
                    e.Detail,
                }),
            });
        })
        .RequireViewer()
        .WithName("GetRecordingTimeline")
        .WithSummary("Footage spans and event marks for one camera over a time range.");

        group.MapGet("/{id}/play", (HttpContext http, long id, RecordingRepository recordings, StorageManager storage) =>
        {
            var segment = recordings.GetById(id);
            if (segment is null) return Results.NotFound(new { error = "That recording does not exist." });

            // Paths come from our own index, but a corrupted row must never be able to serve an
            // arbitrary file. Confining playback to the recordings root closes that off entirely.
            if (!IsInsideRecordingsRoot(segment.FilePath, storage.GetRoot()))
            {
                return Results.Json(new { error = "That recording is outside the recordings folder." },
                                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (!File.Exists(segment.FilePath))
            {
                return Results.Json(new { error = "The recording file is no longer on disk.", code = "file_missing" },
                                    statusCode: StatusCodes.Status410Gone);
            }

            // enableRangeProcessing lets the browser seek without downloading the whole segment.
            var stream = new FileStream(segment.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            return Results.File(stream, "video/mp4", enableRangeProcessing: true);
        })
        .RequireViewer()
        .WithName("PlayRecording");

        group.MapGet("/{id}/download", (long id, RecordingRepository recordings, StorageManager storage, CameraRepository cameras) =>
        {
            var segment = recordings.GetById(id);
            if (segment is null) return Results.NotFound(new { error = "That recording does not exist." });
            if (!IsInsideRecordingsRoot(segment.FilePath, storage.GetRoot()))
                return Results.Json(new { error = "That recording is outside the recordings folder." }, statusCode: StatusCodes.Status403Forbidden);
            if (!File.Exists(segment.FilePath))
                return Results.Json(new { error = "The recording file is no longer on disk." }, statusCode: StatusCodes.Status410Gone);

            var cameraName = cameras.GetById(segment.CameraId)?.Name ?? segment.CameraId;
            var fileName = Core.Util.Ids.SafeFileName($"{cameraName} {segment.StartUtc.ToLocalTime():yyyy-MM-dd HH-mm-ss}.mp4");

            var stream = new FileStream(segment.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            return Results.File(stream, "video/mp4", fileName, enableRangeProcessing: true);
        })
        .RequireOperator()
        .WithName("DownloadRecording");

        group.MapDelete("/{id}", (HttpContext http, long id, RecordingRepository recordings, StorageManager storage, AuthService auth) =>
        {
            var segment = recordings.GetById(id);
            if (segment is null) return Results.NotFound(new { error = "That recording does not exist." });

            if (IsInsideRecordingsRoot(segment.FilePath, storage.GetRoot()))
            {
                try { if (File.Exists(segment.FilePath)) File.Delete(segment.FilePath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Results.Json(new { error = $"The file could not be deleted: {ex.Message}" },
                                        statusCode: StatusCodes.Status500InternalServerError);
                }
            }

            recordings.Delete(id);
            auth.Audit(http.CurrentUser(), "recording.delete", id.ToString(), http.ClientAddress(), segment.CameraId);
            return Results.Ok(new { ok = true });
        })
        .RequireAdministrator()
        .WithName("DeleteRecording");
    }

    private static void MapStorage(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/storage").WithTags("Storage").RequireViewer();

        group.MapGet("/", (StorageManager storage, RecordingRepository recordings, CameraRepository cameras) =>
        {
            var info = storage.GetStorageInfo();
            var bytesPerDay = storage.GetMeasuredBytesPerDay();

            // Projected days are computed from what this server has actually written, and are
            // omitted entirely when there is not yet enough history to measure. A made-up figure
            // here would be worse than no figure, because people plan disk purchases around it.
            double? projectedDays = bytesPerDay is > 0 && info.FreeBytes > 0
                ? Math.Round(info.FreeBytes / bytesPerDay.Value, 1)
                : null;

            return Results.Ok(new
            {
                info.Path,
                info.Exists,
                info.TotalBytes,
                info.FreeBytes,
                info.UsedByRecordingsBytes,
                info.RetentionDays,
                info.Error,
                usedPercent = info.TotalBytes > 0
                    ? Math.Round((info.TotalBytes - info.FreeBytes) * 100.0 / info.TotalBytes, 1)
                    : (double?)null,
                limitBytes = storage.GetStorageLimitBytes(),
                measuredBytesPerDay = bytesPerDay,
                projectedDaysRemaining = projectedDays,
                perCamera = cameras.GetAll().Select(camera => new
                {
                    camera.Id,
                    camera.Name,
                    bytes = recordings.GetTotalBytes(camera.Id),
                    camera.RetentionDays,
                }),
            });
        })
        .WithName("GetStorage");
    }

    /// <summary>
    /// True when a path resolves to somewhere inside the recordings root.
    /// Comparison happens after full path resolution so <c>..</c> segments and symlinked-looking
    /// paths cannot escape.
    /// </summary>
    private static bool IsInsideRecordingsRoot(string filePath, string root)
    {
        try
        {
            var fullFile = Path.GetFullPath(filePath);
            var fullRoot = Path.GetFullPath(root);
            if (!fullRoot.EndsWith(Path.DirectorySeparatorChar)) fullRoot += Path.DirectorySeparatorChar;

            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return fullFile.StartsWith(fullRoot, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
