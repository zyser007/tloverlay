using TLOverlay.Core.Setup;
using Xunit;

namespace TLOverlay.Core.Tests;

public class ModelCatalogTests
{
    [Fact]
    public void EveryEntryPointsAtAGgufOverHttps()
    {
        // The first shipped catalog contained two invented URLs that had never
        // been requested even once. Shape is all a unit test can check; CI runs
        // tools/check-model-urls.ps1 to prove they still resolve.
        Assert.NotEmpty(ModelCatalog.Entries);

        foreach (var entry in ModelCatalog.Entries)
        {
            Assert.Equal(Uri.UriSchemeHttps, entry.Url.Scheme);
            Assert.EndsWith(".gguf", entry.Url.AbsolutePath, StringComparison.OrdinalIgnoreCase);
            Assert.True(entry.ApproximateBytes > 0, $"{entry.Id} has no size");
            Assert.False(string.IsNullOrWhiteSpace(entry.License), $"{entry.Id} has no licence");
        }
    }

    [Fact]
    public void EntryIdsAreUniqueAndNoneCollideWithTheCustomMarker()
    {
        var ids = ModelCatalog.Entries.Select(static e => e.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(ModelCatalog.CustomId, ids, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindByIdMatchesTheDefault()
    {
        Assert.Equal(ModelCatalog.Default, ModelCatalog.FindById(ModelCatalog.Default.Id));
        Assert.Null(ModelCatalog.FindById("no-such-model"));
    }

    [Fact]
    public void CustomEntryTakesItsNameFromTheUrl()
    {
        var entry = ModelCatalog.TryCreateCustom(
            "https://example.invalid/models/my-model-Q4_K_M.gguf");

        Assert.NotNull(entry);
        Assert.Equal(ModelCatalog.CustomId, entry!.Id);
        Assert.Equal("my-model-Q4_K_M.gguf", entry.DisplayName);
    }

    [Fact]
    public void CustomEntryTrimsSurroundingWhitespaceFromAPastedUrl()
    {
        Assert.NotNull(ModelCatalog.TryCreateCustom("  https://example.invalid/a.gguf  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("models/local.gguf")]
    // A local path is not something the downloader can fetch; that is what the
    // Browse button is for.
    [InlineData(@"C:\models\local.gguf")]
    public void UnusableCustomUrlsProduceNothing(string? url)
    {
        Assert.Null(ModelCatalog.TryCreateCustom(url));
    }

    [Fact]
    public void SummaryOmitsTheSizeWhenItIsNotKnownYet()
    {
        var custom = ModelCatalog.TryCreateCustom("https://example.invalid/a.gguf");

        Assert.NotNull(custom);
        Assert.DoesNotContain("GB", custom!.Summary, StringComparison.Ordinal);
        Assert.Contains("GB", ModelCatalog.Default.Summary, StringComparison.Ordinal);
    }
}
