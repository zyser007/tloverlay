using TLOverlay.Core.Pipeline;
using TLOverlay.Core.Profiles;
using Xunit;

namespace TLOverlay.Core.Tests;

/// <summary>
/// How big the Thai on a full-screen label ends up. The overlay cannot be run on
/// CI, so this is the only place the sizing policy is checked - and it is a
/// policy that is wrong in a way nobody reports as a bug, they just quietly stop
/// using the mode because they cannot read it.
/// </summary>
public class ScreenLabelMetricsTests
{
    private const double TypicalBoxHeight = 30;

    [Fact]
    public void TheDefaultSettingSizesTextFromTheBoxItReplaces()
    {
        // 0.68 of the box: an OCR rectangle includes the line's leading, so
        // filling it exactly puts the text against both edges.
        Assert.Equal(
            20.4,
            ScreenLabelMetrics.StartingFontSize(TypicalBoxHeight, ScreenLabelMetrics.NeutralFontSize),
            3);
    }

    [Fact]
    public void AskingForBiggerTextActuallyGetsBiggerText()
    {
        // The whole point of the setting. It used to be an upper clamp, which
        // meant raising it did nothing at all to a line whose box was short -
        // that is, to most of a game screen.
        double normal = ScreenLabelMetrics.StartingFontSize(TypicalBoxHeight, ScreenLabelMetrics.NeutralFontSize);
        double larger = ScreenLabelMetrics.StartingFontSize(TypicalBoxHeight, ScreenLabelMetrics.NeutralFontSize * 2);

        Assert.Equal(normal * 2, larger, 3);
    }

    [Fact]
    public void AskingForSmallerTextGetsSmallerText()
    {
        Assert.True(
            ScreenLabelMetrics.StartingFontSize(TypicalBoxHeight, 12) <
            ScreenLabelMetrics.StartingFontSize(TypicalBoxHeight, ScreenLabelMetrics.NeutralFontSize));
    }

    [Fact]
    public void TheSizeStillFollowsTheBoxAtAnySetting()
    {
        // A menu label and a line of dialogue want different sizes on the same
        // screen, which is why the setting scales the fit instead of replacing it.
        Assert.True(
            ScreenLabelMetrics.StartingFontSize(60, 30) >
            ScreenLabelMetrics.StartingFontSize(20, 30));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-5)]
    public void NothingIsEverSizedBelowTheFloor(double boxHeight)
    {
        Assert.Equal(
            ScreenLabelMetrics.MinimumFontSize,
            ScreenLabelMetrics.StartingFontSize(boxHeight, ScreenLabelMetrics.NeutralFontSize));
    }

    [Fact]
    public void AProfileWithNoFontSizeIsTreatedAsNeutral()
    {
        // Rather than collapsing every label to the floor, which is what
        // dividing by it would do.
        Assert.Equal(
            ScreenLabelMetrics.StartingFontSize(TypicalBoxHeight, ScreenLabelMetrics.NeutralFontSize),
            ScreenLabelMetrics.StartingFontSize(TypicalBoxHeight, 0),
            3);
    }

    [Fact]
    public void ThaiIsAllowedToBeWiderThanTheEnglishItReplaces()
    {
        // The budget is the grown box, not the original. Measuring against the
        // English width meant shrinking nearly every line, because Thai is
        // almost always longer.
        Assert.True(ScreenLabelMetrics.WidthBudget(200, ScreenLabelMetrics.NeutralFontSize) > 200);
    }

    [Fact]
    public void TextThatFitsIsLeftAtTheSizeItAskedFor()
    {
        Assert.Equal(20, ScreenLabelMetrics.ShrinkToFit(20, neededWidth: 100, budget: 300));
    }

    [Fact]
    public void TextThatDoesNotFitShrinksInProportion()
    {
        // Twice as wide as the budget allows, so half the size.
        Assert.Equal(10, ScreenLabelMetrics.ShrinkToFit(20, neededWidth: 400, budget: 200));
    }

    [Fact]
    public void ShrinkingStopsAtTheFloorRatherThanVanishing()
    {
        Assert.Equal(
            ScreenLabelMetrics.MinimumFontSize,
            ScreenLabelMetrics.ShrinkToFit(20, neededWidth: 10000, budget: 50));
    }

    [Fact]
    public void AVeryLongLineInASmallBoxIsStillLegibleAtALargeSetting()
    {
        // The combination that used to produce nine-point text: a wide
        // translation in a short HUD box. It should end up bigger with the
        // setting turned up, not identical.
        double atDefault = Fit(ScreenLabelMetrics.NeutralFontSize);
        double atMaximum = Fit(GameProfile.MaximumFontSize);

        Assert.True(atMaximum > atDefault, $"{atMaximum} was not larger than {atDefault}");

        static double Fit(double setting)
        {
            const double boxWidth = 120;
            double size = ScreenLabelMetrics.StartingFontSize(24, setting);

            // Thai at roughly half the font size per character, forty characters.
            double needed = size * 0.5 * 40;

            return ScreenLabelMetrics.ShrinkToFit(size, needed, ScreenLabelMetrics.WidthBudget(boxWidth, setting));
        }
    }

    [Fact]
    public void TurningTheSettingDownDoesNotNarrowTheBox()
    {
        // Smaller text in the box it already had, not a tighter box: shrinking
        // the budget would start trimming lines that fit perfectly well before.
        Assert.Equal(
            ScreenLabelMetrics.MaxGrowth,
            ScreenLabelMetrics.GrowthFor(GameProfile.MinimumFontSize));
    }

    [Fact]
    public void TheProfileDefaultAndTheNeutralSizeAgree()
    {
        // They are the same number in two places by design - the setting scales
        // from the default, so a fresh profile has to mean "no scaling".
        Assert.Equal(
            ScreenLabelMetrics.NeutralFontSize,
            GameProfile.CreateDefault("Default").FontSize);
    }

    [Fact]
    public void TheChoosableRangeStraddlesTheDefault()
    {
        Assert.True(GameProfile.MinimumFontSize < ScreenLabelMetrics.NeutralFontSize);
        Assert.True(GameProfile.MaximumFontSize > ScreenLabelMetrics.NeutralFontSize);
        Assert.True(GameProfile.MinimumFontSize >= ScreenLabelMetrics.MinimumFontSize);
    }
}
