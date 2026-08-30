using System.Text;
using System.Text.RegularExpressions;

namespace TLOverlay.Core.Translation;

/// <summary>
/// A glossary term and what to do with it.
/// <paramref name="Target"/> null means "leave this exactly as written" - the
/// usual case for character and item names that a translator would otherwise
/// render into Thai differently every time.
/// </summary>
public sealed record GlossaryEntry(string Source, string? Target = null);

/// <summary>The result of masking glossary terms out of a source string.</summary>
public sealed record ProtectedText(string Text, IReadOnlyList<string> Replacements)
{
    public static readonly ProtectedText Empty = new(string.Empty, Array.Empty<string>());
}

/// <summary>
/// Keeps proper nouns stable across translations.
///
/// Terms are swapped for numbered placeholders before the text reaches the
/// translator and swapped back afterwards. Masking rather than post-hoc
/// find-and-replace is what makes this reliable: once "Aether Blade" has become
/// Thai text there is nothing left to match on.
/// </summary>
public sealed class GlossaryService
{
    private readonly List<GlossaryEntry> _entries;
    private readonly Regex? _matcher;

    private static readonly Regex PlaceholderPattern = new(
        @"\[\[\s*(\d+)\s*\]\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public GlossaryService(IEnumerable<GlossaryEntry>? entries = null)
    {
        _entries = (entries ?? Array.Empty<GlossaryEntry>())
            .Where(static e => !string.IsNullOrWhiteSpace(e.Source))
            // Longest first, so "Aether Blade" wins over a bare "Aether" entry.
            .OrderByDescending(static e => e.Source.Length)
            .ToList();

        _matcher = _entries.Count == 0 ? null : BuildMatcher(_entries);
    }

    public int Count => _entries.Count;

    /// <summary>Replaces known terms with <c>[[n]]</c> placeholders.</summary>
    public ProtectedText Protect(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return ProtectedText.Empty;
        }

        if (_matcher is null)
        {
            return new ProtectedText(text, Array.Empty<string>());
        }

        var replacements = new List<string>();

        string masked = _matcher.Replace(text, match =>
        {
            var entry = _entries.FirstOrDefault(e =>
                string.Equals(e.Source, match.Value, StringComparison.OrdinalIgnoreCase));

            // Fall back to the matched text so a term with no explicit target is
            // simply carried through untranslated.
            replacements.Add(entry?.Target ?? match.Value);
            return $"[[{replacements.Count - 1}]]";
        });

        return new ProtectedText(masked, replacements);
    }

    /// <summary>
    /// Puts the protected terms back. Tolerates the whitespace a language model
    /// sometimes sprinkles inside the placeholder, and leaves any placeholder
    /// the model invented but we never issued as-is rather than throwing.
    /// </summary>
    public static string Restore(string translated, ProtectedText protectedText)
    {
        ArgumentNullException.ThrowIfNull(protectedText);

        if (string.IsNullOrEmpty(translated) || protectedText.Replacements.Count == 0)
        {
            return translated ?? string.Empty;
        }

        return PlaceholderPattern.Replace(translated, match =>
        {
            if (int.TryParse(match.Groups[1].ValueSpan, out int index)
                && index >= 0
                && index < protectedText.Replacements.Count)
            {
                return protectedText.Replacements[index];
            }

            return match.Value;
        });
    }

    private static Regex BuildMatcher(List<GlossaryEntry> entries)
    {
        var pattern = new StringBuilder();

        foreach (var entry in entries)
        {
            if (pattern.Length > 0)
            {
                pattern.Append('|');
            }

            string escaped = Regex.Escape(entry.Source);

            // Only anchor on word boundaries where the term actually starts or
            // ends with a word character; "+2 Sword" would never match otherwise.
            if (char.IsLetterOrDigit(entry.Source[0]))
            {
                pattern.Append("\\b");
            }

            pattern.Append(escaped);

            if (char.IsLetterOrDigit(entry.Source[^1]))
            {
                pattern.Append("\\b");
            }
        }

        return new Regex(
            pattern.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
