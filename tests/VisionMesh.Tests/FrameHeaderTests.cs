using VisionMesh.Core.Contracts;
using Xunit;

namespace VisionMesh.Tests;

/// <summary>
/// The server half of the frame header wire contract.
///
/// The browser camera builds this header in JavaScript and the agents build it in C#. Both are
/// checked against the same fixed byte sequence - the one below, and the identical literal in
/// scripts/verify-frame-header.mjs - rather than against each other. Checking them against each
/// other would let both drift together; checking both against a literal means any change to
/// either fails a test.
///
/// This matters more than it looks. A wrong header does not throw: the server just fails to match
/// the slot, silently drops every frame, and the camera sits offline with no explanation.
/// </summary>
public class FrameHeaderTests
{
    /// <summary>slot 0x1234, sequence 0xDEADBEEF, 1700000000123 ms, 1920x1080, native JPEG.</summary>
    private static readonly byte[] ExpectedHeader =
    {
        0x56, 0x4D, 0x46, 0x31,                             // "VMF1"
        0x01,                                               // payload: JPEG
        0x01,                                               // flags: native JPEG
        0x34, 0x12,                                         // slot, little endian
        0xEF, 0xBE, 0xAD, 0xDE,                             // sequence, little endian
        0x7B, 0x68, 0xE5, 0xCF, 0x8B, 0x01, 0x00, 0x00,     // timestamp, little endian int64
        0x80, 0x07,                                         // width 1920
        0x38, 0x04,                                         // height 1080
    };

    [Fact]
    public void WritesExactlyTheBytesTheContractSpecifies()
    {
        var header = new FrameHeader(
            FramePayload.Jpeg,
            FrameFlags.NativeJpeg,
            slot: 0x1234,
            sequence: 0xDEADBEEF,
            timestampUnixMs: 1_700_000_000_123,
            width: 1920,
            height: 1080);

        var buffer = new byte[FrameHeader.Size];
        header.WriteTo(buffer);

        Assert.Equal(24, FrameHeader.Size);
        Assert.Equal(ExpectedHeader, buffer);
    }

    [Fact]
    public void ReadsBackTheBytesTheContractSpecifies()
    {
        Assert.True(FrameHeader.TryRead(ExpectedHeader, out var header));

        Assert.Equal(FramePayload.Jpeg, header.Payload);
        Assert.Equal(FrameFlags.NativeJpeg, header.Flags);
        Assert.Equal(0x1234, header.Slot);
        Assert.Equal(0xDEADBEEFu, header.Sequence);
        Assert.Equal(1_700_000_000_123, header.TimestampUnixMs);
        Assert.Equal(1920, header.Width);
        Assert.Equal(1080, header.Height);
    }

    [Fact]
    public void RoundTripsEveryFieldAtItsExtremes()
    {
        var header = new FrameHeader(
            FramePayload.Jpeg,
            FrameFlags.None,
            slot: ushort.MaxValue,
            sequence: uint.MaxValue,
            timestampUnixMs: long.MaxValue,
            width: ushort.MaxValue,
            height: ushort.MaxValue);

        var buffer = new byte[FrameHeader.Size];
        header.WriteTo(buffer);

        Assert.True(FrameHeader.TryRead(buffer, out var parsed));
        Assert.Equal(ushort.MaxValue, parsed.Slot);
        Assert.Equal(uint.MaxValue, parsed.Sequence);
        Assert.Equal(long.MaxValue, parsed.TimestampUnixMs);
        Assert.Equal(ushort.MaxValue, parsed.Width);
        Assert.Equal(ushort.MaxValue, parsed.Height);
    }

    [Fact]
    public void RejectsAnythingWithoutTheMagicPrefix()
    {
        // A stray text message or a truncated frame must be dropped, not misread as video.
        Assert.False(FrameHeader.TryRead(new byte[FrameHeader.Size], out _));
        Assert.False(FrameHeader.TryRead("not a visionmesh frame!!"u8, out _));
    }

    [Fact]
    public void RejectsABufferShorterThanTheHeader()
    {
        for (var length = 0; length < FrameHeader.Size; length++)
        {
            Assert.False(FrameHeader.TryRead(ExpectedHeader.AsSpan(0, length), out _),
                $"A {length} byte buffer was accepted as a header.");
        }
    }

    [Fact]
    public void RefusesToWriteIntoABufferThatIsTooSmall()
    {
        var header = new FrameHeader(FramePayload.Jpeg, FrameFlags.None, 1, 1, 1, 640, 480);
        Assert.Throws<ArgumentException>(() => header.WriteTo(new byte[FrameHeader.Size - 1]));
    }
}
