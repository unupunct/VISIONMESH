using VisionMesh.Agent.Core;
using VisionMesh.Core.Models;

namespace VisionMesh.Tests;

/// <summary>
/// A capture implementation that emits a fixed JPEG at a fixed rate.
///
/// This stands in for a webcam so the end-to-end test can run on a build machine with no camera
/// attached. Everything downstream of it - protocol framing, the frame bus, MJPEG fan-out,
/// recording - is the real implementation, which is the part worth testing.
/// </summary>
public sealed class FakeCameraCapture(byte[] jpeg, int width = 320, int height = 240) : ICameraCapture
{
    public const string SourceId = "fake:camera:0";

    /// <summary>Sessions opened so far, so a test can assert the agent actually opened the camera.</summary>
    public int SessionsOpened { get; private set; }

    public IReadOnlyList<CaptureDeviceInfo> Enumerate() => new List<CaptureDeviceInfo>
    {
        new()
        {
            SourceId = SourceId,
            Name = "Test Camera",
            Description = "Synthetic camera used by the VisionMesh test suite",
            Available = true,
            Formats = new List<CaptureFormatInfo>
            {
                new() { Width = width, Height = height, Fps = 30, Format = "MJPG", NativeJpeg = true },
            },
        },
    };

    public ICaptureSession Open(string sourceId, int requestedWidth, int requestedHeight, int fps, int quality)
    {
        if (sourceId != SourceId) throw new CameraCaptureException($"No camera with id '{sourceId}'.");
        SessionsOpened++;
        return new Session(jpeg, width, height, fps);
    }

    private sealed class Session(byte[] jpeg, int width, int height, int fps) : ICaptureSession
    {
        private readonly TimeSpan _interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(fps <= 0 ? 15 : fps, 1, 60));
        private bool _disposed;

        public int Width => width;
        public int Height => height;
        public long DroppedFrames => 0;

        public async Task<CapturedFrame?> ReadFrameAsync(CancellationToken cancellationToken)
        {
            if (_disposed) return null;
            await Task.Delay(_interval, cancellationToken);
            return new CapturedFrame(jpeg, width, height, NativeJpeg: true);
        }

        public void Dispose() => _disposed = true;
    }
}
