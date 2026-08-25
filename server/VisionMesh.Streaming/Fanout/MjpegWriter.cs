using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using VisionMesh.Core.Abstractions;

namespace VisionMesh.Streaming.Fanout;

/// <summary>
/// Streams a camera to a browser as multipart/x-mixed-replace (MJPEG).
///
/// Why MJPEG for live view: it works in every browser and every webview with no plugin, no
/// JavaScript player, no signalling and no transcoding - the JPEG frames the camera already
/// produced are forwarded byte-for-byte. It costs more bandwidth than H.264 and carries no
/// audio, which is the trade we accept for a first release that genuinely works everywhere.
/// </summary>
public static class MjpegWriter
{
    public const string Boundary = "visionmeshframe";
    public const string ContentType = "multipart/x-mixed-replace; boundary=" + Boundary;

    private static readonly byte[] BoundaryPrefix = Encoding.ASCII.GetBytes("\r\n--" + Boundary + "\r\n");
    private static readonly byte[] CrLfCrLf = "\r\n\r\n"u8.ToArray();

    /// <summary>
    /// Pumps frames to the response until the client disconnects or the token is cancelled.
    /// Returns the number of frames written, which the caller can log.
    /// </summary>
    public static async Task<long> WriteStreamAsync(
        HttpResponse response,
        IFrameSubscription subscription,
        CancellationToken cancellationToken)
    {
        response.ContentType = ContentType;
        response.Headers.CacheControl = "no-store, no-cache, must-revalidate, private";
        response.Headers.Pragma = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no"; // stop reverse proxies buffering a live stream
        response.Headers.Connection = "close";

        var body = response.BodyWriter;
        long frames = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await subscription.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null) break;

            var header = Encoding.ASCII.GetBytes(
                $"Content-Type: image/jpeg\r\nContent-Length: {frame.Jpeg.Length}\r\nX-Timestamp: {frame.ReceivedUtc:O}");

            body.Write(BoundaryPrefix);
            body.Write(header);
            body.Write(CrLfCrLf);
            body.Write(frame.Jpeg.Span);

            var flush = await body.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (flush.IsCompleted || flush.IsCanceled) break;
            frames++;
        }

        return frames;
    }
}

/// <summary>Helpers shared by the WebSocket send paths.</summary>
internal static class WebSocketExtensions
{
    public static bool IsUsable(this WebSocket socket) => socket.State == WebSocketState.Open;
}
