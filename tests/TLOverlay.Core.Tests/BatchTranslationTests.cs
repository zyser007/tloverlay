using TLOverlay.Core.Translation;
using Xunit;

namespace TLOverlay.Core.Tests;

/// <summary>
/// Reading a numbered answer back. This is where a full-screen sweep goes wrong
/// quietly: a list rebuilt one row out is not a worse translation, it is Thai
/// painted over the wrong English, and it looks like an overlay bug.
/// </summary>
public class NumberedOutputTests
{
    [Fact]
    public void ACleanAnswerParsesInOrder()
    {
        IReadOnlyList<string?> parsed = ChatTranslationPrompt.ParseNumberedOutput(
            "1. เริ่มเกม\n2. ตั้งค่า\n3. ออก",
            3);

        Assert.Equal(["เริ่มเกม", "ตั้งค่า", "ออก"], parsed);
    }

    [Fact]
    public void OutOfOrderNumberingStillLandsInTheRightPlaces()
    {
        IReadOnlyList<string?> parsed = ChatTranslationPrompt.ParseNumberedOutput(
            "3. ออก\n1. เริ่มเกม\n2. ตั้งค่า",
            3);

        Assert.Equal(["เริ่มเกม", "ตั้งค่า", "ออก"], parsed);
    }

    [Theory]
    [InlineData("1) เริ่มเกม")]
    [InlineData("1: เริ่มเกม")]
    [InlineData("1 - เริ่มเกม")]
    [InlineData("  1.   เริ่มเกม  ")]
    public void TheSeparatorsModelsActuallyUseAllParse(string line)
    {
        Assert.Equal("เริ่มเกม", ChatTranslationPrompt.ParseNumberedOutput(line, 1)[0]);
    }

    [Fact]
    public void ADroppedLineIsReportedRatherThanShiftingTheRest()
    {
        IReadOnlyList<string?> parsed = ChatTranslationPrompt.ParseNumberedOutput(
            "1. เริ่มเกม\n3. ออก",
            3);

        Assert.Equal("เริ่มเกม", parsed[0]);
        Assert.Null(parsed[1]);

        // The one that matters: "ออก" belongs at index 2, not index 1.
        Assert.Equal("ออก", parsed[2]);
    }

    [Fact]
    public void APreambleIsIgnored()
    {
        IReadOnlyList<string?> parsed = ChatTranslationPrompt.ParseNumberedOutput(
            "Here are the translations:\n\n1. เริ่มเกม\n2. ตั้งค่า",
            2);

        Assert.Equal(["เริ่มเกม", "ตั้งค่า"], parsed);
    }

    [Fact]
    public void AThaiLineThatStartsWithADigitIsNotSlicedInHalf()
    {
        // "3 ชิ้น" is a translation, not a line number. Requiring the separator
        // is what keeps the leading digit attached to its own line.
        IReadOnlyList<string?> parsed = ChatTranslationPrompt.ParseNumberedOutput(
            "1. 3 ชิ้น\n2. 15 นาที",
            2);

        Assert.Equal("3 ชิ้น", parsed[0]);
        Assert.Equal("15 นาที", parsed[1]);
    }

    [Fact]
    public void NumbersOutsideTheBatchAreIgnored()
    {
        IReadOnlyList<string?> parsed = ChatTranslationPrompt.ParseNumberedOutput(
            "1. เริ่มเกม\n9. ของแถม",
            2);

        Assert.Equal("เริ่มเกม", parsed[0]);
        Assert.Null(parsed[1]);
    }

    [Fact]
    public void NothingUsableComesBackAsAllMissing()
    {
        Assert.All(
            ChatTranslationPrompt.ParseNumberedOutput("ขอโทษครับ ผมไม่เข้าใจ", 3),
            Assert.Null);

        Assert.All(ChatTranslationPrompt.ParseNumberedOutput(null, 2), Assert.Null);
    }

    [Fact]
    public void TheTokenBudgetGrowsWithTheBatch()
    {
        // 512 - the single-line budget - truncates a forty-line answer, and every
        // line after the cut is simply lost.
        Assert.True(ChatTranslationPrompt.MaxTokensFor(40) > 512);
        Assert.True(ChatTranslationPrompt.MaxTokensFor(1) >= 256);
        Assert.True(ChatTranslationPrompt.MaxTokensFor(1000) <= 8192);
    }
}

public class TranslateManyTests
{
    [Fact]
    public async Task AnEngineWithoutABatchPathGetsOneRequestPerLine()
    {
        var translator = new FakeTranslator();

        IReadOnlyList<string> results = await translator.TranslateManyAsync(["one", "two", "three"]);

        Assert.Equal(["TH:one", "TH:two", "TH:three"], results);
        Assert.Equal(3, translator.CallCount);
    }

    [Fact]
    public async Task AnEngineWithABatchPathGetsOneRequest()
    {
        var translator = new FakeBatchTranslator();

        IReadOnlyList<string> results = await translator.TranslateManyAsync(["one", "two"]);

        Assert.Equal(["TH:one", "TH:two"], results);
        Assert.Equal(1, translator.BatchCallCount);
        Assert.Equal(0, translator.CallCount);
    }

    [Fact]
    public async Task AShortAnswerIsPaddedRatherThanMisaligned()
    {
        // An engine that returns two results for three lines must not leave the
        // caller pairing result[1] with line[2].
        var translator = new FakeBatchTranslator(batch: lines => lines.Take(2).Select(l => "TH:" + l).ToList());

        IReadOnlyList<string> results = await translator.TranslateManyAsync(["one", "two", "three"]);

        Assert.Equal(3, results.Count);
        Assert.Equal("TH:one", results[0]);
        Assert.Equal(string.Empty, results[2]);
    }

    [Fact]
    public async Task NoLinesCostsNoRequest()
    {
        var translator = new FakeBatchTranslator();

        Assert.Empty(await translator.TranslateManyAsync([]));
        Assert.Equal(0, translator.BatchCallCount);
    }
}
