using TLOverlay.Core.Translation;
using Xunit;

namespace TLOverlay.Core.Tests;

public class LlamaOutputCleaningTests
{
    [Theory]
    [InlineData("เธอไม่เข้าใจหรอก", "เธอไม่เข้าใจหรอก")]
    [InlineData("  เธอไม่เข้าใจหรอก  ", "เธอไม่เข้าใจหรอก")]
    [InlineData("\"เธอไม่เข้าใจหรอก\"", "เธอไม่เข้าใจหรอก")]
    [InlineData("Thai: เธอไม่เข้าใจหรอก", "เธอไม่เข้าใจหรอก")]
    [InlineData("คำแปล: เธอไม่เข้าใจหรอก", "เธอไม่เข้าใจหรอก")]
    [InlineData("", "")]
    public void StripsScaffoldingModelsLeakDespiteTheSystemPrompt(string raw, string expected)
    {
        Assert.Equal(expected, LlamaSidecarTranslator.CleanModelOutput(raw));
    }

    [Fact]
    public void DropsATrailingNoteAfterABlankLine()
    {
        string cleaned = LlamaSidecarTranslator.CleanModelOutput(
            "เธอไม่เข้าใจหรอก\n\nNote: this is informal register.");

        Assert.Equal("เธอไม่เข้าใจหรอก", cleaned);
    }

    [Fact]
    public void KeepsGenuineMultiLineDialogue()
    {
        string cleaned = LlamaSidecarTranslator.CleanModelOutput("บรรทัดแรก\nบรรทัดสอง");

        Assert.Equal("บรรทัดแรก\nบรรทัดสอง", cleaned);
    }

    [Fact]
    public void LeavesGlossaryPlaceholdersIntact()
    {
        string cleaned = LlamaSidecarTranslator.CleanModelOutput("[[0]] ฟื้นฟูพลังชีวิต");

        Assert.Equal("[[0]] ฟื้นฟูพลังชีวิต", cleaned);
    }
}
