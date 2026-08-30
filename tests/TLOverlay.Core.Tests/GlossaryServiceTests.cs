using TLOverlay.Core.Translation;
using Xunit;

namespace TLOverlay.Core.Tests;

public class GlossaryServiceTests
{
    [Fact]
    public void ProtectsAndRestoresATermWithNoTarget()
    {
        var glossary = new GlossaryService([new GlossaryEntry("Aether Blade")]);

        var masked = glossary.Protect("You found the Aether Blade.");

        Assert.DoesNotContain("Aether", masked.Text, StringComparison.Ordinal);
        Assert.Single(masked.Replacements);
        Assert.Contains("[[0]]", masked.Text, StringComparison.Ordinal);

        // Stand in for a translation that kept the placeholder.
        string restored = GlossaryService.Restore("คุณพบ [[0]] แล้ว", masked);

        Assert.Equal("คุณพบ Aether Blade แล้ว", restored);
    }

    [Fact]
    public void SubstitutesAnExplicitTargetWhenGiven()
    {
        var glossary = new GlossaryService([new GlossaryEntry("Potion", "ยาฟื้นพลัง")]);

        var masked = glossary.Protect("Use a Potion.");
        string restored = GlossaryService.Restore("ใช้ [[0]] สิ", masked);

        Assert.Equal("ใช้ ยาฟื้นพลัง สิ", restored);
    }

    [Fact]
    public void LongerTermsWinOverShorterOverlappingOnes()
    {
        var glossary = new GlossaryService([
            new GlossaryEntry("Aether"),
            new GlossaryEntry("Aether Blade"),
        ]);

        var masked = glossary.Protect("The Aether Blade hums.");

        Assert.Single(masked.Replacements);
        Assert.Equal("Aether Blade", masked.Replacements[0]);
    }

    [Fact]
    public void DoesNotMatchInsideLongerWords()
    {
        var glossary = new GlossaryService([new GlossaryEntry("art")]);

        var masked = glossary.Protect("The cart departed.");

        Assert.Empty(masked.Replacements);
        Assert.Equal("The cart departed.", masked.Text);
    }

    [Fact]
    public void ToleratesWhitespaceTheModelAddedInsideAPlaceholder()
    {
        var glossary = new GlossaryService([new GlossaryEntry("Vale")]);

        var masked = glossary.Protect("Return to Vale.");
        string restored = GlossaryService.Restore("กลับไปที่ [[ 0 ]]", masked);

        Assert.Equal("กลับไปที่ Vale", restored);
    }

    [Fact]
    public void LeavesUnknownPlaceholdersAloneInsteadOfThrowing()
    {
        var glossary = new GlossaryService([new GlossaryEntry("Vale")]);

        var masked = glossary.Protect("Return to Vale.");
        string restored = GlossaryService.Restore("[[7]] และ [[0]]", masked);

        Assert.Equal("[[7]] และ Vale", restored);
    }

    [Fact]
    public void EmptyGlossaryPassesTextThroughUnchanged()
    {
        var glossary = new GlossaryService();

        var masked = glossary.Protect("Nothing to protect here.");

        Assert.Equal("Nothing to protect here.", masked.Text);
        Assert.Empty(masked.Replacements);
    }
}
