namespace TLOverlay.Core.Translation;

/// <summary>
/// Runs a batch of lines through a chat model: chunking, glossary masking, and
/// what to do when the answer comes back with lines missing.
///
/// Shared by the local model and the hosted one, for the same reason the prompt
/// is shared - a player who switches engines should get the same behaviour, not
/// two subtly different batch implementations.
/// </summary>
internal static class ChatBatch
{
    /// <summary>
    /// Lines per request. Small models degrade sharply past about this many:
    /// they start merging adjacent lines and dropping numbers, and one dropped
    /// line costs a whole extra round trip to recover.
    /// </summary>
    public const int MaxLinesPerRequest = 20;

    /// <summary>
    /// How many lines a sweep may re-ask for one at a time.
    ///
    /// The salvage path is what makes a dropped line cost one request instead of
    /// a lost translation - but a model that ignored the format entirely would
    /// otherwise turn one batch into forty billed requests. Past this, the
    /// remaining lines come back empty and the player sees fewer boxes rather
    /// than an unexpected bill.
    /// </summary>
    public const int MaxSalvageRequests = 8;

    /// <summary>
    /// <paramref name="askModel"/> sends already-masked lines and returns the raw
    /// model answer. <paramref name="askOne"/> is the engine's ordinary
    /// single-line path, used to fill gaps; it does its own masking.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RunAsync(
        IReadOnlyList<string> lines,
        GlossaryService glossary,
        Func<IReadOnlyList<string>, CancellationToken, Task<string>> askModel,
        Func<string, CancellationToken, Task<string>> askOne,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(glossary);
        ArgumentNullException.ThrowIfNull(askModel);
        ArgumentNullException.ThrowIfNull(askOne);

        var results = new string[lines.Count];
        var missing = new List<int>();

        for (int start = 0; start < lines.Count; start += MaxLinesPerRequest)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int count = Math.Min(MaxLinesPerRequest, lines.Count - start);
            await RunChunkAsync(lines, start, count, glossary, askModel, results, missing, cancellationToken)
                .ConfigureAwait(false);
        }

        int salvaged = 0;

        foreach (int index in missing)
        {
            if (salvaged >= MaxSalvageRequests)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            salvaged++;

            results[index] = await askOne(lines[index], cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    private static async Task RunChunkAsync(
        IReadOnlyList<string> lines,
        int start,
        int count,
        GlossaryService glossary,
        Func<IReadOnlyList<string>, CancellationToken, Task<string>> askModel,
        string[] results,
        List<int> missing,
        CancellationToken cancellationToken)
    {
        // Masked per line rather than across the chunk. Placeholders are numbered
        // from zero within one Protect call, so sharing the numbering would make
        // the restore for line twelve depend on how many terms lines one to
        // eleven happened to contain - and the salvage path, which re-asks for a
        // single line, would then be restoring against different numbers.
        var masked = new ProtectedText[count];
        var payload = new string[count];

        for (int i = 0; i < count; i++)
        {
            masked[i] = glossary.Protect(lines[start + i]);

            // Newlines would break the one-line-per-number contract.
            payload[i] = masked[i].Text.Replace('\n', ' ').Replace('\r', ' ');
        }

        string raw = await askModel(payload, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string?> parsed = ChatTranslationPrompt.ParseNumberedOutput(raw, count);

        for (int i = 0; i < count; i++)
        {
            string? line = parsed[i];

            if (string.IsNullOrWhiteSpace(line))
            {
                results[start + i] = string.Empty;
                missing.Add(start + i);
                continue;
            }

            results[start + i] = GlossaryService.Restore(line, masked[i]);
        }
    }
}
