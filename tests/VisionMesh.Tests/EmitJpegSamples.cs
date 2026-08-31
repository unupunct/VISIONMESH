using VisionMesh.Agent.Core;
using Xunit;

namespace VisionMesh.Tests;

/// <summary>
/// Writes sample output from the fallback JPEG encoder to disk so scripts/verify-jpeg.py can
/// decode it with an independent image library.
///
/// This exists because the unit tests check the encoder against VisionMesh's own decoder, and two
/// components written by the same hand agreeing with each other is weaker evidence than either
/// agreeing with libjpeg.
/// </summary>
public class EmitJpegSamples
{
    [Fact]
    public void WriteSamplesForExternalVerification()
    {
        var directory = Path.Combine(Path.GetTempPath(), "visionmesh-jpeg-samples");
        Directory.CreateDirectory(directory);

        // A colour gradient, so a decoder mismatch in chroma shows up rather than hiding in grey.
        const int width = 320, height = 240;
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 3;
                rgb[offset] = (byte)(x * 255 / (width - 1));
                rgb[offset + 1] = (byte)(y * 255 / (height - 1));
                rgb[offset + 2] = 128;
            }
        }

        foreach (var quality in new[] { 40, 75, 92 })
        {
            File.WriteAllBytes(Path.Combine(directory, $"gradient-q{quality}.jpg"),
                JpegEncoder.EncodeRgb24(rgb, width, height, quality));
        }

        // Flat mid-grey via the YUYV path.
        var yuyv = new byte[160 * 120 * 2];
        for (var i = 0; i < yuyv.Length; i++) yuyv[i] = 128;
        File.WriteAllBytes(Path.Combine(directory, "flat-yuyv.jpg"), JpegEncoder.EncodeYuyv(yuyv, 160, 120, 90));

        // An odd size, to prove the MCU padding does not corrupt the edges.
        var odd = new byte[151 * 113 * 3];
        for (var i = 0; i < odd.Length; i += 3) { odd[i] = 200; odd[i + 1] = 60; odd[i + 2] = 40; }
        File.WriteAllBytes(Path.Combine(directory, "odd-151x113.jpg"), JpegEncoder.EncodeRgb24(odd, 151, 113, 80));

        File.WriteAllText(Path.Combine(directory, "expected.txt"),
            string.Join('\n',
                "gradient-q40.jpg 320 240 gradient",
                "gradient-q75.jpg 320 240 gradient",
                "gradient-q92.jpg 320 240 gradient",
                "flat-yuyv.jpg 160 120 flat128",
                "odd-151x113.jpg 151 113 rgb200-60-40"));

        Assert.True(Directory.GetFiles(directory, "*.jpg").Length == 5);
    }
}
