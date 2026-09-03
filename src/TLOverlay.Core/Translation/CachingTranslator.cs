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
public sealed class CachingTranslator : ITranslator, IBatchTranslator
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

    /// <summary>
    /// Translates a batch, sending only what is not already known.
    ///
    /// This is what makes pressing translate again after the dialogue advances
    /// bearable: most of a game screen - menu labels, button captions, the quest
    /// title - is identical to the last sweep, so the second pass usually costs a
    /// request for the two or three lines that actually changed.
    ///
    /// Two properties matter more than the saving. Duplicates on one screen ("OK"
    /// in three places) become one entry, and the results come back in the
    /// caller's order. A misalignment here is not a worse translation, it is Thai
    /// painted over the wrong English - which reads as an overlay bug and is not.
    /// </summary>
    public async Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var results = new string[lines.Count];
        var normalized = new string[lines.Count];

        // Distinct misses, and every input position each one answers for.
        var pending = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var order = new List<string>();

        for (int i = 0; i < lines.Count; i++)
        {
            normalized[i] = TextAssembler.Normalize(lines[i] ?? string.Empty);
            results[i] = string.Empty;

            if (normalized[i].Length == 0)
            {
                continue;
            }

            if (_cache.TryGet(BuildKey(_inner.Id, normalized[i]), out string cached))
            {
                Hits++;
                results[i] = cached;
                continue;
            }

            if (pending.TryGetValue(normalized[i], out List<int>? positions))
            {
                positions.Add(i);
                continue;
            }

            pending[normalized[i]] = [i];
            order.Add(normalized[i]);
        }

        if (order.Count == 0)
        {
            return results;
        }

        IReadOnlyList<string> translated = await _inner
            .TranslateManyAsync(order, cancellationToken)
            .ConfigureAwait(false);

        for (int i = 0; i < order.Count; i++)
        {
            string text = translated[i];
            Misses++;

            // A failed or empty translation must never be cached, or the bad
            // result sticks for the rest of the playthrough.
            if (!string.IsNullOrWhiteSpace(text))
            {
                _cache.Set(BuildKey(_inner.Id, order[i]), text);
            }

            foreach (int position in pending[order[i]])
            {
                results[position] = text;
            }
        }

        return results;
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
