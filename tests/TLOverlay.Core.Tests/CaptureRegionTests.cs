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

    [Fact]
    public void ClampKeepsADraggedPanelInsideTheWindow()
    {
        // Dragging the panel past the right edge must not leave it somewhere the
        // player can no longer grab it.
        var clamped = new RelativeRect(0.9, 0.9, 0.4, 0.3).Clamped();

        Assert.True(clamped.X + clamped.Width <= 1.0001);
        Assert.True(clamped.Y + clamped.Height <= 1.0001);
    }

    [Fact]
    public void ClampRefusesToCollapseAPanelToNothing()
    {
        var clamped = new RelativeRect(0.5, 0.5, 0.001, 0.001).Clamped(minimumSize: 0.05);

        Assert.Equal(0.05, clamped.Width, precision: 6);
        Assert.Equal(0.05, clamped.Height, precision: 6);
    }

    [Fact]
    public void ClampLeavesAReasonableRectangleAlone()
    {
        var original = new RelativeRect(0.2, 0.3, 0.4, 0.2);

        Assert.Equal(original, original.Clamped());
    }

    [Fact]
    public void SettingTheRegionReplacesRatherThanAppends()
    {
        // One region per profile: setting a new one must not leave the old.
        var profile = GameProfile.CreateDefault("Test");

        profile.SetRegion(new CaptureRegion("Whatever", 0.1, 0.1, 0.3, 0.2));

        Assert.Single(profile.Regions);
        Assert.Equal(CaptureRegion.DefaultName, profile.Regions[0].Name);
        Assert.Equal(0.1, profile.Region!.X);
    }

    [Fact]
    public void ClearingTheRegionLeavesNone()
    {
        var profile = GameProfile.CreateDefault("Test");

        profile.SetRegion(null);

        Assert.Empty(profile.Regions);
        Assert.Null(profile.Region);
    }

    [Fact]
    public void AnInvalidStoredRegionIsNotReturned()
    {
        // A hand-edited profile should not feed a zero-sized region to the pipeline.
        var profile = GameProfile.CreateDefault("Test");
        profile.Regions = [new CaptureRegion("Dialogue", 0.1, 0.1, 0, 0)];

        Assert.Null(profile.Region);
    }
}
