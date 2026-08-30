namespace TLOverlay.Core.Pipeline;

/// <summary>
/// OCR output jitters: a static dialogue box re-recognised a second later can
/// come back with one character different. The pipeline uses this to decide
/// whether newly recognised text is genuinely new or just the same line read
/// slightly differently, so it doesn't re-run the translator for nothing.
/// </summary>
public static class TextSimilarity
{
    /// <summary>
    /// Returns 1.0 for identical strings, 0.0 for completely different ones,
    /// using normalised Levenshtein distance.
    /// </summary>
    public static double Ratio(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (ReferenceEquals(a, b) || string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1.0;
        }

        if (a.Length == 0 || b.Length == 0)
        {
            return 0.0;
        }

        int distance = Distance(a, b);
        int longest = Math.Max(a.Length, b.Length);
        return 1.0 - ((double)distance / longest);
    }

    private static int Distance(string a, string b)
    {
        // Two-row Levenshtein: O(min(n,m)) memory.
        if (a.Length > b.Length)
        {
            (a, b) = (b, a);
        }

        var previous = new int[a.Length + 1];
        var current = new int[a.Length + 1];

        for (int i = 0; i <= a.Length; i++)
        {
            previous[i] = i;
        }

        for (int j = 1; j <= b.Length; j++)
        {
            current[0] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                int substitution = previous[i - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                int deletion = previous[i] + 1;
                int insertion = current[i - 1] + 1;
                current[i] = Math.Min(substitution, Math.Min(deletion, insertion));
            }

            (previous, current) = (current, previous);
        }

        return previous[a.Length];
    }
}
