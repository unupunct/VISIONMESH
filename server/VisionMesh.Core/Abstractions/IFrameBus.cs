using VisionMesh.Core.Contracts;

namespace VisionMesh.Core.Abstractions;

/// <summary>One decoded-and-ready JPEG frame travelling from a source to any number of viewers.</summary>
public sealed class VideoFrame
{
    public required string CameraId { get; init; }
    public required ReadOnlyMemory<byte> Jpeg { get; init; }
    public required DateTimeOffset ReceivedUtc { get; init; }
    public long CaptureUnixMs { get; init; }
    public uint Sequence { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public FrameFlags Flags { get; init; }
}

/// <summary>
/// Fan-out point between camera sources and viewers.
///
/// Publishers do not know or care how many viewers exist; a camera with zero viewers and no
/// recording is stopped upstream rather than being published into a void.
/// </summary>
public interface IFrameBus
{
    /// <summary>Publishes a frame to all current subscribers of that camera. Never blocks on slow viewers.</summary>
    void Publish(VideoFrame frame);

    /// <summary>Subscribes to a camera's frames. Dispose the returned handle to unsubscribe.</summary>
    IFrameSubscription Subscribe(string cameraId);

    /// <summary>Most recent frame for a camera, used for snapshots and dashboard thumbnails.</summary>
    VideoFrame? GetLatestFrame(string cameraId);

    /// <summary>Number of live subscribers, used to stop idle cameras.</summary>
    int GetSubscriberCount(string cameraId);
}

public interface IFrameSubscription : IDisposable
{
    string CameraId { get; }
    /// <summary>
    /// Waits for the next frame. Slow consumers see frames dropped rather than buffered,
    /// because a late surveillance frame is worthless and unbounded buffering is a memory leak.
    /// </summary>
    ValueTask<VideoFrame?> ReadAsync(CancellationToken cancellationToken);
}
