using TLOverlay.Core.Capture;
using Xunit;

namespace TLOverlay.Core.Tests;

public class CapturedFrameTests
{
    /// <summary>Builds a frame whose blue channel encodes the x coordinate and green the y.</summary>
    private static CapturedFrame Gradient(int width, int height, int? stride = null)
    {
        int actualStride = stride ?? (width * CapturedFrame.BytesPerPixel);
        var pixels = new byte[actualStride * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int p = (y * actualStride) + (x * CapturedFrame.BytesPerPixel);
                pixels[p] = (byte)x;
                pixels[p + 1] = (byte)y;
                pixels[p + 2] = 0;
                pixels[p + 3] = 255;
            }
        }

        return new CapturedFrame(pixels, width, height, actualStride);
    }

    [Fact]
    public void CropTakesTheRequestedPixels()
    {
        var frame = Gradient(64, 48);

        var crop = frame.Crop(10, 20, 8, 6);

        Assert.Equal(8, crop.Width);
        Assert.Equal(6, crop.Height);
        Assert.Equal(8 * CapturedFrame.BytesPerPixel, crop.Stride);

        // Top-left of the crop must be the source pixel at (10, 20).
        Assert.Equal(10, crop.Pixels[0]);
        Assert.Equal(20, crop.Pixels[1]);

        // Bottom-right must be (17, 25).
        int last = ((6 - 1) * crop.Stride) + ((8 - 1) * CapturedFrame.BytesPerPixel);
        Assert.Equal(17, crop.Pixels[last]);
        Assert.Equal(25, crop.Pixels[last + 1]);
    }

    [Fact]
    public void CropHonoursAPaddedSourceStride()
    {
        // Captured textures are row-aligned, so the source stride routinely
        // exceeds width*4. Reading rows at the wrong offset would skew the crop.
        var frame = Gradient(20, 10, stride: 256);

        var crop = frame.Crop(4, 3, 5, 4);

        Assert.Equal(4, crop.Pixels[0]);
        Assert.Equal(3, crop.Pixels[1]);
    }

    [Fact]
    public void CropClampsToTheFrame()
    {
        var frame = Gradient(32, 32);

        var crop = frame.Crop(28, 28, 100, 100);

        Assert.True(crop.Width <= 4);
        Assert.True(crop.Height <= 4);
        Assert.True(crop.Width >= 1 && crop.Height >= 1);
    }

    [Fact]
    public void RejectsABufferSmallerThanTheDescribedImage()
    {
        Assert.Throws<ArgumentException>(() => new CapturedFrame(new byte[16], 32, 32, 128));
    }

    [Fact]
    public void RejectsAStrideNarrowerThanTheRow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CapturedFrame(new byte[4096], 32, 32, 64));
    }
}
