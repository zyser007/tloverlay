using TLOverlay.Core.Translation;
using Xunit;

namespace TLOverlay.Core.Tests;

public class CachingTranslatorTests
{
    [Fact]
    public async Task SecondIdenticalLineSkipsTheModel()
    {
        var inner = new FakeTranslator();
        var caching = new CachingTranslator(inner, new MemoryTranslationCache());

        string first = await caching.TranslateAsync("Hold the line!");
        string second = await caching.TranslateAsync("Hold the line!");

        Assert.Equal(first, second);
        Assert.Equal(1, inner.CallCount);
        Assert.Equal(1, caching.Hits);
        Assert.Equal(1, caching.Misses);
    }

    [Fact]
    public async Task WhitespaceVariationsShareACacheEntry()
    {
        var inner = new FakeTranslator();
        var caching = new CachingTranslator(inner, new MemoryTranslationCache());

        await caching.TranslateAsync("Hold the line!");
        await caching.TranslateAsync("  Hold   the line!  ");

        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task EmptyTranslationsAreNotCached()
    {
        var inner = new FakeTranslator(translate: _ => string.Empty);
        var caching = new CachingTranslator(inner, new MemoryTranslationCache());

        await caching.TranslateAsync("Something");
        await caching.TranslateAsync("Something");

        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task DifferentModelsDoNotShareEntries()
    {
        var cache = new MemoryTranslationCache();
        var modelA = new FakeTranslator("llama:typhoon", _ => "A");
        var modelB = new FakeTranslator("llama:gemma", _ => "B");

        Assert.Equal("A", await new CachingTranslator(modelA, cache).TranslateAsync("Same text"));
        Assert.Equal("B", await new CachingTranslator(modelB, cache).TranslateAsync("Same text"));
    }

    [Fact]
    public async Task BlankInputNeverReachesTheModel()
    {
        var inner = new FakeTranslator();
        var caching = new CachingTranslator(inner, new MemoryTranslationCache());

        Assert.Equal(string.Empty, await caching.TranslateAsync("   "));
        Assert.Equal(0, inner.CallCount);
    }
}
