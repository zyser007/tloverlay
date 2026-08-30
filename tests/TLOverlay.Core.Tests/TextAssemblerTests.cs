using TLOverlay.Core.Ocr;
using TLOverlay.Core.Pipeline;
using Xunit;

namespace TLOverlay.Core.Tests;

public class TextAssemblerTests
{
    private static OcrLine Line(string text, double y, double x = 0) =>
        OcrLine.FromText(text, new TextRect(x, y, 400, 20));

    [Fact]
    public void JoinsWrappedLinesWithASpace()
    {
        var result = new OcrResult([
            Line("The gate will not open", 0),
            Line("until the seal is broken.", 24),
        ]);

        Assert.Equal("The gate will not open until the seal is broken.", TextAssembler.Assemble(result));
    }

    [Fact]
    public void RejoinsWordsSplitAcrossLines()
    {
        var result = new OcrResult([
            Line("This artefact reduces mana consump-", 0),
            Line("tion by thirty percent.", 24),
        ]);

        Assert.Equal("This artefact reduces mana consumption by thirty percent.", TextAssembler.Assemble(result));
    }

    [Fact]
    public void KeepsRealHyphensInCompoundNames()
    {
        // "Anti-" followed by a capitalised word is a compound, not a wrap.
        var result = new OcrResult([
            Line("He wields the Anti-", 0),
            Line("Magic Blade.", 24),
        ]);

        Assert.Equal("He wields the Anti- Magic Blade.", TextAssembler.Assemble(result));
    }

    [Fact]
    public void OrdersLinesTopToBottomRegardlessOfInputOrder()
    {
        var result = new OcrResult([
            Line("second line", 40),
            Line("first line", 10),
        ]);

        Assert.Equal("first line second line", TextAssembler.Assemble(result));
    }

    [Fact]
    public void DropsHudGlyphNoise()
    {
        var result = new OcrResult([
            Line("|||", 0),
            Line("Welcome, traveller.", 24),
            Line("*", 48),
        ]);

        Assert.Equal("Welcome, traveller.", TextAssembler.Assemble(result));
    }

    [Fact]
    public void CollapsesRunsOfWhitespace()
    {
        var result = new OcrResult([Line("Too    many     spaces", 0)]);

        Assert.Equal("Too many spaces", TextAssembler.Assemble(result));
    }

    [Fact]
    public void EmptyInputProducesEmptyString()
    {
        Assert.Equal(string.Empty, TextAssembler.Assemble(OcrResult.Empty));
    }

    [Theory]
    [InlineData("She turned away.", true)]
    [InlineData("Are you certain?", true)]
    [InlineData("Stop right there!", true)]
    [InlineData("\"I never said that.\"", true)]
    [InlineData("He walked towards the", false)]
    [InlineData("", false)]
    public void LooksCompleteDetectsFinishedSentences(string text, bool expected)
    {
        Assert.Equal(expected, TextAssembler.LooksComplete(text));
    }

    [Fact]
    public void NormalizeFoldsNonBreakingSpaces()
    {
        Assert.Equal("a b", TextAssembler.Normalize("a\u00A0\u3000b  "));
    }
}
