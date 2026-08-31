using VisionMesh.Agent.Core;
using VisionMesh.Recording.Motion;
using Xunit;

namespace VisionMesh.Tests;

/// <summary>
/// Verifies the fallback JPEG encoder.
///
/// The encoder and the DC decoder were written independently against the same standard, so
/// decoding an encoded frame and getting the original brightness back is a genuine cross-check
/// rather than one component agreeing with itself. A separate script (scripts/verify-jpeg.py)
/// decodes the same output with a real image library.
/// </summary>
public class JpegEncoderTests
{
    [Fact]
    public void EncodesRgbToAJpegThatDecodesBackToTheSamePicture()
    {
        const int width = 320, height = 240;
        var rgb = new byte[width * height * 3];

        // A horizontal brightness ramp: easy to verify, and it exercises real DCT coefficients
        // rather than the degenerate flat case.
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = (byte)(x * 255 / (width - 1));
                var offset = (y * width + x) * 3;
                rgb[offset] = value;
                rgb[offset + 1] = value;
                rgb[offset + 2] = value;
            }
        }

        var jpeg = JpegEncoder.EncodeRgb24(rgb, width, height, 85);

        Assert.True(jpeg.Length > 200, "The encoded JPEG is implausibly small.");
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
        Assert.Equal(0xFF, jpeg[^2]);
        Assert.Equal(0xD9, jpeg[^1]);

        var decoded = JpegDcDecoder.TryDecodeLuma(jpeg);
        Assert.NotNull(decoded);
        Assert.Equal(width, decoded!.SourceWidth);
        Assert.Equal(height, decoded.SourceHeight);

        // Grey means luma equals the input value, so each 8x8 block should average close to the
        // ramp value at its centre.
        var error = 0.0;
        var samples = 0;
        for (var by = 0; by < decoded.Height; by++)
        {
            for (var bx = 0; bx < decoded.Width; bx++)
            {
                var centreX = Math.Min(bx * 8 + 4, width - 1);
                var expected = centreX * 255.0 / (width - 1);
                error += Math.Abs(decoded.Pixels[by * decoded.Width + bx] - expected);
                samples++;
            }
        }

        var meanError = error / samples;
        Assert.True(meanError < 8.0, $"Round-tripped brightness differs by {meanError:0.0} on average, which is too much.");
    }

    [Fact]
    public void EncodesYuyvWithoutGoingThroughRgb()
    {
        const int width = 160, height = 120;
        var yuyv = new byte[width * height * 2];

        // Mid-grey: Y = 128 with neutral chroma at 128.
        for (var i = 0; i < yuyv.Length; i += 4)
        {
            yuyv[i] = 128;      // Y0
            yuyv[i + 1] = 128;  // U
            yuyv[i + 2] = 128;  // Y1
            yuyv[i + 3] = 128;  // V
        }

        var jpeg = JpegEncoder.EncodeYuyv(yuyv, width, height, 90);
        var decoded = JpegDcDecoder.TryDecodeLuma(jpeg);

        Assert.NotNull(decoded);
        var min = decoded!.Pixels.Min();
        var max = decoded.Pixels.Max();

        Assert.True(max - min <= 3, $"A flat frame should decode flat, but ranged {min}-{max}.");
        Assert.InRange(decoded.Pixels[0], 120, 136);
    }

    [Fact]
    public void EncodesBgr32()
    {
        const int width = 64, height = 48;
        var bgr = new byte[width * height * 4];

        for (var i = 0; i < width * height; i++)
        {
            bgr[i * 4] = 32;        // blue
            bgr[i * 4 + 1] = 200;   // green
            bgr[i * 4 + 2] = 64;    // red
            bgr[i * 4 + 3] = 255;
        }

        var jpeg = JpegEncoder.EncodeBgr32(bgr, width, height, 80);
        var decoded = JpegDcDecoder.TryDecodeLuma(jpeg);

        Assert.NotNull(decoded);

        // Luma of that colour is 0.299*64 + 0.587*200 + 0.114*32 = about 140.
        Assert.InRange(decoded!.Pixels[decoded.Pixels.Length / 2], 130, 150);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(75)]
    [InlineData(95)]
    public void HigherQualityProducesLargerFiles(int quality)
    {
        const int width = 128, height = 96;
        var rgb = BuildNoiseImage(width, height, seed: 7);

        var jpeg = JpegEncoder.EncodeRgb24(rgb, width, height, quality);
        Assert.NotNull(JpegDcDecoder.TryDecodeLuma(jpeg));

        var lower = JpegEncoder.EncodeRgb24(rgb, width, height, Math.Max(1, quality - 15));
        Assert.True(jpeg.Length >= lower.Length,
            $"Quality {quality} produced {jpeg.Length} bytes, which is not larger than quality {quality - 15} at {lower.Length}.");
    }

    [Theory]
    [InlineData(17, 13)]     // smaller than one MCU
    [InlineData(151, 113)]   // not a multiple of 16
    [InlineData(2, 2)]       // the smallest sensible frame
    public void HandlesSizesThatAreNotAMultipleOfTheMcu(int width, int height)
    {
        var rgb = BuildNoiseImage(width, height, seed: 3);
        var jpeg = JpegEncoder.EncodeRgb24(rgb, width, height, 80);

        var decoded = JpegDcDecoder.TryDecodeLuma(jpeg);
        Assert.NotNull(decoded);
        Assert.Equal(width, decoded!.SourceWidth);
        Assert.Equal(height, decoded.SourceHeight);
    }

    [Fact]
    public void RejectsABufferSmallerThanTheFrameItDescribes()
    {
        // Encoding past the end of a short buffer would read whatever memory follows it.
        Assert.Throws<ArgumentException>(() => JpegEncoder.EncodeRgb24(new byte[10], 320, 240, 80));
        Assert.Throws<ArgumentException>(() => JpegEncoder.EncodeYuyv(new byte[10], 320, 240, 80));
    }

    private static byte[] BuildNoiseImage(int width, int height, int seed)
    {
        var random = new Random(seed);
        var rgb = new byte[width * height * 3];
        random.NextBytes(rgb);
        return rgb;
    }
}
