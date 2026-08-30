using TLOverlay.Core.Translation;
using Xunit;

namespace TLOverlay.Core.Tests;

public class TranslationCacheTests
{
    [Fact]
    public void MemoryCacheEvictsTheLeastRecentlyUsedEntry()
    {
        var cache = new MemoryTranslationCache(capacity: 2);

        cache.Set("a", "1");
        cache.Set("b", "2");
        cache.TryGet("a", out _);   // "a" is now the most recent.
        cache.Set("c", "3");        // evicts "b"

        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void MemoryCacheOverwritesWithoutGrowing()
    {
        var cache = new MemoryTranslationCache(capacity: 4);

        cache.Set("k", "first");
        cache.Set("k", "second");

        Assert.True(cache.TryGet("k", out string value));
        Assert.Equal("second", value);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void SqliteCacheRoundTripsAndUpserts()
    {
        using var cache = SqliteTranslationCache.CreateInMemory();

        cache.Set("key", "แปลแล้ว");
        Assert.True(cache.TryGet("key", out string value));
        Assert.Equal("แปลแล้ว", value);

        cache.Set("key", "แก้ไขแล้ว");
        Assert.True(cache.TryGet("key", out value));
        Assert.Equal("แก้ไขแล้ว", value);
    }

    [Fact]
    public void SqliteCacheSurvivesAReopen()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tloverlay-cache-{Guid.NewGuid():N}.db");

        try
        {
            using (var writer = new SqliteTranslationCache(path))
            {
                writer.Set("persisted", "ยังอยู่");
            }

            using var reader = new SqliteTranslationCache(path);

            Assert.True(reader.TryGet("persisted", out string value));
            Assert.Equal("ยังอยู่", value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LayeredCachePromotesFromSlowToFast()
    {
        var fast = new MemoryTranslationCache();
        var slow = new MemoryTranslationCache();
        slow.Set("k", "v");

        var layered = new LayeredTranslationCache(fast, slow);

        Assert.True(layered.TryGet("k", out string value));
        Assert.Equal("v", value);
        Assert.True(fast.TryGet("k", out _));
    }
}
