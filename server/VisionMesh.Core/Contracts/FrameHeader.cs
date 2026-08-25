using System.Buffers.Binary;

namespace VisionMesh.Core.Contracts;

/// <summary>
/// Fixed 24-byte prefix on every binary frame message.
///
/// Layout (little endian):
///   0..3   magic "VMF1"
///   4      payload kind (<see cref="FramePayload"/>)
///   5      flags (<see cref="FrameFlags"/>)
///   6..7   slot (ushort, assigned by the server in start-capture)
///   8..11  sequence number (uint, per slot, wraps)
///   12..19 capture timestamp, unix milliseconds UTC (long)
///   20..21 width  (ushort, 0 when unknown)
///   22..23 height (ushort, 0 when unknown)
/// </summary>
public readonly struct FrameHeader
{
    public const int Size = 24;
    private static readonly byte[] Magic = "VMF1"u8.ToArray();

    public FramePayload Payload { get; }
    public FrameFlags Flags { get; }
    public ushort Slot { get; }
    public uint Sequence { get; }
    public long TimestampUnixMs { get; }
    public ushort Width { get; }
    public ushort Height { get; }

    public FrameHeader(FramePayload payload, FrameFlags flags, ushort slot, uint sequence,
                       long timestampUnixMs, ushort width, ushort height)
    {
        Payload = payload;
        Flags = flags;
        Slot = slot;
        Sequence = sequence;
        TimestampUnixMs = timestampUnixMs;
        Width = width;
        Height = height;
    }

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size) throw new ArgumentException("Buffer too small for frame header.", nameof(destination));
        Magic.CopyTo(destination);
        destination[4] = (byte)Payload;
        destination[5] = (byte)Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], Slot);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(destination[12..], TimestampUnixMs);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[20..], Width);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[22..], Height);
    }

    public static bool TryRead(ReadOnlySpan<byte> source, out FrameHeader header)
    {
        header = default;
        if (source.Length < Size) return false;
        if (source[0] != Magic[0] || source[1] != Magic[1] || source[2] != Magic[2] || source[3] != Magic[3]) return false;
        header = new FrameHeader(
            (FramePayload)source[4],
            (FrameFlags)source[5],
            BinaryPrimitives.ReadUInt16LittleEndian(source[6..]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[12..]),
            BinaryPrimitives.ReadUInt16LittleEndian(source[20..]),
            BinaryPrimitives.ReadUInt16LittleEndian(source[22..]));
        return true;
    }
}

public enum FramePayload : byte
{
    Jpeg = 1,
}

[Flags]
public enum FrameFlags : byte
{
    None = 0,
    /// <summary>Frame was produced by the source in JPEG form and was not re-encoded by the agent.</summary>
    NativeJpeg = 1,
}
