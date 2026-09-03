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

    [Fact]
    public async Task ASecondSweepOverTheSameScreenCostsNothing()
    {
        var inner = new FakeBatchTranslator();
        var caching = new CachingTranslator(inner, new MemoryTranslationCache());

        string[] screen = ["Start Game", "Options", "Quit"];

        IReadOnlyList<string> first = await caching.TranslateBatchAsync(screen);
        IReadOnlyList<string> second = await caching.TranslateBatchAsync(screen);

        Assert.Equal(first, second);

        // The whole point of pressing translate again after the dialogue moves on.
        Assert.Equal(1, inner.BatchCallCount);
    }

    [Fact]
    public async Task OnlyTheLinesThatChangedAreSentAgain()
    {
        var inner = new FakeBatchTranslator();
        var caching = new CachingTranslator(inner, new MemoryTranslationCache());

        await caching.TranslateBatchAsync(["Start Game", "Options"]);

        IReadOnlyList<string> second = await caching.TranslateBatchAsync(["Start Game", "Load Game", "Options"]);

        Assert.Equal(["Load Game"], inner.LastBatch);

        // And the answer still lines up with what the caller asked for, which is
        // what keeps Thai off the wrong English.
        Assert.Equal(["TH:Start Game", "TH:Load Game", "TH:Options"], second);
    }

    [Fact]
    public async Task TheSameWordsTwiceOnOneScreenCostOneEntry()
    {
        var inner = new FakeBatchTranslator();
        var caching = new CachingTranslator(inner, new MemoryTranslationCache());

        IReadOnlyList<string> results = await caching.TranslateBatchAsync(["OK", "Cancel", "OK"]);

        Assert.Equal(["OK", "Cancel"], inner.LastBatch);
        Assert.Equal(["TH:OK", "TH:Cancel", "TH:OK"], results);
    }

    [Fact]
    public async Task BlankLinesKeepTheirPlaces()
    {
        var inner = new FakeBatchTranslator();
        var caching = new CachingTranslator(inner, new MemoryTranslationCache());

        IReadOnlyList<string> results = await caching.TranslateBatchAsync(["Start", "   ", "Quit"]);

        Assert.Equal(3, results.Count);
        Assert.Equal(string.Empty, results[1]);
        Assert.Equal("TH:Quit", results[2]);
    }

    [Fact]
    public async Task ABatchThroughAPlainEngineStillWorks()
    {
        // The engine has no batch path, so the fallback does the work - and the
        // cache still has to line the answers up.
        var inner = new FakeTranslator();
        var caching = new CachingTranslator(inner, new MemoryTranslationCache());

        IReadOnlyList<string> results = await caching.TranslateBatchAsync(["one", "two"]);

        Assert.Equal(["TH:one", "TH:two"], results);
        Assert.Equal(2, inner.CallCount);
    }
}
