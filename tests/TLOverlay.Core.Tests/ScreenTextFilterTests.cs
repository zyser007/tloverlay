using TLOverlay.Core.Ocr;
using TLOverlay.Core.Pipeline;
using Xunit;

namespace TLOverlay.Core.Tests;

/// <summary>
/// What a full-screen sweep decides to spend translation on. Every line that
/// gets through costs a line in the request and, on a metered engine, money -
/// so the filtering is the difference between translating a game and translating
/// its frame counter.
/// </summary>
public class ScreenTextFilterTests
{
    private const int Width = 1920;
    private const int Height = 1080;

    private static OcrLine Line(string text, double x = 100, double y = 100, double w = 200, double h = 24) =>
        OcrLine.FromText(text, new TextRect(x, y, w, h));

    private static IReadOnlyList<ScreenCandidate> Select(params OcrLine[] lines) =>
        ScreenTextFilter.Select(new OcrResult(lines), Width, Height);

    [Fact]
    public void OrdinaryDialogueGetsThrough()
    {
        Assert.Equal(
            "The gate will not open.",
            Assert.Single(Select(Line("The gate will not open."))).Text);
    }

    [Theory]
    [InlineData("1,240")]
    [InlineData("00:32")]
    [InlineData("98%")]
    [InlineData("-15")]
    public void NumbersOnTheHudAreNotWorthTranslating(string text)
    {
        // Their Thai is identical to their English, and on a whole screen they
        // are most of what OCR finds.
        Assert.Empty(Select(Line(text)));
    }

    [Theory]
    [InlineData("X")]
    [InlineData("»")]
    [InlineData("::")]
    public void SingleGlyphsAndSymbolSoupAreDropped(string text)
    {
        Assert.Empty(Select(Line(text)));
    }

    [Fact]
    public void TextThatIsAlreadyThaiIsSkipped()
    {
        // The overlay is excluded from capture, but that call can fail - and
        // translating our own output back would be baffling to watch.
        Assert.Empty(Select(Line("ประตูจะไม่เปิด")));
    }

    [Fact]
    public void BoxesTooSmallToHaveBeenReadProperlyAreDropped()
    {
        Assert.Empty(Select(Line("Menu", h: 5)));
        Assert.Empty(Select(Line("Menu", w: 0)));
    }

    [Fact]
    public void BoundsComeBackAsFractionsOfTheFrame()
    {
        ScreenCandidate candidate = Assert.Single(Select(Line("Continue", x: 960, y: 540, w: 192, h: 54)));

        Assert.Equal(0.5, candidate.Bounds.X, 3);
        Assert.Equal(0.5, candidate.Bounds.Y, 3);
        Assert.Equal(0.1, candidate.Bounds.Width, 3);
        Assert.Equal(0.05, candidate.Bounds.Height, 3);
    }

    [Fact]
    public void ASmallLineKeepsItsSmallBox()
    {
        // RelativeRect.Clamped would round this up to a twentieth of the screen
        // in each direction - right for a draggable panel, very wrong for a label
        // that has to sit on one line of HUD text.
        ScreenCandidate candidate = Assert.Single(Select(Line("Save", x: 0, y: 0, w: 60, h: 18)));

        Assert.True(candidate.Bounds.Height < 0.02, $"height was {candidate.Bounds.Height}");
        Assert.True(candidate.Bounds.Width < 0.04, $"width was {candidate.Bounds.Width}");
    }

    [Fact]
    public void WhenThereIsTooMuchTheBiggestBoxesSurvive()
    {
        var lines = new List<OcrLine> { Line("The dialogue that matters", w: 900, h: 60, y: 800) };

        for (int i = 0; i < 60; i++)
        {
            lines.Add(Line($"tiny hud label {i}", x: 10, y: 10 + i, w: 80, h: 12));
        }

        IReadOnlyList<ScreenCandidate> selected = ScreenTextFilter.Select(
            new OcrResult(lines), Width, Height, maxLines: 5);

        Assert.Equal(5, selected.Count);

        // Dropping the dialogue and keeping the frame counter would be exactly
        // backwards.
        Assert.Contains(selected, c => c.Text == "The dialogue that matters");
    }

    [Fact]
    public void ResultsComeBackInReadingOrder()
    {
        IReadOnlyList<ScreenCandidate> selected = Select(
            Line("bottom line here", y: 900, w: 100, h: 20),
            Line("top line here", y: 100, w: 900, h: 60));

        Assert.Equal("top line here", selected[0].Text);
        Assert.Equal("bottom line here", selected[1].Text);
    }

    [Fact]
    public void TheCharacterBudgetStopsAccumulation()
    {
        var lines = Enumerable.Range(0, 40)
            .Select(i => Line($"a sentence worth translating number {i}", y: i * 20))
            .ToList();

        IReadOnlyList<ScreenCandidate> selected = ScreenTextFilter.Select(
            new OcrResult(lines), Width, Height, maxCharacters: 100);

        Assert.True(selected.Count is > 0 and < 5, $"kept {selected.Count}");
        Assert.True(selected.Sum(c => c.Text.Length) <= 100);
    }

    [Fact]
    public void NothingToReadIsNotAnError()
    {
        Assert.Empty(ScreenTextFilter.Select(null, Width, Height));
        Assert.Empty(ScreenTextFilter.Select(OcrResult.Empty, Width, Height));
        Assert.Empty(ScreenTextFilter.Select(new OcrResult([Line("Hello there")]), 0, 0));
    }
}
