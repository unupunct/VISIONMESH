namespace VisionMesh.Streaming.Sources;

/// <summary>
/// Splits a continuous MJPEG byte stream (ffmpeg's image2pipe output) into individual JPEG images.
///
/// This walks the JPEG marker structure rather than scanning for the FFD8/FFD9 byte pair.
/// Scanning is the obvious shortcut and is wrong in general: an APP1/EXIF segment can embed a
/// thumbnail that contains its own FFD9, which would truncate the frame. Walking the segments
/// costs almost nothing and cannot be fooled that way.
/// </summary>
public sealed class JpegStreamReader(Stream source, int maxFrameBytes = 8 * 1024 * 1024)
{
    private byte[] _buffer = new byte[128 * 1024];
    private int _length;   // bytes currently held
    private int _scanned;  // how far into the buffer the parser has already advanced

    /// <summary>
    /// Reads the next complete JPEG image, or null when the stream ends.
    /// The returned array is a fresh copy and is safe to hand to the frame bus.
    /// </summary>
    public async Task<byte[]?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (TryExtractFrame(out var frame)) return frame;

            if (_length == _buffer.Length) Grow();

            var read = await source.ReadAsync(_buffer.AsMemory(_length), cancellationToken).ConfigureAwait(false);
            if (read == 0) return null;
            _length += read;

            if (_length > maxFrameBytes)
            {
                // Either the source is not JPEG at all or a frame is absurdly large. Either way,
                // resyncing beats growing the buffer without limit.
                Resync();
            }
        }
    }

    private void Grow()
    {
        var target = Math.Min(_buffer.Length * 2, maxFrameBytes + (128 * 1024));
        if (target <= _buffer.Length) target = _buffer.Length + (128 * 1024);
        Array.Resize(ref _buffer, target);
    }

    /// <summary>Drops everything before the next SOI so a corrupt stream can recover.</summary>
    private void Resync()
    {
        var span = _buffer.AsSpan(1, _length - 1);
        var index = IndexOfSoi(span);
        if (index < 0)
        {
            _length = 0;
        }
        else
        {
            var start = index + 1;
            _buffer.AsSpan(start, _length - start).CopyTo(_buffer);
            _length -= start;
        }
        _scanned = 0;
    }

    private static int IndexOfSoi(ReadOnlySpan<byte> span)
    {
        for (var i = 0; i + 1 < span.Length; i++)
        {
            if (span[i] == 0xFF && span[i + 1] == 0xD8) return i;
        }
        return -1;
    }

    private bool TryExtractFrame(out byte[]? frame)
    {
        frame = null;
        if (_length < 4) return false;

        // Discard anything before the first SOI.
        if (_buffer[0] != 0xFF || _buffer[1] != 0xD8)
        {
            var index = IndexOfSoi(_buffer.AsSpan(0, _length));
            if (index < 0)
            {
                // Keep the final byte: it may be the FF of an FFD8 straddling the read boundary.
                _buffer[0] = _buffer[_length - 1];
                _length = 1;
                _scanned = 0;
                return false;
            }
            _buffer.AsSpan(index, _length - index).CopyTo(_buffer);
            _length -= index;
            _scanned = 0;
        }

        var end = FindEndOfImage();
        if (end < 0) return false;

        frame = _buffer.AsSpan(0, end).ToArray();
        var remaining = _length - end;
        if (remaining > 0) _buffer.AsSpan(end, remaining).CopyTo(_buffer);
        _length = remaining;
        _scanned = 0;
        return true;
    }

    /// <summary>
    /// Returns the exclusive end offset of the JPEG starting at index 0, or -1 if more data is needed.
    /// Parsing resumes from <see cref="_scanned"/> so repeated calls stay linear in the stream length.
    /// </summary>
    private int FindEndOfImage()
    {
        var position = _scanned > 0 ? _scanned : 2; // skip SOI on the first pass

        while (position + 1 < _length)
        {
            if (_buffer[position] != 0xFF)
            {
                position++;
                continue;
            }

            var marker = _buffer[position + 1];

            switch (marker)
            {
                case 0xFF:            // fill byte: the next byte may itself start a marker
                    position++;
                    continue;

                // Inside entropy-coded scan data an FF is escaped as FF 00, and restart
                // markers are standalone. Handling both here means scan data needs no
                // separate parser and the scan stays resumable across reads.
                case 0x00:            // stuffed FF inside scan data
                case 0x01:            // TEM
                case >= 0xD0 and <= 0xD7: // RSTn
                case 0xD8:            // stray SOI: treat as two data bytes
                    position += 2;
                    continue;

                case 0xD9:            // EOI
                    _scanned = 0;
                    return position + 2;

                // SOS and every other marker segment carry a 2-byte length. Skipping by
                // length is required because a segment payload (a Huffman table, say) may
                // legitimately contain FF bytes that are not markers.
                default:
                {
                    if (position + 4 > _length) { _scanned = position; return -1; }
                    var segmentLength = (_buffer[position + 2] << 8) | _buffer[position + 3];
                    if (segmentLength < 2) { position += 2; continue; }
                    position += 2 + segmentLength;
                    continue;
                }
            }
        }

        _scanned = Math.Max(2, position);
        return -1;
    }
}
