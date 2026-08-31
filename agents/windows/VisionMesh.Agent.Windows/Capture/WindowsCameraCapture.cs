using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using VisionMesh.Agent.Core;
using VisionMesh.Core.Models;

namespace VisionMesh.Agent.Windows.Capture;

/// <summary>
/// Camera access on Windows through Media Foundation.
///
/// The format choice is the interesting part. Almost every USB webcam can emit MJPEG natively,
/// and VisionMesh transports JPEG. When those line up, a frame goes from the camera's own encoder
/// to the viewer's screen without ever being decoded or re-encoded: no CPU cost, no quality loss.
/// Only when a camera cannot produce JPEG does the agent fall back to RGB32 and encode, and it
/// reports which of the two happened so the dashboard can show it honestly.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCameraCapture : ICameraCapture, IDisposable
{
    private readonly ILogger _log;
    private bool _started;

    public WindowsCameraCapture(ILogger log)
    {
        _log = log;
        var hr = MediaFoundation.MFStartup(MediaFoundation.MF_VERSION, MediaFoundation.MFSTARTUP_LITE);
        if (hr < 0) throw new CameraCaptureException($"Windows Media Foundation could not be started (0x{hr:X8}).");
        _started = true;
    }

    public IReadOnlyList<CaptureDeviceInfo> Enumerate()
    {
        var devices = new List<CaptureDeviceInfo>();

        var hr = MediaFoundation.MFCreateAttributes(out var attributes, 1);
        if (hr < 0)
        {
            _log.LogError("Could not create Media Foundation attributes (0x{Hr:X8}).", hr);
            return devices;
        }

        try
        {
            var sourceTypeKey = MediaFoundation.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE;
            var videoCapture = MediaFoundation.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID;
            attributes.SetGUID(ref sourceTypeKey, ref videoCapture);

            hr = MediaFoundation.MFEnumDeviceSources(attributes, out var activateArray, out var count);
            if (hr < 0)
            {
                _log.LogError("Could not list video capture devices (0x{Hr:X8}).", hr);
                return devices;
            }

            try
            {
                for (var i = 0; i < count; i++)
                {
                    var pointer = Marshal.ReadIntPtr(activateArray, i * IntPtr.Size);
                    if (pointer == IntPtr.Zero) continue;

                    var activate = (IMFActivate)Marshal.GetObjectForIUnknown(pointer);
                    try
                    {
                        var device = DescribeDevice(activate);
                        if (device is not null) devices.Add(device);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(activate);
                        Marshal.Release(pointer);
                    }
                }
            }
            finally
            {
                // MFEnumDeviceSources allocates the array with CoTaskMemAlloc.
                Marshal.FreeCoTaskMem(activateArray);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(attributes);
        }

        return devices;
    }

    private CaptureDeviceInfo? DescribeDevice(IMFActivate activate)
    {
        var nameKey = MediaFoundation.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME;
        var linkKey = MediaFoundation.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK;

        if (activate.GetAllocatedString(ref nameKey, out var friendlyName, out _) < 0) friendlyName = "Camera";
        if (activate.GetAllocatedString(ref linkKey, out var symbolicLink, out _) < 0 || string.IsNullOrEmpty(symbolicLink))
        {
            // Without the symbolic link there is no stable identity for this camera, and a camera
            // that cannot be identified across reboots is worse than not listing it at all.
            _log.LogWarning("Skipping camera '{Name}': Windows did not report a device path for it.", friendlyName);
            return null;
        }

        var device = new CaptureDeviceInfo
        {
            SourceId = symbolicLink,
            Name = friendlyName,
            Description = "Media Foundation video capture device",
            Available = true,
        };

        // Opening the device is the only way to learn its formats, and it doubles as a check
        // that nothing else has already claimed it.
        try
        {
            var riid = MediaFoundation.IID_IMFMediaSource;
            var hr = activate.ActivateObject(ref riid, out var sourceObject);
            if (hr < 0)
            {
                device.Available = false;
                device.Unavailable = DescribeActivationFailure(hr);
                return device;
            }

            var source = (IMFMediaSource)sourceObject;
            try
            {
                hr = MediaFoundation.MFCreateSourceReaderFromMediaSource(source, null, out var reader);
                if (hr < 0)
                {
                    device.Available = false;
                    device.Unavailable = DescribeActivationFailure(hr);
                    return device;
                }

                try
                {
                    device.Formats = ReadFormats(reader);
                }
                finally
                {
                    Marshal.ReleaseComObject(reader);
                }
            }
            finally
            {
                source.Shutdown();
                Marshal.ReleaseComObject(source);
            }
        }
        catch (COMException ex)
        {
            device.Available = false;
            device.Unavailable = DescribeActivationFailure(ex.HResult);
        }

        return device;
    }

    /// <summary>
    /// Highest media type index to examine. Cameras like the Logitech BRIO advertise well over
    /// 256 combinations of size, rate and encoding, and stopping early hides their best modes.
    /// </summary>
    private const uint MaxMediaTypes = 2048;

    /// <summary>How many formats to report upstream. Enough to choose from, not a wall of text.</summary>
    private const int ReportedFormatLimit = 32;

    private static List<CaptureFormatInfo> ReadFormats(IMFSourceReader reader)
    {
        var formats = new List<CaptureFormatInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (uint index = 0; index < MaxMediaTypes; index++)
        {
            if (reader.GetNativeMediaType(MediaFoundation.MF_SOURCE_READER_FIRST_VIDEO_STREAM, index, out var mediaType) < 0) break;

            try
            {
                var subtypeKey = MediaFoundation.MF_MT_SUBTYPE;
                var sizeKey = MediaFoundation.MF_MT_FRAME_SIZE;
                var rateKey = MediaFoundation.MF_MT_FRAME_RATE;

                if (mediaType.GetGUID(ref subtypeKey, out var subtype) < 0) continue;
                if (mediaType.GetUINT64(ref sizeKey, out var packedSize) < 0) continue;

                var (width, height) = MediaFoundation.Unpack(packedSize);

                double fps = 0;
                if (mediaType.GetUINT64(ref rateKey, out var packedRate) >= 0)
                {
                    var (numerator, denominator) = MediaFoundation.Unpack(packedRate);
                    if (denominator > 0) fps = Math.Round((double)numerator / denominator, 2);
                }

                var name = MediaFoundation.DescribeSubtype(subtype);

                // Cameras list the same resolution at many frame rates. Keep one entry per
                // size and encoding, carrying the highest rate that size supports.
                var key = $"{width}x{height}:{name}";
                if (!seen.Add(key)) continue;

                formats.Add(new CaptureFormatInfo
                {
                    Width = (int)width,
                    Height = (int)height,
                    Fps = fps,
                    Format = name,
                    NativeJpeg = subtype == MediaFoundation.MFVideoFormat_MJPG,
                });
            }
            finally
            {
                Marshal.ReleaseComObject(mediaType);
            }
        }

        return formats
            .OrderByDescending(f => f.NativeJpeg)
            .ThenByDescending(f => (long)f.Width * f.Height)
            .ThenByDescending(f => f.Fps)
            .Take(ReportedFormatLimit)
            .ToList();
    }

    private static string DescribeActivationFailure(int hr) => (uint)hr switch
    {
        0x8007001F => "Windows reported a device error. Unplugging and reconnecting the camera usually clears it.",
        0x80070005 => "Windows denied access to the camera. Allow desktop apps to use the camera in Settings, Privacy, Camera.",
        0xC00D3704 => "The camera is already being used by another program.",
        0x80070002 => "The camera is no longer connected.",
        _ => $"The camera could not be opened (0x{(uint)hr:X8}).",
    };

    public ICaptureSession Open(string sourceId, int width, int height, int fps, int quality)
        => new Session(sourceId, width, height, fps, quality, _log);

    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        MediaFoundation.MFShutdown();
    }

    /// <summary>One open camera, delivering JPEG frames.</summary>
    [SupportedOSPlatform("windows")]
    private sealed class Session : ICaptureSession
    {
        private readonly ILogger _log;
        private readonly int _quality;
        private readonly object _gate = new();

        private IMFMediaSource? _source;
        private IMFSourceReader? _reader;
        private bool _nativeJpeg;
        private int _stride;
        private byte[]? _jpegBuffer;
        private MemoryStream? _encodeBuffer;
        private bool _disposed;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public long DroppedFrames { get; private set; }

        public Session(string sourceId, int width, int height, int fps, int quality, ILogger log)
        {
            _log = log;
            _quality = Math.Clamp(quality, 1, 100);

            var activate = FindActivate(sourceId)
                ?? throw new CameraCaptureException("That camera is no longer connected to this computer.");

            try
            {
                var riid = MediaFoundation.IID_IMFMediaSource;
                var hr = activate.ActivateObject(ref riid, out var sourceObject);
                if (hr < 0) throw new CameraCaptureException(DescribeActivationFailure(hr));

                _source = (IMFMediaSource)sourceObject;

                // Advanced video processing lets MF convert an unusual pixel format to RGB32 for
                // us, which is what makes the non-MJPEG fallback path work on any webcam.
                MediaFoundation.MFCreateAttributes(out var readerAttributes, 1);
                try
                {
                    var advancedKey = MediaFoundation.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING;
                    readerAttributes.SetUINT32(ref advancedKey, 1);

                    hr = MediaFoundation.MFCreateSourceReaderFromMediaSource(_source, readerAttributes, out var reader);
                    if (hr < 0) throw new CameraCaptureException($"The camera could not be opened for reading (0x{(uint)hr:X8}).");
                    _reader = reader;
                }
                finally
                {
                    Marshal.ReleaseComObject(readerAttributes);
                }

                Configure(width, height, fps);
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                Marshal.ReleaseComObject(activate);
            }
        }

        private static IMFActivate? FindActivate(string sourceId)
        {
            if (MediaFoundation.MFCreateAttributes(out var attributes, 1) < 0) return null;

            try
            {
                var sourceTypeKey = MediaFoundation.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE;
                var videoCapture = MediaFoundation.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID;
                attributes.SetGUID(ref sourceTypeKey, ref videoCapture);

                if (MediaFoundation.MFEnumDeviceSources(attributes, out var array, out var count) < 0) return null;

                try
                {
                    for (var i = 0; i < count; i++)
                    {
                        var pointer = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                        if (pointer == IntPtr.Zero) continue;

                        var activate = (IMFActivate)Marshal.GetObjectForIUnknown(pointer);
                        var linkKey = MediaFoundation.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK;

                        var matches = activate.GetAllocatedString(ref linkKey, out var link, out _) >= 0
                                      && string.Equals(link, sourceId, StringComparison.OrdinalIgnoreCase);

                        Marshal.Release(pointer);
                        if (matches) return activate;
                        Marshal.ReleaseComObject(activate);
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(array);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(attributes);
            }

            return null;
        }

        /// <summary>Picks the best available format and configures the reader for it.</summary>
        private void Configure(int requestedWidth, int requestedHeight, int requestedFps)
        {
            if (_reader is null) throw new CameraCaptureException("The camera reader was not created.");

            var best = ChooseFormat(_reader, requestedWidth, requestedHeight, requestedFps);

            if (best.MediaType is not null)
            {
                var hr = _reader.SetCurrentMediaType(MediaFoundation.MF_SOURCE_READER_FIRST_VIDEO_STREAM, IntPtr.Zero, best.MediaType);
                Marshal.ReleaseComObject(best.MediaType);

                if (hr < 0)
                {
                    _log.LogWarning("The camera refused the chosen format (0x{Hr:X8}); falling back to RGB conversion.", hr);
                    best = default;
                }
            }

            if (best.MediaType is null)
            {
                // Ask MF to hand us RGB32 and let it insert whatever converter is needed.
                if (MediaFoundation.MFCreateMediaType(out var rgbType) < 0)
                    throw new CameraCaptureException("Could not prepare a video format for this camera.");

                try
                {
                    var majorKey = MediaFoundation.MF_MT_MAJOR_TYPE;
                    var video = MediaFoundation.MFMediaType_Video;
                    var subtypeKey = MediaFoundation.MF_MT_SUBTYPE;
                    var rgb32 = MediaFoundation.MFVideoFormat_RGB32;

                    rgbType.SetGUID(ref majorKey, ref video);
                    rgbType.SetGUID(ref subtypeKey, ref rgb32);

                    var hr = _reader.SetCurrentMediaType(MediaFoundation.MF_SOURCE_READER_FIRST_VIDEO_STREAM, IntPtr.Zero, rgbType);
                    if (hr < 0) throw new CameraCaptureException($"This camera does not offer a format VisionMesh can use (0x{(uint)hr:X8}).");
                }
                finally
                {
                    Marshal.ReleaseComObject(rgbType);
                }
            }

            _reader.SetStreamSelection(MediaFoundation.MF_SOURCE_READER_ALL_STREAMS, false);
            _reader.SetStreamSelection(MediaFoundation.MF_SOURCE_READER_FIRST_VIDEO_STREAM, true);

            ReadNegotiatedFormat();
        }

        private (IMFMediaType? MediaType, bool NativeJpeg) ChooseFormat(IMFSourceReader reader, int width, int height, int fps)
        {
            IMFMediaType? bestType = null;
            var bestScore = long.MinValue;

            for (uint index = 0; index < MaxMediaTypes; index++)
            {
                if (reader.GetNativeMediaType(MediaFoundation.MF_SOURCE_READER_FIRST_VIDEO_STREAM, index, out var mediaType) < 0) break;

                var keep = false;
                try
                {
                    var subtypeKey = MediaFoundation.MF_MT_SUBTYPE;
                    var sizeKey = MediaFoundation.MF_MT_FRAME_SIZE;

                    if (mediaType.GetGUID(ref subtypeKey, out var subtype) < 0) continue;
                    if (subtype != MediaFoundation.MFVideoFormat_MJPG) continue;   // only MJPEG avoids re-encoding
                    if (mediaType.GetUINT64(ref sizeKey, out var packedSize) < 0) continue;

                    var (candidateWidth, candidateHeight) = MediaFoundation.Unpack(packedSize);

                    // Prefer the format closest to what was asked for, in pixel count. Exact
                    // matches win outright; otherwise the nearest size is chosen rather than the
                    // largest, so asking for 720p on a 4K camera does not saturate the network.
                    var requestedPixels = (long)width * height;
                    var candidatePixels = (long)candidateWidth * candidateHeight;
                    var score = -Math.Abs(candidatePixels - requestedPixels);
                    if (candidateWidth == width && candidateHeight == height) score = long.MaxValue / 2;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        if (bestType is not null) Marshal.ReleaseComObject(bestType);
                        bestType = mediaType;
                        keep = true;
                    }
                }
                finally
                {
                    if (!keep) Marshal.ReleaseComObject(mediaType);
                }
            }

            return (bestType, bestType is not null);
        }

        /// <summary>Reads back what the camera actually agreed to, which is what gets reported upstream.</summary>
        private void ReadNegotiatedFormat()
        {
            if (_reader is null) return;
            if (_reader.GetCurrentMediaType(MediaFoundation.MF_SOURCE_READER_FIRST_VIDEO_STREAM, out var current) < 0) return;

            try
            {
                var subtypeKey = MediaFoundation.MF_MT_SUBTYPE;
                var sizeKey = MediaFoundation.MF_MT_FRAME_SIZE;
                var strideKey = MediaFoundation.MF_MT_DEFAULT_STRIDE;

                if (current.GetGUID(ref subtypeKey, out var subtype) >= 0)
                {
                    _nativeJpeg = subtype == MediaFoundation.MFVideoFormat_MJPG;
                }

                if (current.GetUINT64(ref sizeKey, out var packedSize) >= 0)
                {
                    var (width, height) = MediaFoundation.Unpack(packedSize);
                    Width = (int)width;
                    Height = (int)height;
                }

                // Stride carries the row order for RGB formats: negative means the buffer is
                // bottom-up, which is normal for RGB out of Media Foundation. Ignoring the sign
                // produces an upside-down picture.
                _stride = current.GetUINT32(ref strideKey, out var stride) >= 0 ? unchecked((int)stride) : Width * 4;

                _log.LogInformation("Camera negotiated {Width}x{Height} {Format}.",
                    Width, Height, _nativeJpeg ? "MJPEG (forwarded without re-encoding)" : "RGB32 (encoded to JPEG by the agent)");
            }
            finally
            {
                Marshal.ReleaseComObject(current);
            }
        }

        public Task<CapturedFrame?> ReadFrameAsync(CancellationToken cancellationToken)
        {
            // Media Foundation's synchronous reader blocks, so it runs on the thread pool rather
            // than stalling the agent's async loop.
            return Task.Run(() => ReadFrame(cancellationToken), cancellationToken);
        }

        private CapturedFrame? ReadFrame(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_disposed || _reader is null) return null;

                while (!cancellationToken.IsCancellationRequested)
                {
                    var hr = _reader.ReadSample(
                        MediaFoundation.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0,
                        out _, out var streamFlags, out _, out var sample);

                    if (hr < 0) throw new CameraCaptureException($"Reading from the camera failed (0x{(uint)hr:X8}).");

                    if ((streamFlags & MediaFoundation.MF_SOURCE_READERF_ENDOFSTREAM) != 0) return null;

                    if ((streamFlags & MediaFoundation.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0)
                    {
                        // Some cameras renegotiate mid-stream, e.g. when the resolution changes.
                        ReadNegotiatedFormat();
                    }

                    if (sample is null)
                    {
                        // A stream tick is a gap, not a frame. Nothing to send, so ask again.
                        if ((streamFlags & MediaFoundation.MF_SOURCE_READERF_STREAMTICK) != 0)
                        {
                            DroppedFrames++;
                            continue;
                        }
                        continue;
                    }

                    try
                    {
                        return ConvertSample(sample);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(sample);
                    }
                }

                return null;
            }
        }

        private CapturedFrame? ConvertSample(IMFSample sample)
        {
            if (sample.ConvertToContiguousBuffer(out var buffer) < 0) return null;

            try
            {
                if (buffer.Lock(out var pointer, out _, out var length) < 0) return null;

                try
                {
                    if (length == 0) return null;

                    if (_nativeJpeg)
                    {
                        // Straight through: the camera already produced a JPEG.
                        if (_jpegBuffer is null || _jpegBuffer.Length < length) _jpegBuffer = new byte[length];
                        Marshal.Copy(pointer, _jpegBuffer, 0, (int)length);
                        return new CapturedFrame(_jpegBuffer.AsMemory(0, (int)length), Width, Height, NativeJpeg: true);
                    }

                    return EncodeRgb32(pointer, (int)length);
                }
                finally
                {
                    buffer.Unlock();
                }
            }
            finally
            {
                Marshal.ReleaseComObject(buffer);
            }
        }

        private CapturedFrame? EncodeRgb32(IntPtr pointer, int length)
        {
            if (Width <= 0 || Height <= 0) return null;

            var stride = _stride != 0 ? _stride : Width * 4;
            var absoluteStride = Math.Abs(stride);
            if ((long)absoluteStride * Height > length) return null;

            // A negative stride means the first row in memory is the bottom row of the picture.
            // Pointing Bitmap at the last row and keeping the negative stride flips it correctly
            // without copying the frame.
            var scan0 = stride < 0 ? pointer + (absoluteStride * (Height - 1)) : pointer;

            using var bitmap = new Bitmap(Width, Height, stride, PixelFormat.Format32bppRgb, scan0);

            _encodeBuffer ??= new MemoryStream(256 * 1024);
            _encodeBuffer.SetLength(0);

            using var parameters = new EncoderParameters(1);
            using var qualityParameter = new EncoderParameter(Encoder.Quality, (long)_quality);
            parameters.Param[0] = qualityParameter;

            bitmap.Save(_encodeBuffer, JpegEncoder.Value, parameters);

            return new CapturedFrame(_encodeBuffer.GetBuffer().AsMemory(0, (int)_encodeBuffer.Length), Width, Height, NativeJpeg: false);
        }

        private static readonly Lazy<ImageCodecInfo> JpegEncoder = new(() =>
            ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid));

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;

                if (_reader is not null)
                {
                    Marshal.ReleaseComObject(_reader);
                    _reader = null;
                }

                if (_source is not null)
                {
                    // Shutdown releases the camera so the privacy indicator goes out and other
                    // programs can use it again.
                    try { _source.Shutdown(); } catch (COMException) { }
                    Marshal.ReleaseComObject(_source);
                    _source = null;
                }

                _encodeBuffer?.Dispose();
                _encodeBuffer = null;
                _jpegBuffer = null;
            }
        }
    }
}
