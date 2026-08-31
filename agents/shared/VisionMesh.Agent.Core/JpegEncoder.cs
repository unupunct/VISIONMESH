namespace VisionMesh.Agent.Core;

/// <summary>
/// A baseline JPEG encoder, good enough for surveillance frames and dependent on nothing.
///
/// Why write one: the Linux agent has no System.Drawing, and every managed imaging library either
/// carries native binaries, a restrictive licence, or a history of decoder CVEs. An encoder is
/// also the safe half of the problem - it only ever reads pixel buffers the agent produced
/// itself, never bytes from the network.
///
/// It is deliberately plain: 4:2:0 subsampling, the standard Annex K quantisation and Huffman
/// tables, no optimisation passes. A camera that can emit MJPEG never reaches this code, so this
/// is the fallback for cameras that only offer raw formats.
/// </summary>
public static class JpegEncoder
{
    // Annex K luminance quantisation table, in zigzag order applied later.
    private static readonly int[] LuminanceQuant =
    {
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99,
    };

    private static readonly int[] ChrominanceQuant =
    {
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    };

    private static readonly int[] ZigZag =
    {
        0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    };

    // Standard Annex K Huffman tables: bit-length counts followed by symbol values.
    private static readonly byte[] LuminanceDcBits = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] LuminanceDcValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

    private static readonly byte[] ChrominanceDcBits = { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
    private static readonly byte[] ChrominanceDcValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

    private static readonly byte[] LuminanceAcBits = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d };
    private static readonly byte[] LuminanceAcValues =
    {
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08, 0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
        0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5,
        0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    };

    private static readonly byte[] ChrominanceAcBits = { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };
    private static readonly byte[] ChrominanceAcValues =
    {
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0,
        0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5,
        0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3,
        0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    };

    /// <summary>Huffman code table: code and length per symbol, built once per table.</summary>
    private sealed class HuffmanTable
    {
        public readonly ushort[] Codes = new ushort[256];
        public readonly byte[] Lengths = new byte[256];

        public HuffmanTable(byte[] bits, byte[] values)
        {
            var code = 0;
            var index = 0;
            for (var length = 1; length <= 16; length++)
            {
                for (var i = 0; i < bits[length - 1]; i++)
                {
                    Codes[values[index]] = (ushort)code;
                    Lengths[values[index]] = (byte)length;
                    index++;
                    code++;
                }
                code <<= 1;
            }
        }
    }

    private static readonly HuffmanTable LuminanceDc = new(LuminanceDcBits, LuminanceDcValues);
    private static readonly HuffmanTable LuminanceAc = new(LuminanceAcBits, LuminanceAcValues);
    private static readonly HuffmanTable ChrominanceDc = new(ChrominanceDcBits, ChrominanceDcValues);
    private static readonly HuffmanTable ChrominanceAc = new(ChrominanceAcBits, ChrominanceAcValues);

    /// <summary>
    /// Encodes a packed YUV 4:2:2 buffer (YUYV, the most common raw webcam format) as JPEG.
    /// Encoding straight from YUYV skips a conversion to RGB and back, which is the whole reason
    /// the camera hands it over in that form.
    /// </summary>
    public static byte[] EncodeYuyv(ReadOnlySpan<byte> yuyv, int width, int height, int quality)
    {
        var (luma, blueChroma, redChroma) = PlanarFromYuyv(yuyv, width, height);
        return Encode(luma, blueChroma, redChroma, width, height, quality);
    }

    /// <summary>Encodes a packed RGB24 buffer as JPEG.</summary>
    public static byte[] EncodeRgb24(ReadOnlySpan<byte> rgb, int width, int height, int quality)
    {
        var (luma, blueChroma, redChroma) = PlanarFromRgb(rgb, width, height, 3, 0, 1, 2);
        return Encode(luma, blueChroma, redChroma, width, height, quality);
    }

    /// <summary>Encodes a packed BGR32 buffer (B, G, R, unused) as JPEG.</summary>
    public static byte[] EncodeBgr32(ReadOnlySpan<byte> bgr, int width, int height, int quality)
    {
        var (luma, blueChroma, redChroma) = PlanarFromRgb(bgr, width, height, 4, 2, 1, 0);
        return Encode(luma, blueChroma, redChroma, width, height, quality);
    }

    private static (float[] Y, float[] Cb, float[] Cr) PlanarFromYuyv(ReadOnlySpan<byte> yuyv, int width, int height)
    {
        var luma = new float[width * height];
        var blueChroma = new float[width * height];
        var redChroma = new float[width * height];

        var expected = (long)width * height * 2;
        if (yuyv.Length < expected) throw new ArgumentException("The YUYV buffer is smaller than the frame it describes.");

        for (var y = 0; y < height; y++)
        {
            var rowStart = y * width * 2;
            for (var x = 0; x < width; x += 2)
            {
                var offset = rowStart + x * 2;
                var y0 = yuyv[offset];
                var u = yuyv[offset + 1];
                var y1 = yuyv[offset + 2];
                var v = yuyv[offset + 3];

                var index = y * width + x;
                luma[index] = y0;
                blueChroma[index] = u;
                redChroma[index] = v;

                if (x + 1 < width)
                {
                    // The chroma pair is shared by both pixels, which is what 4:2:2 means.
                    luma[index + 1] = y1;
                    blueChroma[index + 1] = u;
                    redChroma[index + 1] = v;
                }
            }
        }

        return (luma, blueChroma, redChroma);
    }

    private static (float[] Y, float[] Cb, float[] Cr) PlanarFromRgb(
        ReadOnlySpan<byte> pixels, int width, int height, int stride, int redOffset, int greenOffset, int blueOffset)
    {
        var luma = new float[width * height];
        var blueChroma = new float[width * height];
        var redChroma = new float[width * height];

        var expected = (long)width * height * stride;
        if (pixels.Length < expected) throw new ArgumentException("The pixel buffer is smaller than the frame it describes.");

        for (var i = 0; i < width * height; i++)
        {
            var offset = i * stride;
            float red = pixels[offset + redOffset];
            float green = pixels[offset + greenOffset];
            float blue = pixels[offset + blueOffset];

            // ITU-R BT.601, which is what JPEG expects.
            luma[i] = 0.299f * red + 0.587f * green + 0.114f * blue;
            blueChroma[i] = 128f - 0.168736f * red - 0.331264f * green + 0.5f * blue;
            redChroma[i] = 128f + 0.5f * red - 0.418688f * green - 0.081312f * blue;
        }

        return (luma, blueChroma, redChroma);
    }

    private static byte[] Encode(float[] luma, float[] blueChroma, float[] redChroma, int width, int height, int quality)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("The frame has no size.");

        var lumaQuant = ScaleQuantTable(LuminanceQuant, quality);
        var chromaQuant = ScaleQuantTable(ChrominanceQuant, quality);

        using var stream = new MemoryStream(width * height / 2 + 1024);
        var writer = new BitWriter(stream);

        WriteHeaders(stream, width, height, lumaQuant, chromaQuant);

        // 4:2:0: one chroma sample per 2x2 luma block, so the MCU is 16x16 pixels.
        var mcusWide = (width + 15) / 16;
        var mcusHigh = (height + 15) / 16;

        int lumaDc = 0, blueDc = 0, redDc = 0;
        var block = new float[64];

        for (var mcuY = 0; mcuY < mcusHigh; mcuY++)
        {
            for (var mcuX = 0; mcuX < mcusWide; mcuX++)
            {
                // Four luma blocks per MCU.
                for (var subY = 0; subY < 2; subY++)
                {
                    for (var subX = 0; subX < 2; subX++)
                    {
                        ExtractBlock(luma, width, height, mcuX * 16 + subX * 8, mcuY * 16 + subY * 8, block);
                        lumaDc = EncodeBlock(writer, block, lumaQuant, LuminanceDc, LuminanceAc, lumaDc);
                    }
                }

                // One downsampled block for each chroma channel.
                ExtractDownsampledBlock(blueChroma, width, height, mcuX * 16, mcuY * 16, block);
                blueDc = EncodeBlock(writer, block, chromaQuant, ChrominanceDc, ChrominanceAc, blueDc);

                ExtractDownsampledBlock(redChroma, width, height, mcuX * 16, mcuY * 16, block);
                redDc = EncodeBlock(writer, block, chromaQuant, ChrominanceDc, ChrominanceAc, redDc);
            }
        }

        writer.Flush();
        stream.WriteByte(0xFF);
        stream.WriteByte(0xD9);   // EOI

        return stream.ToArray();
    }

    /// <summary>
    /// Scales the standard tables for a 1-100 quality value, using the conventional libjpeg curve.
    /// </summary>
    private static int[] ScaleQuantTable(int[] table, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        var scale = quality < 50 ? 5000 / quality : 200 - quality * 2;

        var scaled = new int[64];
        for (var i = 0; i < 64; i++)
        {
            scaled[i] = Math.Clamp((table[i] * scale + 50) / 100, 1, 255);
        }
        return scaled;
    }

    /// <summary>
    /// Copies an 8x8 block, clamping reads at the frame edge so a frame whose size is not a
    /// multiple of 16 is padded by repeating its last row and column rather than with black,
    /// which would show as a dark fringe.
    /// </summary>
    private static void ExtractBlock(float[] plane, int width, int height, int originX, int originY, float[] block)
    {
        for (var y = 0; y < 8; y++)
        {
            var sourceY = Math.Min(originY + y, height - 1);
            for (var x = 0; x < 8; x++)
            {
                var sourceX = Math.Min(originX + x, width - 1);
                block[y * 8 + x] = plane[sourceY * width + sourceX] - 128f;
            }
        }
    }

    /// <summary>Averages each 2x2 group into one sample, producing the 4:2:0 chroma block.</summary>
    private static void ExtractDownsampledBlock(float[] plane, int width, int height, int originX, int originY, float[] block)
    {
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var baseX = originX + x * 2;
                var baseY = originY + y * 2;

                var x0 = Math.Min(baseX, width - 1);
                var x1 = Math.Min(baseX + 1, width - 1);
                var y0 = Math.Min(baseY, height - 1);
                var y1 = Math.Min(baseY + 1, height - 1);

                var sum = plane[y0 * width + x0] + plane[y0 * width + x1]
                        + plane[y1 * width + x0] + plane[y1 * width + x1];

                block[y * 8 + x] = sum / 4f - 128f;
            }
        }
    }

    private static int EncodeBlock(BitWriter writer, float[] block, int[] quant, HuffmanTable dcTable, HuffmanTable acTable, int previousDc)
    {
        Span<float> transformed = stackalloc float[64];
        ForwardDct(block, transformed);

        Span<int> quantised = stackalloc int[64];
        for (var i = 0; i < 64; i++)
        {
            // The zigzag reorder happens here, so the coefficients come out in the order the
            // entropy coder expects.
            var value = transformed[ZigZag[i]] / quant[ZigZag[i]];
            quantised[i] = (int)MathF.Round(value);
        }

        // DC is coded as a difference from the previous block of the same component.
        var dcDifference = quantised[0] - previousDc;
        var (dcSize, dcBits) = Magnitude(dcDifference);
        writer.Write(dcTable.Codes[dcSize], dcTable.Lengths[dcSize]);
        if (dcSize > 0) writer.Write(dcBits, dcSize);

        var runLength = 0;
        for (var i = 1; i < 64; i++)
        {
            if (quantised[i] == 0)
            {
                runLength++;
                continue;
            }

            // Runs longer than 15 zeroes need explicit ZRL symbols.
            while (runLength > 15)
            {
                writer.Write(acTable.Codes[0xF0], acTable.Lengths[0xF0]);
                runLength -= 16;
            }

            var (size, bits) = Magnitude(quantised[i]);
            var symbol = (runLength << 4) | size;
            writer.Write(acTable.Codes[symbol], acTable.Lengths[symbol]);
            writer.Write(bits, size);
            runLength = 0;
        }

        if (runLength > 0) writer.Write(acTable.Codes[0x00], acTable.Lengths[0x00]);   // EOB

        return quantised[0];
    }

    /// <summary>
    /// Separable 8x8 forward DCT: rows then columns, each a straightforward summation.
    /// A fast integer DCT would be several times quicker, but this path only runs for cameras
    /// that cannot produce JPEG themselves, and correctness is worth more here than speed.
    /// </summary>
    private static void ForwardDct(float[] input, Span<float> output)
    {
        Span<float> intermediate = stackalloc float[64];

        for (var y = 0; y < 8; y++)
        {
            for (var u = 0; u < 8; u++)
            {
                var sum = 0f;
                for (var x = 0; x < 8; x++) sum += input[y * 8 + x] * CosineTable[x * 8 + u];
                intermediate[y * 8 + u] = sum * (u == 0 ? Inverse2Sqrt2 : 0.5f);
            }
        }

        for (var u = 0; u < 8; u++)
        {
            for (var v = 0; v < 8; v++)
            {
                var sum = 0f;
                for (var y = 0; y < 8; y++) sum += intermediate[y * 8 + u] * CosineTable[y * 8 + v];
                output[v * 8 + u] = sum * (v == 0 ? Inverse2Sqrt2 : 0.5f);
            }
        }
    }

    private const float Inverse2Sqrt2 = 0.353553391f;   // 1 / (2 * sqrt(2))

    private static readonly float[] CosineTable = BuildCosineTable();

    private static float[] BuildCosineTable()
    {
        var table = new float[64];
        for (var x = 0; x < 8; x++)
        {
            for (var u = 0; u < 8; u++)
            {
                table[x * 8 + u] = MathF.Cos((2 * x + 1) * u * MathF.PI / 16f);
            }
        }
        return table;
    }

    /// <summary>Splits a coefficient into its JPEG magnitude category and its variable-length bits.</summary>
    private static (int Size, int Bits) Magnitude(int value)
    {
        if (value == 0) return (0, 0);

        var magnitude = Math.Abs(value);
        var size = 0;
        while (magnitude > 0) { size++; magnitude >>= 1; }

        // Negative values are stored as the one's complement of their magnitude.
        var bits = value > 0 ? value : value + (1 << size) - 1;
        return (size, bits);
    }

    private static void WriteHeaders(Stream stream, int width, int height, int[] lumaQuant, int[] chromaQuant)
    {
        void Marker(byte marker) { stream.WriteByte(0xFF); stream.WriteByte(marker); }
        void Length(int length) { stream.WriteByte((byte)(length >> 8)); stream.WriteByte((byte)length); }

        Marker(0xD8);   // SOI

        // JFIF header, so every viewer recognises the file without sniffing.
        Marker(0xE0);
        Length(16);
        stream.Write("JFIF\0"u8);
        stream.WriteByte(1); stream.WriteByte(1);   // version 1.1
        stream.WriteByte(0);                        // units: none
        stream.WriteByte(0); stream.WriteByte(1);   // x density
        stream.WriteByte(0); stream.WriteByte(1);   // y density
        stream.WriteByte(0); stream.WriteByte(0);   // no thumbnail

        // Quantisation tables, written in zigzag order as the standard requires.
        Marker(0xDB);
        Length(2 + 65 + 65);
        stream.WriteByte(0x00);
        for (var i = 0; i < 64; i++) stream.WriteByte((byte)lumaQuant[ZigZag[i]]);
        stream.WriteByte(0x01);
        for (var i = 0; i < 64; i++) stream.WriteByte((byte)chromaQuant[ZigZag[i]]);

        // SOF0: baseline, three components, 4:2:0 sampling.
        Marker(0xC0);
        Length(17);
        stream.WriteByte(8);                                    // 8 bits per sample
        stream.WriteByte((byte)(height >> 8)); stream.WriteByte((byte)height);
        stream.WriteByte((byte)(width >> 8)); stream.WriteByte((byte)width);
        stream.WriteByte(3);
        stream.WriteByte(1); stream.WriteByte(0x22); stream.WriteByte(0);   // Y, 2x2, quant table 0
        stream.WriteByte(2); stream.WriteByte(0x11); stream.WriteByte(1);   // Cb, 1x1, quant table 1
        stream.WriteByte(3); stream.WriteByte(0x11); stream.WriteByte(1);   // Cr, 1x1, quant table 1

        // Huffman tables.
        Marker(0xC4);
        Length(2 + (1 + 16 + LuminanceDcValues.Length) + (1 + 16 + LuminanceAcValues.Length)
                 + (1 + 16 + ChrominanceDcValues.Length) + (1 + 16 + ChrominanceAcValues.Length));
        WriteHuffmanTable(stream, 0x00, LuminanceDcBits, LuminanceDcValues);
        WriteHuffmanTable(stream, 0x10, LuminanceAcBits, LuminanceAcValues);
        WriteHuffmanTable(stream, 0x01, ChrominanceDcBits, ChrominanceDcValues);
        WriteHuffmanTable(stream, 0x11, ChrominanceAcBits, ChrominanceAcValues);

        // SOS.
        Marker(0xDA);
        Length(12);
        stream.WriteByte(3);
        stream.WriteByte(1); stream.WriteByte(0x00);   // Y uses DC table 0, AC table 0
        stream.WriteByte(2); stream.WriteByte(0x11);   // Cb uses DC table 1, AC table 1
        stream.WriteByte(3); stream.WriteByte(0x11);   // Cr uses DC table 1, AC table 1
        stream.WriteByte(0); stream.WriteByte(63); stream.WriteByte(0);
    }

    private static void WriteHuffmanTable(Stream stream, byte identifier, byte[] bits, byte[] values)
    {
        stream.WriteByte(identifier);
        stream.Write(bits, 0, 16);
        stream.Write(values, 0, values.Length);
    }

    /// <summary>MSB-first bit writer that stuffs a zero after every 0xFF, as JPEG requires.</summary>
    private sealed class BitWriter(Stream stream)
    {
        private int _buffer;
        private int _count;

        public void Write(int bits, int length)
        {
            for (var i = length - 1; i >= 0; i--)
            {
                _buffer = (_buffer << 1) | ((bits >> i) & 1);
                _count++;

                if (_count != 8) continue;

                var value = (byte)_buffer;
                stream.WriteByte(value);
                // Without this stuffed zero, a 0xFF in the entropy data would be read as a marker.
                if (value == 0xFF) stream.WriteByte(0x00);

                _buffer = 0;
                _count = 0;
            }
        }

        /// <summary>Pads the final partial byte with ones, which is what the standard specifies.</summary>
        public void Flush()
        {
            while (_count != 0) Write(1, 1);
        }
    }
}
