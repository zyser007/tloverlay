using System.Security.Cryptography;
using System.Text;
using TLOverlay.Core.Pipeline;

namespace TLOverlay.Core.Translation;

/// <summary>
/// Decorator that serves repeat lines from cache instead of the model.
///
/// This is the single biggest perceived-latency win in the app: cached lines
/// appear immediately, uncached ones take as long as the model takes.
/// </summary>
public sealed class CachingTranslator : ITranslator
{
    private readonly ITranslator _inner;
    private readonly ITranslationCache _cache;

    public CachingTranslator(ITranslator inner, ITranslationCache cache)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public string Id => _inner.Id;

    public long Hits { get; private set; }

    public long Misses { get; private set; }

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) =>
        _inner.IsReadyAsync(cancellationToken);

    public async Task<string> TranslateAsync(string text, CancellationToken cancellationToken = default)
    {
        string normalized = TextAssembler.Normalize(text ?? string.Empty);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        string key = BuildKey(_inner.Id, normalized);

        if (_cache.TryGet(key, out string cached))
        {
            Hits++;
            return cached;
        }

        string translated = await _inner.TranslateAsync(normalized, cancellationToken).ConfigureAwait(false);
        Misses++;

        // A failed or empty translation must never be cached, or the bad result
        // sticks for the rest of the playthrough.
        if (!string.IsNullOrWhiteSpace(translated))
        {
            _cache.Set(key, translated);
        }

        return translated;
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    /// <summary>
    /// Keyed on model identity as well as text, so switching model does not
    /// serve the previous model's output.
    /// </summary>
    internal static string BuildKey(string translatorId, string normalizedText)
    {
        // The separator keeps ("ab", "c") from colliding with ("a", "bc").
        byte[] bytes = Encoding.UTF8.GetBytes(translatorId + "\u0001" + normalizedText);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
