using System.Runtime.InteropServices;
using VisionMesh.Agent.Linux.Capture;
using Xunit;

namespace VisionMesh.Tests;

/// <summary>
/// Checks the Video4Linux2 struct layouts against the sizes the kernel expects.
///
/// This test exists because the failure it catches is invisible any other way. A V4L2 ioctl
/// request number encodes sizeof(struct) in bits 16-29, so a struct that is even one byte wrong
/// produces a request the kernel rejects with ENOTTY - and the symptom a user sees is "my camera
/// does not work on Linux", with nothing in the logs pointing at a struct definition.
///
/// The sizes are recovered from the ioctl constants themselves rather than written out again, so
/// the constant and the struct are checked against each other rather than both against a third
/// copy of the same assumption. It runs on any platform, which is the point: the layouts can be
/// verified without Linux hardware.
/// </summary>
public class V4l2LayoutTests
{
    /// <summary>Extracts the struct size the kernel encoded into an ioctl request number.</summary>
    private static int SizeFromRequest(ulong request) => (int)((request >> 16) & 0x3FFF);

    [Fact]
    public void CapabilityMatchesQueryCap()
        => Assert.Equal(SizeFromRequest(V4l2.VIDIOC_QUERYCAP), Marshal.SizeOf<V4l2.Capability>());

    [Fact]
    public void FormatDescriptionMatchesEnumFmt()
        => Assert.Equal(SizeFromRequest(V4l2.VIDIOC_ENUM_FMT), Marshal.SizeOf<V4l2.FormatDescription>());

    [Fact]
    public void FormatMatchesGetAndSetFormat()
    {
        Assert.Equal(SizeFromRequest(V4l2.VIDIOC_S_FMT), Marshal.SizeOf<V4l2.Format>());
        Assert.Equal(SizeFromRequest(V4l2.VIDIOC_G_FMT), Marshal.SizeOf<V4l2.Format>());
    }

    [Fact]
    public void RequestBuffersMatchesReqBufs()
        => Assert.Equal(SizeFromRequest(V4l2.VIDIOC_REQBUFS), Marshal.SizeOf<V4l2.RequestBuffers>());

    [Fact]
    public void BufferMatchesQueryQueueAndDequeue()
    {
        var expected = SizeFromRequest(V4l2.VIDIOC_QUERYBUF);
        Assert.Equal(expected, Marshal.SizeOf<V4l2.Buffer>());
        Assert.Equal(expected, SizeFromRequest(V4l2.VIDIOC_QBUF));
        Assert.Equal(expected, SizeFromRequest(V4l2.VIDIOC_DQBUF));
    }

    [Fact]
    public void FrameSizeEnumMatchesEnumFrameSizes()
        => Assert.Equal(SizeFromRequest(V4l2.VIDIOC_ENUM_FRAMESIZES), Marshal.SizeOf<V4l2.FrameSizeEnum>());

    [Fact]
    public void FrameIntervalEnumMatchesEnumFrameIntervals()
        => Assert.Equal(SizeFromRequest(V4l2.VIDIOC_ENUM_FRAMEINTERVALS), Marshal.SizeOf<V4l2.FrameIntervalEnum>());

    [Fact]
    public void StreamParmMatchesGetAndSetParm()
    {
        Assert.Equal(SizeFromRequest(V4l2.VIDIOC_S_PARM), Marshal.SizeOf<V4l2.StreamParm>());
        Assert.Equal(SizeFromRequest(V4l2.VIDIOC_G_PARM), Marshal.SizeOf<V4l2.StreamParm>());
    }

    [Fact]
    public void BufferFieldsSitWhereTheKernelPutsThem()
    {
        // The kernel aligns the timestamp to eight bytes, leaving a four byte hole after Field.
        // A plain sequential layout would close that hole and shift every field after it.
        Assert.Equal(0, Marshal.OffsetOf<V4l2.Buffer>(nameof(V4l2.Buffer.Index)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<V4l2.Buffer>(nameof(V4l2.Buffer.BytesUsed)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<V4l2.Buffer>(nameof(V4l2.Buffer.TimestampSeconds)).ToInt32());
        Assert.Equal(56, Marshal.OffsetOf<V4l2.Buffer>(nameof(V4l2.Buffer.Sequence)).ToInt32());
        Assert.Equal(60, Marshal.OffsetOf<V4l2.Buffer>(nameof(V4l2.Buffer.Memory)).ToInt32());
        Assert.Equal(64, Marshal.OffsetOf<V4l2.Buffer>(nameof(V4l2.Buffer.Offset)).ToInt32());
        Assert.Equal(72, Marshal.OffsetOf<V4l2.Buffer>(nameof(V4l2.Buffer.Length)).ToInt32());
    }

    [Fact]
    public void ThePixelFormatBeginsAfterTheUnionsAlignmentHole()
    {
        // This test used to assert offset 4 and was wrong, which is how the bug it now guards
        // survived: v4l2_format's fmt union contains v4l2_window, which holds pointers, so on
        // 64-bit the union is eight byte aligned and pix starts at offset 8.
        //
        // Written at offset 4, width lands in the padding, height in width and the pixel format
        // in height. The kernel answers EINVAL for every format offered, which looks exactly like
        // a camera VisionMesh cannot use. Sizes still matched, which is why only running against
        // a real V4L2 device found it.
        Assert.Equal(8, Marshal.OffsetOf<V4l2.Format>(nameof(V4l2.Format.Width)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<V4l2.Format>(nameof(V4l2.Format.Height)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<V4l2.Format>(nameof(V4l2.Format.PixelFormat)).ToInt32());
    }

    [Fact]
    public void TheFourByteHoleIsProvenByTheIoctlNumbersThemselves()
    {
        // An offset assertion is only as good as the reasoning behind it, so here is the same
        // claim from evidence already in the repository rather than from memory.
        //
        // v4l2_format and v4l2_streamparm are the same shape: a __u32 type tag followed by a
        // 200 byte union. The sizes encoded in their ioctl request numbers differ by exactly
        // four, because only v4l2_format's union contains a pointer and so is eight byte aligned.
        var formatSize = SizeFromRequest(V4l2.VIDIOC_S_FMT);
        var parmSize = SizeFromRequest(V4l2.VIDIOC_S_PARM);

        Assert.Equal(204, parmSize);
        Assert.Equal(208, formatSize);
        Assert.Equal(4, formatSize - parmSize);

        // Which is to say: pix begins four bytes later than parm's union does.
        Assert.Equal(
            Marshal.OffsetOf<V4l2.StreamParm>(nameof(V4l2.StreamParm.Capability)).ToInt32() + 4,
            Marshal.OffsetOf<V4l2.Format>(nameof(V4l2.Format.Width)).ToInt32());
    }

    [Theory]
    [InlineData("MJPG", 0x47504A4D)]
    [InlineData("YUYV", 0x56595559)]
    [InlineData("RGB3", 0x33424752)]
    [InlineData("H264", 0x34363248)]
    public void FourCcValuesMatchTheKernelConstants(string code, uint expected)
        => Assert.Equal(expected, V4l2.FourCc(code[0], code[1], code[2], code[3]));

    [Fact]
    public void FourCcRoundTripsBackToItsText()
    {
        Assert.Equal("MJPG", V4l2.DescribeFourCc(V4l2.V4L2_PIX_FMT_MJPEG));
        Assert.Equal("YUYV", V4l2.DescribeFourCc(V4l2.V4L2_PIX_FMT_YUYV));
    }
}
