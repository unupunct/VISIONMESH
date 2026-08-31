using System.Runtime.InteropServices;

namespace VisionMesh.Agent.Linux.Capture;

/// <summary>
/// Video4Linux2 declarations.
///
/// The ioctl request numbers are written out as literals rather than computed from the _IOC
/// macros. That is deliberate: the encoding folds sizeof(struct) into the request number, so a
/// struct that is one byte off produces a request the kernel simply rejects with ENOTTY, and the
/// resulting bug looks like "the camera does not work" rather than "the struct is wrong". Using
/// the published constants makes the expected struct size checkable by inspection - the size is
/// bits 16-29 of the request - and each one is noted below.
/// </summary>
internal static class V4l2
{
    // ---- ioctl requests. The middle bytes carry the expected sizeof(struct). ----
    public const ulong VIDIOC_QUERYCAP = 0x80685600;             // size 0x068 = 104
    public const ulong VIDIOC_ENUM_FMT = 0xc0405602;             // size 0x040 = 64
    public const ulong VIDIOC_G_FMT = 0xc0d05604;                // size 0x0d0 = 208
    public const ulong VIDIOC_S_FMT = 0xc0d05605;                // size 0x0d0 = 208
    public const ulong VIDIOC_REQBUFS = 0xc0145608;              // size 0x014 = 20
    public const ulong VIDIOC_QUERYBUF = 0xc0585609;             // size 0x058 = 88
    public const ulong VIDIOC_QBUF = 0xc058560f;                 // size 0x058 = 88
    public const ulong VIDIOC_DQBUF = 0xc0585611;                // size 0x058 = 88
    public const ulong VIDIOC_STREAMON = 0x40045612;             // size 0x004 = 4
    public const ulong VIDIOC_STREAMOFF = 0x40045613;            // size 0x004 = 4
    public const ulong VIDIOC_G_PARM = 0xc0cc5615;               // size 0x0cc = 204
    public const ulong VIDIOC_S_PARM = 0xc0cc5616;               // size 0x0cc = 204
    public const ulong VIDIOC_ENUM_FRAMESIZES = 0xc02c564a;      // size 0x02c = 44
    public const ulong VIDIOC_ENUM_FRAMEINTERVALS = 0xc034564b;  // size 0x034 = 52

    public const uint V4L2_BUF_TYPE_VIDEO_CAPTURE = 1;
    public const uint V4L2_MEMORY_MMAP = 1;
    public const uint V4L2_FIELD_ANY = 0;
    public const uint V4L2_FIELD_NONE = 1;

    public const uint V4L2_CAP_VIDEO_CAPTURE = 0x00000001;
    public const uint V4L2_CAP_STREAMING = 0x04000000;
    public const uint V4L2_CAP_DEVICE_CAPS = 0x80000000;

    public const uint V4L2_FRMSIZE_TYPE_DISCRETE = 1;
    public const uint V4L2_CAP_TIMEPERFRAME = 0x1000;

    // Pixel formats, as FourCC values.
    public static readonly uint V4L2_PIX_FMT_MJPEG = FourCc('M', 'J', 'P', 'G');
    public static readonly uint V4L2_PIX_FMT_JPEG = FourCc('J', 'P', 'E', 'G');
    public static readonly uint V4L2_PIX_FMT_YUYV = FourCc('Y', 'U', 'Y', 'V');
    public static readonly uint V4L2_PIX_FMT_YVYU = FourCc('Y', 'V', 'Y', 'U');
    public static readonly uint V4L2_PIX_FMT_UYVY = FourCc('U', 'Y', 'V', 'Y');
    public static readonly uint V4L2_PIX_FMT_RGB24 = FourCc('R', 'G', 'B', '3');
    public static readonly uint V4L2_PIX_FMT_BGR24 = FourCc('B', 'G', 'R', '3');
    public static readonly uint V4L2_PIX_FMT_H264 = FourCc('H', '2', '6', '4');
    public static readonly uint V4L2_PIX_FMT_NV12 = FourCc('N', 'V', '1', '2');

    public static uint FourCc(char a, char b, char c, char d)
        => (uint)a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);

    public static string DescribeFourCc(uint value)
    {
        var text = new string(new[] { (char)(value & 0xFF), (char)((value >> 8) & 0xFF), (char)((value >> 16) & 0xFF), (char)((value >> 24) & 0xFF) });
        return text.All(c => c is >= ' ' and <= '~') ? text.Trim() : $"0x{value:X8}";
    }

    // ---- libc ----
    public const int O_RDWR = 0x0002;
    public const int O_NONBLOCK = 0x0800;
    public const int EINTR = 4;
    public const int EAGAIN = 11;
    public const int EINVAL = 22;
    public const int EACCES = 13;
    public const int EBUSY = 16;
    public const int ENODEV = 19;

    public const int PROT_READ = 0x1;
    public const int PROT_WRITE = 0x2;
    public const int MAP_SHARED = 0x01;
    public const short POLLIN = 0x001;

    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    public static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true, EntryPoint = "close")]
    public static extern int Close(int fd);

    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    public static extern int Ioctl(int fd, ulong request, IntPtr argument);

    [DllImport("libc", SetLastError = true, EntryPoint = "mmap")]
    public static extern IntPtr Mmap(IntPtr address, nuint length, int protection, int flags, int fd, long offset);

    [DllImport("libc", SetLastError = true, EntryPoint = "munmap")]
    public static extern int Munmap(IntPtr address, nuint length);

    [DllImport("libc", SetLastError = true, EntryPoint = "poll")]
    public static extern int Poll([In, Out] PollFd[] fds, uint count, int timeoutMilliseconds);

    /// <summary>Retries an ioctl through EINTR, which a signal can cause at any time.</summary>
    public static int IoctlRetry(int fd, ulong request, IntPtr argument)
    {
        int result;
        do
        {
            result = Ioctl(fd, request, argument);
        }
        while (result == -1 && Marshal.GetLastWin32Error() == EINTR);

        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [StructLayout(LayoutKind.Sequential, Size = 104)]
    public struct Capability
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Driver;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Card;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] BusInfo;
        public uint Version;
        public uint Capabilities;
        public uint DeviceCaps;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public uint[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    public struct FormatDescription
    {
        public uint Index;
        public uint Type;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Description;
        public uint PixelFormat;
        public uint MbusCode;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public uint[] Reserved;
    }

    /// <summary>
    /// v4l2_format. The kernel's union is 200 bytes; only the pix member is used here, and the
    /// rest is padding so the struct matches the size baked into the ioctl request number.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 208)]
    public struct Format
    {
        public uint Type;

        // struct v4l2_pix_format
        public uint Width;
        public uint Height;
        public uint PixelFormat;
        public uint Field;
        public uint BytesPerLine;
        public uint SizeImage;
        public uint Colorspace;
        public uint Priv;
        public uint Flags;
        public uint YcbcrEncoding;
        public uint Quantization;
        public uint TransferFunction;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 152)] public byte[] Padding;
    }

    [StructLayout(LayoutKind.Sequential, Size = 20)]
    public struct RequestBuffers
    {
        public uint Count;
        public uint Type;
        public uint Memory;
        public uint Capabilities;
        public uint Reserved;
    }

    /// <summary>
    /// v4l2_buffer, 88 bytes on 64-bit. The explicit offsets matter: the kernel aligns the
    /// timestamp to eight bytes, which leaves a four byte hole after Field that a sequential
    /// layout would not otherwise reproduce.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 88)]
    public struct Buffer
    {
        [FieldOffset(0)] public uint Index;
        [FieldOffset(4)] public uint Type;
        [FieldOffset(8)] public uint BytesUsed;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Field;
        // 20..23 is padding inserted by the kernel's alignment of the timeval below.
        [FieldOffset(24)] public long TimestampSeconds;
        [FieldOffset(32)] public long TimestampMicroseconds;
        // 40..55 is struct v4l2_timecode, which this agent does not use.
        [FieldOffset(56)] public uint Sequence;
        [FieldOffset(60)] public uint Memory;
        [FieldOffset(64)] public ulong Offset;      // union m: offset for MMAP buffers
        [FieldOffset(72)] public uint Length;
        [FieldOffset(76)] public uint Reserved2;
        [FieldOffset(80)] public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Size = 44)]
    public struct FrameSizeEnum
    {
        public uint Index;
        public uint PixelFormat;
        public uint Type;

        // Discrete sizes use the first two fields; stepwise uses all six.
        public uint Width;
        public uint Height;
        public uint MaxWidth;
        public uint MaxHeight;
        public uint StepWidth;
        public uint StepHeight;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public uint[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Size = 52)]
    public struct FrameIntervalEnum
    {
        public uint Index;
        public uint PixelFormat;
        public uint Width;
        public uint Height;
        public uint Type;

        public uint Numerator;
        public uint Denominator;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)] public byte[] Padding;
    }

    [StructLayout(LayoutKind.Sequential, Size = 204)]
    public struct StreamParm
    {
        public uint Type;

        // struct v4l2_captureparm
        public uint Capability;
        public uint CaptureMode;
        public uint TimePerFrameNumerator;
        public uint TimePerFrameDenominator;
        public uint ExtendedMode;
        public uint ReadBuffers;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 176)] public byte[] Padding;
    }

    public static string ReadFixedString(byte[]? value)
    {
        if (value is null) return "";
        var length = Array.IndexOf(value, (byte)0);
        if (length < 0) length = value.Length;
        return System.Text.Encoding.UTF8.GetString(value, 0, length).Trim();
    }
}
