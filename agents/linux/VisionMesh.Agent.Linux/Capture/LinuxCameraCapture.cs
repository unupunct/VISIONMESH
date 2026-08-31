using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using VisionMesh.Agent.Core;
using VisionMesh.Core.Models;

namespace VisionMesh.Agent.Linux.Capture;

/// <summary>
/// Camera access on Linux through Video4Linux2, using memory-mapped streaming.
///
/// Like the Windows agent, this prefers a camera's native MJPEG output: when the camera has its
/// own encoder, frames go straight to the server untouched. Cameras that only offer raw formats
/// fall back to VisionMesh's own JPEG encoder rather than dragging in an imaging library.
///
/// Buffers are mmapped rather than read(), because read() copies every frame through the kernel
/// an extra time and some drivers do not implement it at all.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxCameraCapture(ILogger log) : ICameraCapture
{
    /// <summary>Number of driver buffers to queue. Four is enough to absorb scheduling jitter.</summary>
    private const uint BufferCount = 4;

    public IReadOnlyList<CaptureDeviceInfo> Enumerate()
    {
        var devices = new List<CaptureDeviceInfo>();

        // /dev/video* covers cameras, but also encoders, decoders and metadata nodes on
        // platforms like the Raspberry Pi. QUERYCAP tells us which ones actually capture video.
        var nodes = Directory.Exists("/dev")
            ? Directory.GetFiles("/dev", "video*").OrderBy(NodeNumber).ToArray()
            : Array.Empty<string>();

        foreach (var node in nodes)
        {
            var device = Describe(node);
            if (device is not null) devices.Add(device);
        }

        return devices;
    }

    private static int NodeNumber(string path)
        => int.TryParse(Path.GetFileName(path).Replace("video", ""), out var number) ? number : int.MaxValue;

    private CaptureDeviceInfo? Describe(string node)
    {
        var fd = V4l2.Open(node, V4l2.O_RDWR | V4l2.O_NONBLOCK);
        if (fd < 0)
        {
            var error = Marshal.GetLastWin32Error();

            // A permission error is worth reporting: it is the single most common reason a Linux
            // camera "does not work", and the fix is one usermod away.
            if (error == V4l2.EACCES)
            {
                return new CaptureDeviceInfo
                {
                    SourceId = node,
                    Name = Path.GetFileName(node),
                    Available = false,
                    Unavailable = $"VisionMesh does not have permission to use {node}. "
                                + "Add the user running the agent to the 'video' group, then sign out and back in.",
                };
            }

            log.LogDebug("Skipping {Node}: open failed with errno {Error}.", node, error);
            return null;
        }

        try
        {
            var capability = new V4l2.Capability();
            if (!Ioctl(fd, V4l2.VIDIOC_QUERYCAP, ref capability))
            {
                log.LogDebug("Skipping {Node}: it did not answer QUERYCAP.", node);
                return null;
            }

            // DeviceCaps describes this node; Capabilities describes the whole physical device,
            // which on multi-node drivers would wrongly mark an output node as a camera.
            var caps = (capability.Capabilities & V4l2.V4L2_CAP_DEVICE_CAPS) != 0
                ? capability.DeviceCaps
                : capability.Capabilities;

            if ((caps & V4l2.V4L2_CAP_VIDEO_CAPTURE) == 0) return null;

            var card = V4l2.ReadFixedString(capability.Card);
            var driver = V4l2.ReadFixedString(capability.Driver);
            var busInfo = V4l2.ReadFixedString(capability.BusInfo);

            var device = new CaptureDeviceInfo
            {
                SourceId = node,
                Name = string.IsNullOrWhiteSpace(card) ? Path.GetFileName(node) : card,
                Description = string.Join(" · ", new[] { driver, busInfo }.Where(s => !string.IsNullOrWhiteSpace(s))),
                Available = true,
                Formats = ReadFormats(fd),
            };

            if ((caps & V4l2.V4L2_CAP_STREAMING) == 0)
            {
                device.Available = false;
                device.Unavailable = "This device does not support streaming capture, which VisionMesh needs.";
            }
            else if (device.Formats.Count == 0)
            {
                device.Available = false;
                device.Unavailable = "This camera offers no video format VisionMesh can use.";
            }

            return device;
        }
        finally
        {
            V4l2.Close(fd);
        }
    }

    private List<CaptureFormatInfo> ReadFormats(int fd)
    {
        var formats = new List<CaptureFormatInfo>();

        for (uint index = 0; index < 64; index++)
        {
            var description = new V4l2.FormatDescription
            {
                Index = index,
                Type = V4l2.V4L2_BUF_TYPE_VIDEO_CAPTURE,
                Description = new byte[32],
                Reserved = new uint[3],
            };

            if (!Ioctl(fd, V4l2.VIDIOC_ENUM_FMT, ref description)) break;
            if (!IsUsable(description.PixelFormat)) continue;

            var nativeJpeg = description.PixelFormat == V4l2.V4L2_PIX_FMT_MJPEG
                          || description.PixelFormat == V4l2.V4L2_PIX_FMT_JPEG;

            foreach (var (width, height) in ReadFrameSizes(fd, description.PixelFormat))
            {
                formats.Add(new CaptureFormatInfo
                {
                    Width = (int)width,
                    Height = (int)height,
                    Fps = ReadBestFrameRate(fd, description.PixelFormat, width, height),
                    Format = V4l2.DescribeFourCc(description.PixelFormat),
                    NativeJpeg = nativeJpeg,
                });
            }
        }

        return formats
            .OrderByDescending(f => f.NativeJpeg)
            .ThenByDescending(f => (long)f.Width * f.Height)
            .ThenByDescending(f => f.Fps)
            .Take(32)
            .ToList();
    }

    /// <summary>
    /// Formats VisionMesh can turn into JPEG. H.264 and the planar formats are deliberately
    /// excluded: forwarding H.264 would need a different transport, and adding conversions for
    /// formats almost no webcam exposes would be code nobody exercises.
    /// </summary>
    private static bool IsUsable(uint pixelFormat)
        => pixelFormat == V4l2.V4L2_PIX_FMT_MJPEG
        || pixelFormat == V4l2.V4L2_PIX_FMT_JPEG
        || pixelFormat == V4l2.V4L2_PIX_FMT_YUYV
        || pixelFormat == V4l2.V4L2_PIX_FMT_RGB24
        || pixelFormat == V4l2.V4L2_PIX_FMT_BGR24;

    private List<(uint Width, uint Height)> ReadFrameSizes(int fd, uint pixelFormat)
    {
        var sizes = new List<(uint, uint)>();

        for (uint index = 0; index < 64; index++)
        {
            var query = new V4l2.FrameSizeEnum { Index = index, PixelFormat = pixelFormat, Reserved = new uint[2] };
            if (!Ioctl(fd, V4l2.VIDIOC_ENUM_FRAMESIZES, ref query)) break;

            if (query.Type == V4l2.V4L2_FRMSIZE_TYPE_DISCRETE)
            {
                sizes.Add((query.Width, query.Height));
                continue;
            }

            // A stepwise or continuous range would be an unbounded list. Offering a handful of
            // familiar sizes that fall inside the range is far more useful than the raw range.
            foreach (var (width, height) in new[] { (640u, 480u), (1280u, 720u), (1920u, 1080u), (2560u, 1440u) })
            {
                if (width >= query.Width && width <= query.MaxWidth && height >= query.Height && height <= query.MaxHeight)
                {
                    sizes.Add((width, height));
                }
            }
            break;
        }

        return sizes;
    }

    private double ReadBestFrameRate(int fd, uint pixelFormat, uint width, uint height)
    {
        var best = 0.0;

        for (uint index = 0; index < 32; index++)
        {
            var query = new V4l2.FrameIntervalEnum
            {
                Index = index,
                PixelFormat = pixelFormat,
                Width = width,
                Height = height,
                Padding = new byte[24],
            };

            if (!Ioctl(fd, V4l2.VIDIOC_ENUM_FRAMEINTERVALS, ref query)) break;
            if (query.Numerator == 0) continue;

            // The interval is seconds per frame, so the rate is its reciprocal.
            best = Math.Max(best, (double)query.Denominator / query.Numerator);
        }

        return Math.Round(best, 2);
    }

    public ICaptureSession Open(string sourceId, int width, int height, int fps, int quality)
        => new Session(sourceId, width, height, fps, quality, log);

    private static bool Ioctl<T>(int fd, ulong request, ref T argument) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(argument, buffer, false);
            if (V4l2.IoctlRetry(fd, request, buffer) < 0) return false;
            argument = Marshal.PtrToStructure<T>(buffer);
            return true;
        }
        finally
        {
            Marshal.DestroyStructure<T>(buffer);
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>One open V4L2 device, streaming through mmapped buffers.</summary>
    [SupportedOSPlatform("linux")]
    private sealed class Session : ICaptureSession
    {
        private readonly ILogger _log;
        private readonly int _quality;
        private readonly object _gate = new();

        private int _fd = -1;
        private (IntPtr Address, nuint Length)[] _buffers = Array.Empty<(IntPtr, nuint)>();
        private uint _pixelFormat;
        private bool _streaming;
        private bool _disposed;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public long DroppedFrames { get; private set; }

        private uint _lastSequence;
        private bool _haveSequence;

        public Session(string node, int requestedWidth, int requestedHeight, int fps, int quality, ILogger log)
        {
            _log = log;
            _quality = Math.Clamp(quality, 1, 100);

            _fd = V4l2.Open(node, V4l2.O_RDWR);
            if (_fd < 0)
            {
                var error = Marshal.GetLastWin32Error();
                throw new CameraCaptureException(error switch
                {
                    V4l2.EACCES => $"VisionMesh does not have permission to use {node}. Add the agent's user to the 'video' group.",
                    V4l2.EBUSY => "The camera is already being used by another program.",
                    V4l2.ENODEV => "The camera is no longer connected.",
                    _ => $"The camera could not be opened (errno {error}).",
                });
            }

            try
            {
                Negotiate(requestedWidth, requestedHeight, fps);
                MapBuffers();
                StartStreaming();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void Negotiate(int requestedWidth, int requestedHeight, int fps)
        {
            // Try MJPEG first, then the raw formats, so a camera with its own encoder is used
            // as one rather than being decoded and re-encoded for nothing.
            var candidates = new[]
            {
                V4l2.V4L2_PIX_FMT_MJPEG,
                V4l2.V4L2_PIX_FMT_JPEG,
                V4l2.V4L2_PIX_FMT_YUYV,
                V4l2.V4L2_PIX_FMT_RGB24,
                V4l2.V4L2_PIX_FMT_BGR24,
            };

            foreach (var candidate in candidates)
            {
                var format = new V4l2.Format
                {
                    Type = V4l2.V4L2_BUF_TYPE_VIDEO_CAPTURE,
                    Width = (uint)Math.Max(requestedWidth, 16),
                    Height = (uint)Math.Max(requestedHeight, 16),
                    PixelFormat = candidate,
                    Field = V4l2.V4L2_FIELD_NONE,
                    Padding = new byte[152],
                };

                if (!Ioctl(_fd, V4l2.VIDIOC_S_FMT, ref format)) continue;

                // S_FMT is a negotiation: the driver writes back what it will actually deliver,
                // which may be a different size and, if it refused, a different format entirely.
                if (format.PixelFormat != candidate) continue;

                _pixelFormat = format.PixelFormat;
                Width = (int)format.Width;
                Height = (int)format.Height;

                _log.LogInformation("Camera negotiated {Width}x{Height} {Format}.",
                    Width, Height,
                    IsNativeJpeg
                        ? $"{V4l2.DescribeFourCc(_pixelFormat)} (forwarded without re-encoding)"
                        : $"{V4l2.DescribeFourCc(_pixelFormat)} (encoded to JPEG by the agent)");

                SetFrameRate(fps);
                return;
            }

            throw new CameraCaptureException("This camera offers no video format VisionMesh can use.");
        }

        private bool IsNativeJpeg => _pixelFormat == V4l2.V4L2_PIX_FMT_MJPEG || _pixelFormat == V4l2.V4L2_PIX_FMT_JPEG;

        private void SetFrameRate(int fps)
        {
            if (fps <= 0) return;

            var parameters = new V4l2.StreamParm
            {
                Type = V4l2.V4L2_BUF_TYPE_VIDEO_CAPTURE,
                TimePerFrameNumerator = 1,
                TimePerFrameDenominator = (uint)Math.Clamp(fps, 1, 120),
                Padding = new byte[176],
            };

            // Plenty of cameras ignore this, which is fine: the agent paces frames itself and
            // simply drops what it does not need.
            if (!Ioctl(_fd, V4l2.VIDIOC_S_PARM, ref parameters))
            {
                _log.LogDebug("The camera did not accept a frame rate request; the agent will pace frames instead.");
            }
        }

        private void MapBuffers()
        {
            var request = new V4l2.RequestBuffers
            {
                Count = BufferCount,
                Type = V4l2.V4L2_BUF_TYPE_VIDEO_CAPTURE,
                Memory = V4l2.V4L2_MEMORY_MMAP,
            };

            if (!Ioctl(_fd, V4l2.VIDIOC_REQBUFS, ref request) || request.Count < 2)
            {
                throw new CameraCaptureException("The camera driver would not provide streaming buffers.");
            }

            _buffers = new (IntPtr, nuint)[request.Count];

            for (uint i = 0; i < request.Count; i++)
            {
                var buffer = new V4l2.Buffer
                {
                    Index = i,
                    Type = V4l2.V4L2_BUF_TYPE_VIDEO_CAPTURE,
                    Memory = V4l2.V4L2_MEMORY_MMAP,
                };

                if (!Ioctl(_fd, V4l2.VIDIOC_QUERYBUF, ref buffer))
                    throw new CameraCaptureException("The camera driver would not describe its buffers.");

                var address = V4l2.Mmap(IntPtr.Zero, buffer.Length,
                    V4l2.PROT_READ | V4l2.PROT_WRITE, V4l2.MAP_SHARED, _fd, (long)buffer.Offset);

                if (address == new IntPtr(-1))
                    throw new CameraCaptureException($"The camera's video buffers could not be mapped (errno {Marshal.GetLastWin32Error()}).");

                _buffers[i] = (address, buffer.Length);

                // Hand the buffer straight back to the driver so it can start filling it.
                var queue = buffer;
                if (!Ioctl(_fd, V4l2.VIDIOC_QBUF, ref queue))
                    throw new CameraCaptureException("The camera driver rejected a buffer.");
            }
        }

        private void StartStreaming()
        {
            var type = V4l2.V4L2_BUF_TYPE_VIDEO_CAPTURE;
            var buffer = Marshal.AllocHGlobal(sizeof(uint));
            try
            {
                Marshal.WriteInt32(buffer, (int)type);
                if (V4l2.IoctlRetry(_fd, V4l2.VIDIOC_STREAMON, buffer) < 0)
                    throw new CameraCaptureException($"The camera would not start streaming (errno {Marshal.GetLastWin32Error()}).");
                _streaming = true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public Task<CapturedFrame?> ReadFrameAsync(CancellationToken cancellationToken)
            => Task.Run(() => ReadFrame(cancellationToken), cancellationToken);

        private CapturedFrame? ReadFrame(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_disposed || _fd < 0) return null;

                while (!cancellationToken.IsCancellationRequested)
                {
                    // Poll with a short timeout rather than blocking on DQBUF, so stopping the
                    // camera does not have to wait for the next frame to arrive.
                    var pollFds = new[] { new V4l2.PollFd { Fd = _fd, Events = V4l2.POLLIN } };
                    var ready = V4l2.Poll(pollFds, 1, 200);

                    if (ready < 0)
                    {
                        if (Marshal.GetLastWin32Error() == V4l2.EINTR) continue;
                        throw new CameraCaptureException($"Waiting for a frame failed (errno {Marshal.GetLastWin32Error()}).");
                    }

                    if (ready == 0) continue;   // timed out; check cancellation and wait again

                    var buffer = new V4l2.Buffer
                    {
                        Type = V4l2.V4L2_BUF_TYPE_VIDEO_CAPTURE,
                        Memory = V4l2.V4L2_MEMORY_MMAP,
                    };

                    if (!Ioctl(_fd, V4l2.VIDIOC_DQBUF, ref buffer))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error is V4l2.EAGAIN or V4l2.EINTR) continue;
                        throw new CameraCaptureException($"Reading a frame failed (errno {error}).");
                    }

                    try
                    {
                        // The driver's sequence counter jumping by more than one means frames
                        // were produced that we never collected.
                        if (_haveSequence && buffer.Sequence > _lastSequence + 1)
                        {
                            DroppedFrames += buffer.Sequence - _lastSequence - 1;
                        }
                        _lastSequence = buffer.Sequence;
                        _haveSequence = true;

                        if (buffer.BytesUsed == 0 || buffer.Index >= _buffers.Length) continue;

                        var (address, _) = _buffers[buffer.Index];
                        var payload = new byte[buffer.BytesUsed];
                        Marshal.Copy(address, payload, 0, (int)buffer.BytesUsed);

                        return Convert(payload);
                    }
                    finally
                    {
                        // The buffer must go back to the driver whatever happened, or the queue
                        // drains and the camera stops after a few frames.
                        var requeue = buffer;
                        Ioctl(_fd, V4l2.VIDIOC_QBUF, ref requeue);
                    }
                }

                return null;
            }
        }

        private CapturedFrame? Convert(byte[] payload)
        {
            if (IsNativeJpeg)
            {
                return new CapturedFrame(payload, Width, Height, NativeJpeg: true);
            }

            try
            {
                byte[] jpeg;
                if (_pixelFormat == V4l2.V4L2_PIX_FMT_YUYV) jpeg = JpegEncoder.EncodeYuyv(payload, Width, Height, _quality);
                else if (_pixelFormat == V4l2.V4L2_PIX_FMT_RGB24) jpeg = JpegEncoder.EncodeRgb24(payload, Width, Height, _quality);
                else if (_pixelFormat == V4l2.V4L2_PIX_FMT_BGR24) jpeg = EncodeBgr24(payload);
                else return null;

                return new CapturedFrame(jpeg, Width, Height, NativeJpeg: false);
            }
            catch (ArgumentException ex)
            {
                // A short buffer means the driver delivered less than the format promised.
                _log.LogWarning("Discarding a malformed frame: {Error}", ex.Message);
                return null;
            }
        }

        /// <summary>BGR24 is RGB24 with the outer channels swapped, so one copy handles it.</summary>
        private byte[] EncodeBgr24(byte[] payload)
        {
            var swapped = new byte[payload.Length];
            for (var i = 0; i + 2 < payload.Length; i += 3)
            {
                swapped[i] = payload[i + 2];
                swapped[i + 1] = payload[i + 1];
                swapped[i + 2] = payload[i];
            }
            return JpegEncoder.EncodeRgb24(swapped, Width, Height, _quality);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;

                if (_fd >= 0 && _streaming)
                {
                    var buffer = Marshal.AllocHGlobal(sizeof(uint));
                    try
                    {
                        Marshal.WriteInt32(buffer, (int)V4l2.V4L2_BUF_TYPE_VIDEO_CAPTURE);
                        V4l2.IoctlRetry(_fd, V4l2.VIDIOC_STREAMOFF, buffer);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                    _streaming = false;
                }

                foreach (var (address, length) in _buffers)
                {
                    if (address != IntPtr.Zero && address != new IntPtr(-1)) V4l2.Munmap(address, length);
                }
                _buffers = Array.Empty<(IntPtr, nuint)>();

                if (_fd >= 0)
                {
                    V4l2.Close(_fd);
                    _fd = -1;
                }
            }
        }
    }
}
