using TLOverlay.Core.Capture;
using TLOverlay.Core.Ocr;
using Xunit;

namespace TLOverlay.Core.Tests;

public class ImagePreprocessorTests
{
    private static CapturedFrame Solid(int width, int height, byte value)
    {
        int stride = width * CapturedFrame.BytesPerPixel;
        var pixels = new byte[stride * height];

        for (int i = 0; i < pixels.Length; i += CapturedFrame.BytesPerPixel)
        {
            pixels[i] = value;
            pixels[i + 1] = value;
            pixels[i + 2] = value;
            pixels[i + 3] = 255;
        }

        return new CapturedFrame(pixels, width, height, stride);
    }

    [Fact]
    public void ScalesASmallRegionUpTowardsTheTargetHeight()
    {
        var options = new PreprocessOptions { TargetHeight = 480, MaxDimension = 2600 };

        // A 60px-tall dialogue strip should be scaled, but never past the 4x cap.
        Assert.Equal(4.0, ImagePreprocessor.ChooseScale(800, 60, options));
    }

    [Fact]
    public void LeavesAlreadyLargeRegionsAlone()
    {
        var options = new PreprocessOptions { TargetHeight = 480, MaxDimension = 2600 };

        Assert.Equal(1.0, ImagePreprocessor.ChooseScale(1200, 600, options));
    }

    [Fact]
    public void ShrinksRegionsThatExceedTheRecogniserLimit()
    {
        var options = new PreprocessOptions { TargetHeight = 480, MaxDimension = 2600 };

        // A whole-screen sweep at 4K: Windows OCR rejects it outright unless we
        // bring it down first.
        double scale = ImagePreprocessor.ChooseScale(3840, 2160, options);

        Assert.True(scale < 1.0);
        Assert.True(3840 * scale <= 2600);
    }

    [Fact]
    public void UpscalingNeverBreachesTheDimensionLimit()
    {
        var options = new PreprocessOptions { TargetHeight = 480, MaxDimension = 1000 };

        double scale = ImagePreprocessor.ChooseScale(900, 100, options);

        Assert.True(900 * scale <= 1000);
    }

    [Fact]
    public void LightTextOnDarkBackgroundIsInverted()
    {
        // Mostly dark, which is what a game dialogue panel looks like.
        var frame = Solid(40, 40, 20);

        var prepared = ImagePreprocessor.Prepare(frame, new PreprocessOptions
        {
            AutoInvert = true,
            StretchContrast = false,
            TargetHeight = 40,
        });

        // After inversion the panel should read as light.
        Assert.True(prepared.Pixels[0] > 200, $"expected a light pixel but got {prepared.Pixels[0]}");
    }

    [Fact]
    public void DarkTextOnLightBackgroundIsLeftAlone()
    {
        var frame = Solid(40, 40, 230);

        var prepared = ImagePreprocessor.Prepare(frame, new PreprocessOptions
        {
            AutoInvert = true,
            StretchContrast = false,
            TargetHeight = 40,
        });

        Assert.True(prepared.Pixels[0] > 200);
    }

    [Fact]
    public void PreparedOutputIsGrayscale()
    {
        int stride = 8 * CapturedFrame.BytesPerPixel;
        var pixels = new byte[stride * 8];

        for (int i = 0; i < pixels.Length; i += CapturedFrame.BytesPerPixel)
        {
            pixels[i] = 200;      // blue
            pixels[i + 1] = 40;   // green
            pixels[i + 2] = 90;   // red
            pixels[i + 3] = 255;
        }

        var prepared = ImagePreprocessor.Prepare(
            new CapturedFrame(pixels, 8, 8, stride),
            new PreprocessOptions { TargetHeight = 8, StretchContrast = false, AutoInvert = false });

        Assert.Equal(prepared.Pixels[0], prepared.Pixels[1]);
        Assert.Equal(prepared.Pixels[1], prepared.Pixels[2]);
    }

    [Fact]
    public void ContrastStretchExpandsAFlatButNotDegenerateRange()
    {
        var luma = new byte[1000];
        for (int i = 0; i < luma.Length; i++)
        {
            // Values spread over 100..160 - readable, but using a quarter of the
            // available range.
            luma[i] = (byte)(100 + (i % 61));
        }

        ImagePreprocessor.StretchContrast(luma);

        Assert.Contains(luma, value => value < 20);
        Assert.Contains(luma, value => value > 235);
    }

    [Fact]
    public void ContrastStretchLeavesANearlyFlatRegionAlone()
    {
        // Amplifying a two-level region would turn sensor-grade noise into
        // high-contrast garbage for the recogniser.
        var luma = new byte[500];
        Array.Fill(luma, (byte)128);
        luma[10] = 132;

        ImagePreprocessor.StretchContrast(luma);

        Assert.Equal(128, luma[0]);
    }
}
