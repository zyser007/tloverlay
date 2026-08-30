using TLOverlay.Core.Pipeline;
using Xunit;

namespace TLOverlay.Core.Tests;

public class TextSimilarityTests
{
    [Fact]
    public void IdenticalTextScoresOne()
    {
        Assert.Equal(1.0, TextSimilarity.Ratio("hello world", "hello world"));
    }

    [Fact]
    public void OcrJitterStaysHighlySimilar()
    {
        // The same static line read twice, with one character misread.
        double ratio = TextSimilarity.Ratio(
            "The council will not hear you.",
            "The council wiIl not hear you.");

        Assert.True(ratio > 0.95, $"expected > 0.95 but was {ratio}");
    }

    [Fact]
    public void DifferentDialogueLinesScoreLow()
    {
        double ratio = TextSimilarity.Ratio(
            "The council will not hear you.",
            "Take the eastern road at dawn.");

        Assert.True(ratio < 0.5, $"expected < 0.5 but was {ratio}");
    }

    [Fact]
    public void EmptyAgainstNonEmptyScoresZero()
    {
        Assert.Equal(0.0, TextSimilarity.Ratio(string.Empty, "anything"));
    }
}
