using TLOverlay.Core.Pipeline;
using Xunit;

namespace TLOverlay.Core.Tests;

public class FrameSignatureTests
{
    private static byte[] SolidBgra(int width, int height, byte b, byte g, byte r, int stride)
    {
        var buffer = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int p = (y * stride) + (x * 4);
                buffer[p] = b;
                buffer[p + 1] = g;
                buffer[p + 2] = r;
                buffer[p + 3] = 255;
            }
        }

        return buffer;
    }

    [Fact]
    public void SolidWhiteProducesMaximumLuma()
    {
        var pixels = SolidBgra(64, 64, 255, 255, 255, 64 * 4);

        var signature = FrameSignature.FromBgra(pixels, 64, 64, 64 * 4);

        Assert.Equal(FrameSignature.Length, signature.Length);
        Assert.All(signature, value => Assert.InRange(value, 250, 255));
    }

    [Fact]
    public void BlackAndWhiteAreMaximallyDifferent()
    {
        var black = FrameSignature.FromBgra(SolidBgra(32, 32, 0, 0, 0, 128), 32, 32, 128);
        var white = FrameSignature.FromBgra(SolidBgra(32, 32, 255, 255, 255, 128), 32, 32, 128);

        Assert.True(FrameSignature.Difference(black, white) > 250);
    }

    [Fact]
    public void IdenticalBuffersHaveZeroDifference()
    {
        var pixels = SolidBgra(40, 24, 30, 60, 90, 40 * 4);

        var a = FrameSignature.FromBgra(pixels, 40, 24, 40 * 4);
        var b = FrameSignature.FromBgra(pixels, 40, 24, 40 * 4);

        Assert.Equal(0, FrameSignature.Difference(a, b));
    }

    [Fact]
    public void PaddedStrideIsHonoured()
    {
        // Captured textures are row-aligned, so stride routinely exceeds width*4.
        // Reading past the width must not leak padding into the signature.
        const int Width = 30;
        const int Height = 10;
        const int Stride = 256;

        var pixels = SolidBgra(Width, Height, 255, 255, 255, Stride);

        var signature = FrameSignature.FromBgra(pixels, Width, Height, Stride);

        Assert.All(signature, value => Assert.InRange(value, 250, 255));
    }

    [Fact]
    public void RejectsBufferSmallerThanTheDescribedImage()
    {
        var tiny = new byte[16];

        Assert.Throws<ArgumentException>(() => FrameSignature.FromBgra(tiny, 32, 32, 128));
    }
}
