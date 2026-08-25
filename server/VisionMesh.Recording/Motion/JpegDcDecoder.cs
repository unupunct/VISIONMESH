namespace VisionMesh.Recording.Motion;

/// <summary>
/// A one-eighth scale luma image recovered from a JPEG without fully decoding it.
/// </summary>
/// <param name="Pixels">Row-major luma samples, 0-255, <paramref name="Width"/> * <paramref name="Height"/> long.</param>
public sealed record LumaThumbnail(byte[] Pixels, int Width, int Height, int SourceWidth, int SourceHeight);

/// <summary>
/// Extracts a 1/8-scale greyscale image from a baseline JPEG by decoding only the DC
/// coefficient of each 8x8 block.
///
/// Why this instead of an imaging library: motion detection wants a small, cheap, approximate
/// picture, and the DC coefficient of a block *is* the average brightness of those 64 pixels -
/// exactly the downscale we would compute anyway, available for a fraction of the work because
/// the AC coefficients are skipped rather than dequantised and inverse-transformed.
///
/// It also keeps a third-party image decoder out of the path that parses bytes arriving from
/// cameras on the network. That path is the most exposed surface in the whole product, and the
/// popular managed decoders have a steady history of CVEs. This parser reads a deliberately
/// narrow subset of JPEG, treats every malformed input as "not decodable" rather than throwing,
/// and never allocates based on an unvalidated size field.
///
/// Supports baseline sequential JPEG (SOF0/SOF1), which is what every webcam, phone camera and
/// ffmpeg mjpeg encoder produces. Progressive JPEG (SOF2) is rejected rather than mis-decoded.
/// </summary>
public static class JpegDcDecoder
{
    private const int MaxComponents = 4;
    /// <summary>Guards against a crafted SOF claiming an enormous frame. 8K is far beyond any camera here.</summary>
    private const int MaxDimension = 8192;

    /// <summary>
    /// Decodes the luma plane at 1/8 scale. Returns null for anything this decoder does not
    /// handle - progressive, arithmetic-coded, truncated or malformed data - so callers can
    /// simply skip motion detection for that frame instead of handling exceptions.
    /// </summary>
    public static LumaThumbnail? TryDecodeLuma(ReadOnlySpan<byte> jpeg)
    {
        try
        {
            return Decode(jpeg);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException or OverflowException)
        {
            // Malformed or truncated input. Not an error worth surfacing: the next frame will do.
            return null;
        }
    }

    private static LumaThumbnail? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;

        var quantTables = new ushort[MaxComponents][];
        var dcTables = new HuffmanTable?[4];
        var acTables = new HuffmanTable?[4];
        Component[]? components = null;
        int frameWidth = 0, frameHeight = 0;
        int restartInterval = 0;

        var position = 2;
        while (position + 3 < data.Length)
        {
            if (data[position] != 0xFF) { position++; continue; }

            var marker = data[position + 1];
            position += 2;

            if (marker == 0xFF) { position--; continue; }              // fill byte
            if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7)) continue;
            if (marker == 0xD9) return null;                            // EOI before any scan

            if (position + 1 >= data.Length) return null;
            var segmentLength = (data[position] << 8) | data[position + 1];
            if (segmentLength < 2 || position + segmentLength > data.Length) return null;
            var segment = data.Slice(position + 2, segmentLength - 2);

            switch (marker)
            {
                case 0xC0: // SOF0 baseline
                case 0xC1: // SOF1 extended sequential - same coefficient layout
                {
                    if (segment.Length < 6) return null;
                    frameHeight = (segment[1] << 8) | segment[2];
                    frameWidth = (segment[3] << 8) | segment[4];
                    var count = segment[5];

                    if (frameWidth is <= 0 or > MaxDimension || frameHeight is <= 0 or > MaxDimension) return null;
                    if (count is < 1 or > MaxComponents) return null;
                    if (segment.Length < 6 + (count * 3)) return null;

                    components = new Component[count];
                    for (var i = 0; i < count; i++)
                    {
                        var offset = 6 + (i * 3);
                        components[i] = new Component
                        {
                            Id = segment[offset],
                            HorizontalSampling = segment[offset + 1] >> 4,
                            VerticalSampling = segment[offset + 1] & 15,
                            QuantTableId = segment[offset + 2],
                        };
                        if (components[i].HorizontalSampling is < 1 or > 4) return null;
                        if (components[i].VerticalSampling is < 1 or > 4) return null;
                        if (components[i].QuantTableId >= MaxComponents) return null;
                    }
                    break;
                }

                // Progressive, arithmetic, lossless and hierarchical modes all store coefficients
                // differently. Rejecting them is honest; guessing would produce a wrong picture.
                case 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF:
                    return null;

                case 0xC4: // DHT
                {
                    var offset = 0;
                    while (offset + 17 <= segment.Length)
                    {
                        var tableClass = segment[offset] >> 4;
                        var tableId = segment[offset] & 15;
                        if (tableId >= 4 || tableClass > 1) return null;

                        var counts = new byte[16];
                        var total = 0;
                        for (var i = 0; i < 16; i++)
                        {
                            counts[i] = segment[offset + 1 + i];
                            total += counts[i];
                        }
                        if (total is 0 or > 256) return null;
                        if (offset + 17 + total > segment.Length) return null;

                        var symbols = segment.Slice(offset + 17, total).ToArray();
                        var table = HuffmanTable.Build(counts, symbols);
                        if (tableClass == 0) dcTables[tableId] = table; else acTables[tableId] = table;

                        offset += 17 + total;
                    }
                    break;
                }

                case 0xDB: // DQT
                {
                    var offset = 0;
                    while (offset < segment.Length)
                    {
                        var precision = segment[offset] >> 4;
                        var tableId = segment[offset] & 15;
                        if (tableId >= MaxComponents) return null;

                        var entrySize = precision == 0 ? 1 : 2;
                        if (offset + 1 + (64 * entrySize) > segment.Length) return null;

                        var table = new ushort[64];
                        for (var i = 0; i < 64; i++)
                        {
                            table[i] = precision == 0
                                ? segment[offset + 1 + i]
                                : (ushort)((segment[offset + 1 + (i * 2)] << 8) | segment[offset + 2 + (i * 2)]);
                        }
                        quantTables[tableId] = table;
                        offset += 1 + (64 * entrySize);
                    }
                    break;
                }

                case 0xDD: // DRI
                    if (segment.Length >= 2) restartInterval = (segment[0] << 8) | segment[1];
                    break;

                case 0xDA: // SOS - the scan follows immediately after this segment
                {
                    if (components is null) return null;
                    if (segment.Length < 1) return null;

                    var scanComponentCount = segment[0];
                    if (scanComponentCount < 1 || segment.Length < 1 + (scanComponentCount * 2) + 3) return null;

                    // A non-interleaved scan per component is legal but rare outside progressive
                    // JPEG, and decoding it needs a different MCU walk. Not worth supporting here.
                    if (scanComponentCount != components.Length) return null;

                    for (var i = 0; i < scanComponentCount; i++)
                    {
                        var componentId = segment[1 + (i * 2)];
                        var tables = segment[2 + (i * 2)];
                        var component = Array.Find(components, c => c.Id == componentId);
                        if (component is null) return null;
                        component.DcTableId = tables >> 4;
                        component.AcTableId = tables & 15;
                        if (component.DcTableId >= 4 || component.AcTableId >= 4) return null;
                    }

                    var scanStart = position + segmentLength;
                    if (scanStart >= data.Length) return null;

                    return DecodeScan(data[scanStart..], components, quantTables, dcTables, acTables,
                                      frameWidth, frameHeight, restartInterval);
                }
            }

            position += segmentLength;
        }

        return null;
    }

    private static LumaThumbnail? DecodeScan(
        ReadOnlySpan<byte> scan,
        Component[] components,
        ushort[][] quantTables,
        HuffmanTable?[] dcTables,
        HuffmanTable?[] acTables,
        int frameWidth,
        int frameHeight,
        int restartInterval)
    {
        var maxH = components.Max(c => c.HorizontalSampling);
        var maxV = components.Max(c => c.VerticalSampling);

        var mcusWide = (frameWidth + (8 * maxH) - 1) / (8 * maxH);
        var mcusHigh = (frameHeight + (8 * maxV) - 1) / (8 * maxV);
        if (mcusWide <= 0 || mcusHigh <= 0) return null;

        // The luma plane is component 0 by JPEG convention; its blocks form the thumbnail grid.
        var luma = components[0];
        var outputWidth = mcusWide * luma.HorizontalSampling;
        var outputHeight = mcusHigh * luma.VerticalSampling;

        // ~67M blocks would be needed to exceed this; a real 8K frame needs about 1M.
        if ((long)outputWidth * outputHeight > 16_000_000) return null;

        var pixels = new byte[outputWidth * outputHeight];
        var reader = new BitReader(scan);

        var lumaQuant = quantTables[luma.QuantTableId];
        if (lumaQuant is null) return null;

        foreach (var component in components)
        {
            if (dcTables[component.DcTableId] is null || acTables[component.AcTableId] is null) return null;
            component.DcPredictor = 0;
        }

        var mcuIndex = 0;
        for (var mcuY = 0; mcuY < mcusHigh; mcuY++)
        {
            for (var mcuX = 0; mcuX < mcusWide; mcuX++, mcuIndex++)
            {
                if (restartInterval > 0 && mcuIndex > 0 && mcuIndex % restartInterval == 0)
                {
                    if (!reader.RestartAtMarker()) return Finish(pixels, outputWidth, outputHeight, frameWidth, frameHeight);
                    foreach (var component in components) component.DcPredictor = 0;
                }

                foreach (var component in components)
                {
                    var dcTable = dcTables[component.DcTableId]!;
                    var acTable = acTables[component.AcTableId]!;

                    for (var blockY = 0; blockY < component.VerticalSampling; blockY++)
                    {
                        for (var blockX = 0; blockX < component.HorizontalSampling; blockX++)
                        {
                            if (!reader.TryDecodeBlockDc(dcTable, acTable, ref component.DcPredictor, out var dc))
                            {
                                // Ran out of data: return what was decoded rather than nothing, so a
                                // slightly truncated frame still produces a usable motion comparison.
                                return Finish(pixels, outputWidth, outputHeight, frameWidth, frameHeight);
                            }

                            if (!ReferenceEquals(component, luma)) continue;

                            // DC * quant / 8 is the block's mean sample value before the level
                            // shift that JPEG applies, so adding 128 returns it to 0-255.
                            var value = ((dc * lumaQuant[0]) / 8) + 128;
                            var x = (mcuX * luma.HorizontalSampling) + blockX;
                            var y = (mcuY * luma.VerticalSampling) + blockY;
                            pixels[(y * outputWidth) + x] = (byte)Math.Clamp(value, 0, 255);
                        }
                    }
                }
            }
        }

        return Finish(pixels, outputWidth, outputHeight, frameWidth, frameHeight);
    }

    private static LumaThumbnail Finish(byte[] pixels, int width, int height, int sourceWidth, int sourceHeight)
        => new(pixels, width, height, sourceWidth, sourceHeight);

    private sealed class Component
    {
        public int Id;
        public int HorizontalSampling;
        public int VerticalSampling;
        public int QuantTableId;
        public int DcTableId;
        public int AcTableId;
        public int DcPredictor;
    }

    /// <summary>
    /// Canonical JPEG Huffman table, decoded with the min/max-code algorithm from the standard.
    /// Codes are at most 16 bits, so a per-length range check is both simple and fast enough for
    /// the handful of blocks a motion check needs.
    /// </summary>
    private sealed class HuffmanTable
    {
        private readonly int[] _minCode = new int[17];
        private readonly int[] _maxCode = new int[17];
        private readonly int[] _valuePointer = new int[17];
        private readonly byte[] _symbols;

        private HuffmanTable(byte[] symbols) => _symbols = symbols;

        public static HuffmanTable Build(byte[] counts, byte[] symbols)
        {
            var table = new HuffmanTable(symbols);
            var code = 0;
            var index = 0;

            for (var length = 1; length <= 16; length++)
            {
                var count = counts[length - 1];
                if (count == 0)
                {
                    table._maxCode[length] = -1;   // no code of this length exists
                }
                else
                {
                    table._valuePointer[length] = index;
                    table._minCode[length] = code;
                    index += count;
                    code += count;
                    table._maxCode[length] = code - 1;
                }

                // The shift happens for every length, including empty ones. Skipping it for
                // empty lengths silently mis-decodes any table with a gap in its code lengths -
                // which simple images never have and detailed ones always do.
                code <<= 1;
            }
            return table;
        }

        public bool TryDecode(ref BitReader reader, out byte symbol)
        {
            symbol = 0;
            var code = 0;
            for (var length = 1; length <= 16; length++)
            {
                if (!reader.TryReadBit(out var bit)) return false;
                code = (code << 1) | bit;

                if (_maxCode[length] < 0 || code > _maxCode[length]) continue;

                var index = _valuePointer[length] + code - _minCode[length];
                if (index < 0 || index >= _symbols.Length) return false;
                symbol = _symbols[index];
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// MSB-first bit reader over entropy-coded scan data, handling FF 00 byte stuffing and
    /// stopping cleanly at any marker.
    /// </summary>
    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _bytePosition = 0;
        private int _bitPosition = 0;
        private bool _exhausted = false;

        public bool TryReadBit(out int bit)
        {
            bit = 0;
            if (_exhausted || _bytePosition >= _data.Length) { _exhausted = true; return false; }

            var current = _data[_bytePosition];
            if (current == 0xFF)
            {
                var next = _bytePosition + 1 < _data.Length ? _data[_bytePosition + 1] : (byte)0xD9;
                // FF 00 is a stuffed literal FF; anything else is a marker and ends the scan.
                if (next != 0x00) { _exhausted = true; return false; }
            }

            bit = (current >> (7 - _bitPosition)) & 1;
            _bitPosition++;
            if (_bitPosition == 8)
            {
                _bitPosition = 0;
                _bytePosition += current == 0xFF ? 2 : 1;   // skip the stuffed 00
            }
            return true;
        }

        public bool TryReadBits(int count, out int value)
        {
            value = 0;
            for (var i = 0; i < count; i++)
            {
                if (!TryReadBit(out var bit)) return false;
                value = (value << 1) | bit;
            }
            return true;
        }

        /// <summary>Aligns to the next byte and consumes an RSTn marker, as the restart interval requires.</summary>
        public bool RestartAtMarker()
        {
            if (_bitPosition != 0) { _bitPosition = 0; _bytePosition++; }

            while (_bytePosition + 1 < _data.Length)
            {
                if (_data[_bytePosition] == 0xFF)
                {
                    var marker = _data[_bytePosition + 1];
                    if (marker >= 0xD0 && marker <= 0xD7)
                    {
                        _bytePosition += 2;
                        _exhausted = false;
                        return true;
                    }
                    return false;   // EOI or an unexpected marker: the scan is over
                }
                _bytePosition++;
            }
            return false;
        }

        /// <summary>
        /// Decodes one block's DC coefficient and skips its 63 AC coefficients.
        /// The AC coefficients still have to be Huffman-decoded to find where the block ends -
        /// they are simply discarded instead of being dequantised and transformed.
        /// </summary>
        public bool TryDecodeBlockDc(HuffmanTable dcTable, HuffmanTable acTable, ref int predictor, out int dc)
        {
            dc = 0;
            if (!dcTable.TryDecode(ref this, out var sizeSymbol)) return false;

            var difference = 0;
            if (sizeSymbol > 0)
            {
                if (sizeSymbol > 16) return false;
                if (!TryReadBits(sizeSymbol, out var raw)) return false;
                difference = Extend(raw, sizeSymbol);
            }

            predictor += difference;
            dc = predictor;

            for (var k = 1; k < 64;)
            {
                if (!acTable.TryDecode(ref this, out var runSize)) return false;
                var size = runSize & 15;
                var run = runSize >> 4;

                if (size == 0)
                {
                    if (run != 15) break;   // EOB
                    k += 16;                // ZRL: sixteen zero coefficients
                    continue;
                }

                k += run + 1;
                if (size > 16) return false;
                if (!TryReadBits(size, out _)) return false;
            }

            return true;
        }

        /// <summary>Sign-extends a JPEG variable-length integer, per the standard's EXTEND procedure.</summary>
        private static int Extend(int value, int size)
            => value < (1 << (size - 1)) ? value - (1 << size) + 1 : value;
    }
}
