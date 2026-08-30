using TLOverlay.Core.Profiles;
using Xunit;

namespace TLOverlay.Core.Tests;

public class CaptureRegionTests
{
    [Fact]
    public void ProjectsOntoWindowPixels()
    {
        var region = new CaptureRegion("Dialogue", 0.25, 0.5, 0.5, 0.25);

        var (x, y, w, h) = region.ToPixels(1920, 1080);

        Assert.Equal(480, x);
        Assert.Equal(540, y);
        Assert.Equal(960, w);
        Assert.Equal(270, h);
    }

    [Fact]
    public void SameRegionScalesAcrossResolutions()
    {
        // The point of relative coordinates: a region set at 1080p still lands on
        // the dialogue box at 1440p.
        var region = CaptureRegion.BottomDialogue;

        var (_, y1080, _, _) = region.ToPixels(1920, 1080);
        var (_, y1440, _, _) = region.ToPixels(2560, 1440);

        Assert.Equal(y1080 / 1080.0, y1440 / 1440.0, precision: 2);
    }

    [Fact]
    public void ClampsRegionsThatWouldFallOutsideTheWindow()
    {
        var region = new CaptureRegion("Overflow", 0.9, 0.9, 0.5, 0.5);

        var (x, y, w, h) = region.ToPixels(1000, 1000);

        Assert.True(x + w <= 1000);
        Assert.True(y + h <= 1000);
        Assert.True(w >= 1 && h >= 1);
    }

    [Theory]
    [InlineData(0.0, 0.0, 1.0, 1.0, true)]
    [InlineData(0.1, 0.1, 0.5, 0.5, true)]
    [InlineData(0.1, 0.1, 0.0, 0.5, false)]
    [InlineData(-0.1, 0.1, 0.5, 0.5, false)]
    [InlineData(0.8, 0.1, 0.5, 0.5, false)]
    public void ValidatesBounds(double x, double y, double w, double h, bool expected)
    {
        Assert.Equal(expected, new CaptureRegion("r", x, y, w, h).IsValid);
    }
}
