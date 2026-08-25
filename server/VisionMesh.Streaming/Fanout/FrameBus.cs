using System.Collections.Concurrent;
using System.Threading.Channels;
using VisionMesh.Core.Abstractions;

namespace VisionMesh.Streaming.Fanout;

/// <summary>
/// In-process fan-out from one camera source to many viewers.
///
/// Each subscriber gets a capacity-1 channel in DropOldest mode. That is deliberate: for live
/// surveillance a stale frame has no value, so a viewer that cannot keep up should skip ahead
/// to the newest frame rather than fall further behind. Unbounded queueing here would turn one
/// slow browser tab into unbounded server memory growth.
/// </summary>
public sealed class FrameBus : IFrameBus
{
    private readonly ConcurrentDictionary<string, CameraChannel> _cameras = new(StringComparer.Ordinal);

    public void Publish(VideoFrame frame)
    {
        var channel = _cameras.GetOrAdd(frame.CameraId, static _ => new CameraChannel());
        channel.Latest = frame;
        foreach (var subscriber in channel.Subscribers.Keys) subscriber.Offer(frame);
    }

    public IFrameSubscription Subscribe(string cameraId)
    {
        var channel = _cameras.GetOrAdd(cameraId, static _ => new CameraChannel());
        var subscription = new Subscription(cameraId, channel);
        channel.Subscribers.TryAdd(subscription, 0);

        // Hand the newest frame straight over so a viewer sees a picture immediately
        // instead of waiting up to a full frame interval for the next one.
        if (channel.Latest is { } latest) subscription.Offer(latest);
        return subscription;
    }

    public VideoFrame? GetLatestFrame(string cameraId)
        => _cameras.TryGetValue(cameraId, out var channel) ? channel.Latest : null;

    public int GetSubscriberCount(string cameraId)
        => _cameras.TryGetValue(cameraId, out var channel) ? channel.Subscribers.Count : 0;

    /// <summary>Forgets a camera's cached frame and drops its channel. Called when a camera is deleted or stops.</summary>
    public void Reset(string cameraId)
    {
        if (_cameras.TryGetValue(cameraId, out var channel)) channel.Latest = null;
    }

    public IReadOnlyCollection<string> ActiveCameras => _cameras.Keys.ToArray();

    private sealed class CameraChannel
    {
        public volatile VideoFrame? Latest;
        public ConcurrentDictionary<Subscription, byte> Subscribers { get; } = new();
    }

    private sealed class Subscription : IFrameSubscription
    {
        private readonly CameraChannel _owner;
        private readonly Channel<VideoFrame> _channel = Channel.CreateBounded<VideoFrame>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        private int _disposed;

        public Subscription(string cameraId, CameraChannel owner)
        {
            CameraId = cameraId;
            _owner = owner;
        }

        public string CameraId { get; }

        public void Offer(VideoFrame frame)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _channel.Writer.TryWrite(frame);
        }

        public async ValueTask<VideoFrame?> ReadAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return null; }
            catch (ChannelClosedException) { return null; }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner.Subscribers.TryRemove(this, out _);
            _channel.Writer.TryComplete();
        }
    }
}
