namespace TLOverlay.Core.Translation;

/// <summary>
/// An engine that can translate many lines in one round trip.
///
/// Optional on purpose, and separate from <see cref="ITranslator"/>: a
/// full-screen sweep finds dozens of lines, and asking a hosted model for them
/// one at a time is dozens of billed requests and dozens of round trips for what
/// is one thought. Engines that cannot honour it - the free Google endpoint,
/// anything future - simply do not implement it and get the sequential fallback.
/// </summary>
public interface IBatchTranslator
{
    /// <summary>
    /// Translates every line in one request.
    ///
    /// The result is index-aligned with the input and always the same length. An
    /// entry may be empty, meaning "no translation for this line"; it is never
    /// shorter than the input and never reordered. Callers draw each result over
    /// the line it came from, so a shifted list is not a degraded translation -
    /// it is Thai painted over the wrong English.
    /// </summary>
    Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken = default);
}

/// <summary>Batch translation for any translator, batching or not.</summary>
public static class TranslatorBatch
{
    /// <summary>
    /// Uses the batch path when the engine has one, and falls back to one
    /// request per line when it does not.
    /// </summary>
    public static async Task<IReadOnlyList<string>> TranslateManyAsync(
        this ITranslator translator,
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(translator);
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Count == 0)
        {
            return [];
        }

        if (translator is IBatchTranslator batch)
        {
            IReadOnlyList<string> results = await batch
                .TranslateBatchAsync(lines, cancellationToken)
                .ConfigureAwait(false);

            // A misaligned list would paint Thai over the wrong English, which
            // reads as a broken overlay rather than a broken translator. Pad or
            // trim rather than trusting the count.
            return Align(results, lines.Count);
        }

        var sequential = new string[lines.Count];

        for (int i = 0; i < lines.Count; i++)
        {
            // Sequential rather than parallel: the local server handles one
            // request at a time anyway, and firing forty at a hosted API buys a
            // 429 rather than speed.
            sequential[i] = await translator.TranslateAsync(lines[i], cancellationToken).ConfigureAwait(false);
        }

        return sequential;
    }

    internal static IReadOnlyList<string> Align(IReadOnlyList<string>? results, int expected)
    {
        if (results is not null && results.Count == expected)
        {
            return results;
        }

        var aligned = new string[expected];

        for (int i = 0; i < expected; i++)
        {
            aligned[i] = results is not null && i < results.Count
                ? results[i] ?? string.Empty
                : string.Empty;
        }

        return aligned;
    }
}
