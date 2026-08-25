using VisionMesh.Recording.Motion;
using Xunit;

namespace VisionMesh.Tests;

/// <summary>
/// Verifies the DC-only JPEG decoder against images produced by a real encoder (Pillow).
///
/// The bar is "close enough for motion detection", not "bit-exact": DC-only decoding is a
/// deliberate approximation of an 8x8 box downscale, and the quantiser rounds the DC term.
/// The tests therefore assert on mean absolute error rather than exact equality.
/// </summary>
public class JpegDcDecoderTests
{
    [Fact]
    public void DecodesBaseline420_MatchingTheTrueDownscale()
    {
        var result = JpegDcDecoder.TryDecodeLuma(Fixtures.Read("gradient_420.jpg"));

        Assert.NotNull(result);
        Assert.Equal(320, result!.SourceWidth);
        Assert.Equal(240, result.SourceHeight);
        Assert.Equal(40, result.Width);
        Assert.Equal(30, result.Height);

        var reference = Fixtures.Read("gradient_luma_ref.raw");
        Assert.Equal(reference.Length, result.Pixels.Length);

        var meanError = MeanAbsoluteError(result.Pixels, reference);
        Assert.True(meanError < 6.0, $"DC luma differs from the true downscale by {meanError:0.00} on average, which is too much.");
    }

    [Fact]
    public void DecodesFullChromaSampling()
    {
        // 4:4:4 gives luma a 1x1 sampling factor, so the MCU walk differs from the 4:2:0 case.
        var result = JpegDcDecoder.TryDecodeLuma(Fixtures.Read("gradient_444.jpg"));

        Assert.NotNull(result);
        Assert.Equal(40, result!.Width);
        Assert.Equal(30, result.Height);

        var meanError = MeanAbsoluteError(result.Pixels, Fixtures.Read("gradient_luma_ref.raw"));
        Assert.True(meanError < 6.0, $"4:4:4 luma differs by {meanError:0.00} on average.");
    }

    [Fact]
    public void HandlesRestartMarkers()
    {
        // Restart markers reset the DC predictor mid-scan. Getting this wrong produces a
        // recognisable banding pattern rather than an outright failure, so it is worth asserting.
        var result = JpegDcDecoder.TryDecodeLuma(Fixtures.Read("gradient_restart.jpg"));

        Assert.NotNull(result);
        var meanError = MeanAbsoluteError(result!.Pixels, Fixtures.Read("gradient_luma_ref.raw"));
        Assert.True(meanError < 6.0, $"Restart-marker luma differs by {meanError:0.00} on average.");
    }

    [Fact]
    public void DecodesGrayscaleSingleComponent()
    {
        var result = JpegDcDecoder.TryDecodeLuma(Fixtures.Read("grayscale.jpg"));

        Assert.NotNull(result);
        Assert.Equal(40, result!.Width);
        Assert.Equal(30, result.Height);

        var meanError = MeanAbsoluteError(result.Pixels, Fixtures.Read("gradient_luma_ref.raw"));
        Assert.True(meanError < 6.0, $"Greyscale luma differs by {meanError:0.00} on average.");
    }

    [Fact]
    public void DecodesFlatImageToAUniformValue()
    {
        var result = JpegDcDecoder.TryDecodeLuma(Fixtures.Read("flat_gray.jpg"));

        Assert.NotNull(result);
        var min = result!.Pixels.Min();
        var max = result.Pixels.Max();

        Assert.True(max - min <= 2, $"A flat grey image should decode flat, but ranged {min}-{max}.");
        Assert.InRange(result.Pixels[0], 120, 136);
    }

    [Fact]
    public void HandlesDimensionsThatAreNotAMultipleOfTheMcuSize()
    {
        // 151x113 pads to whole MCUs. The thumbnail covers the padded area, which is expected;
        // what matters is that decoding completes and the covered region is sane.
        var result = JpegDcDecoder.TryDecodeLuma(Fixtures.Read("odd_size.jpg"));

        Assert.NotNull(result);
        Assert.Equal(151, result!.SourceWidth);
        Assert.Equal(113, result.SourceHeight);
        Assert.True(result.Width >= 19 && result.Width <= 20, $"Unexpected thumbnail width {result.Width}.");
        Assert.True(result.Pixels.Any(p => p > 0), "Decoded thumbnail was entirely black.");
    }

    [Fact]
    public void RefusesProgressiveJpegRatherThanMisDecodingIt()
    {
        // Progressive stores coefficients across multiple scans. Producing a wrong picture here
        // would silently corrupt motion detection, so refusing is the correct behaviour.
        Assert.Null(JpegDcDecoder.TryDecodeLuma(Fixtures.Read("progressive.jpg")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(64)]
    public void ReturnsNullForTruncatedInputInsteadOfThrowing(int length)
    {
        var full = Fixtures.Read("gradient_420.jpg");
        var truncated = full.AsSpan(0, Math.Min(length, full.Length)).ToArray();

        var exception = Record.Exception(() => JpegDcDecoder.TryDecodeLuma(truncated));
        Assert.Null(exception);
    }

    [Fact]
    public void ReturnsNullForNonJpegData()
    {
        Assert.Null(JpegDcDecoder.TryDecodeLuma("this is not a jpeg at all"u8));
        Assert.Null(JpegDcDecoder.TryDecodeLuma(new byte[512]));
    }

    [Fact]
    public void SurvivesRandomlyCorruptedInput()
    {
        // Frames arrive over the network from devices we do not control. A corrupt frame must
        // never take the recording engine down, whatever byte happens to be wrong.
        var original = Fixtures.Read("gradient_420.jpg");
        var random = new Random(20260825);

        for (var iteration = 0; iteration < 400; iteration++)
        {
            var corrupted = (byte[])original.Clone();
            var corruptions = random.Next(1, 12);
            for (var i = 0; i < corruptions; i++)
            {
                corrupted[random.Next(corrupted.Length)] = (byte)random.Next(256);
            }

            var exception = Record.Exception(() => JpegDcDecoder.TryDecodeLuma(corrupted));
            Assert.Null(exception);
        }
    }

    [Fact]
    public void ProducesDifferentThumbnailsForFramesThatDiffer()
    {
        var a = JpegDcDecoder.TryDecodeLuma(Fixtures.Read("motion_a.jpg"));
        var b = JpegDcDecoder.TryDecodeLuma(Fixtures.Read("motion_b.jpg"));

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.Pixels.Length, b!.Pixels.Length);

        var changed = a.Pixels.Where((value, index) => Math.Abs(value - b.Pixels[index]) > 25).Count();
        var ratio = (double)changed / a.Pixels.Length;

        // The bright patch covers 80x60 of 320x240, so about 6% of the frame.
        Assert.InRange(ratio, 0.03, 0.12);
    }

    private static double MeanAbsoluteError(byte[] actual, byte[] expected)
    {
        var total = 0L;
        for (var i = 0; i < actual.Length; i++) total += Math.Abs(actual[i] - expected[i]);
        return (double)total / actual.Length;
    }
}
