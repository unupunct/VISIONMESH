using VisionMesh.Core.Models;

namespace VisionMesh.Agent.Core;

/// <summary>One captured frame, already JPEG encoded and ready to send.</summary>
/// <param name="Jpeg">JPEG bytes. The buffer belongs to the session and is valid until the next frame.</param>
/// <param name="NativeJpeg">True when the camera produced JPEG itself and the agent did not re-encode.</param>
public readonly record struct CapturedFrame(ReadOnlyMemory<byte> Jpeg, int Width, int Height, bool NativeJpeg);

/// <summary>
/// A running capture from one camera.
///
/// Frames are pulled rather than pushed so the agent controls pacing: if the link to the server
/// is congested, the agent simply reads the next frame later instead of queueing frames it will
/// never manage to send.
/// </summary>
public interface ICaptureSession : IDisposable
{
    int Width { get; }
    int Height { get; }
    /// <summary>Frames the driver reported as dropped, when the platform reports it.</summary>
    long DroppedFrames { get; }

    /// <summary>
    /// Reads the next frame, or null when the camera has stopped.
    /// Blocks until a frame is available or the token is cancelled.
    /// </summary>
    Task<CapturedFrame?> ReadFrameAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Platform-specific camera access. Implemented once per operating system, against the native
/// capture API rather than through an external tool, so the agent has no runtime dependency
/// beyond the OS itself.
/// </summary>
public interface ICameraCapture
{
    /// <summary>Lists cameras attached to this machine. Never throws: an unreadable device is reported as unavailable.</summary>
    IReadOnlyList<CaptureDeviceInfo> Enumerate();

    /// <summary>
    /// Opens a camera. The requested format is a preference: the closest supported format is
    /// chosen and the session reports what was actually negotiated.
    /// </summary>
    /// <exception cref="CameraCaptureException">The camera exists but cannot be opened.</exception>
    ICaptureSession Open(string sourceId, int width, int height, int fps, int quality);
}

/// <summary>
/// A camera could not be opened or read. Carries a message meant for a person, not a developer:
/// it is shown verbatim in the dashboard when a camera fails.
/// </summary>
public sealed class CameraCaptureException(string message, Exception? innerException = null)
    : Exception(message, innerException);
