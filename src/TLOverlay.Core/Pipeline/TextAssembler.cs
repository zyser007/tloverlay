using System.Globalization;
using System.Text;
using TLOverlay.Core.Ocr;

namespace TLOverlay.Core.Pipeline;

/// <summary>
/// Turns raw OCR lines into one sentence-shaped string fit to hand a translator.
///
/// OCR gives us visual lines, not sentences: a dialogue box wraps mid-sentence,
/// sometimes mid-word, and picks up stray glyphs from HUD art sitting inside the
/// region. Feeding that to a translator line by line produces nonsense, so we
/// reassemble first.
/// </summary>
public static class TextAssembler
{
    /// <summary>
    /// A line whose characters are mostly not letters, digits or ordinary
    /// punctuation is almost certainly HUD art rather than text.
    /// </summary>
    private const double MinPlausibleRatio = 0.55;

    public static string Assemble(OcrResult result) => Assemble(result?.Lines ?? Array.Empty<OcrLine>());

    public static string Assemble(IReadOnlyList<OcrLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var ordered = lines
            .Where(static line => !string.IsNullOrWhiteSpace(line.Text))
            .Select(static line => line with { Text = line.Text.Trim() })
            .Where(static line => IsPlausibleText(line.Text))
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X)
            .ToList();

        if (ordered.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        for (int i = 0; i < ordered.Count; i++)
        {
            string text = ordered[i].Text;

            if (builder.Length == 0)
            {
                builder.Append(text);
                continue;
            }

            if (EndsWithSoftHyphen(builder))
            {
                // "consump-" + "tion" -> "consumption". Only when the next line
                // starts lower-case; "Anti-" + "Magic" is a real hyphen.
                if (text.Length > 0 && char.IsLower(text[0]))
                {
                    builder.Length -= 1;
                    builder.Append(text);
                    continue;
                }
            }

            builder.Append(' ').Append(text);
        }

        return CollapseWhitespace(builder.ToString());
    }

    /// <summary>
    /// Whether the assembled text reads like a finished sentence. Used to hold
    /// back mid-reveal dialogue for one more settle cycle.
    /// </summary>
    public static bool LooksComplete(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // Walk back over closing quotes and brackets to the last real character.
        int i = text.Length - 1;
        while (i >= 0 && (char.IsWhiteSpace(text[i]) || IsClosing(text[i])))
        {
            i--;
        }

        if (i < 0)
        {
            return false;
        }

        return text[i] is '.' or '!' or '?' or '…' or ':' or ';';
    }

    /// <summary>
    /// Canonical form used for cache keys and for comparing one OCR pass with
    /// the next. Case is preserved because it matters to translation quality.
    /// </summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // Games often emit non-breaking or ideographic spaces; fold them first.
        return CollapseWhitespace(text.Replace('\u00A0', ' ').Replace('\u3000', ' '));
    }

    private static bool EndsWithSoftHyphen(StringBuilder builder)
    {
        if (builder.Length < 2)
        {
            return false;
        }

        return (builder[^1] is '-' or '\u2010') && char.IsLetter(builder[^2]);
    }

    private static bool IsClosing(char c) => c is '"' or '\'' or ')' or ']' or '}' or '”' or '’' or '」' or '』';

    internal static bool IsPlausibleText(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        int letters = 0;
        int plausible = 0;

        foreach (char c in text)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);

            if (char.IsLetter(c))
            {
                letters++;
            }

            bool ok = char.IsLetterOrDigit(c)
                || char.IsWhiteSpace(c)
                || category is UnicodeCategory.OpenPunctuation
                    or UnicodeCategory.ClosePunctuation
                    or UnicodeCategory.InitialQuotePunctuation
                    or UnicodeCategory.FinalQuotePunctuation
                    or UnicodeCategory.DashPunctuation
                    or UnicodeCategory.OtherPunctuation
                    or UnicodeCategory.ConnectorPunctuation;

            if (ok)
            {
                plausible++;
            }
        }

        // A lone letter is nearly always a misread HUD glyph, but a lone digit
        // can be a legitimate menu entry, so require a letter somewhere unless
        // the line is a bare number.
        if (letters == 0 && !text.All(static c => char.IsDigit(c) || char.IsWhiteSpace(c)))
        {
            return false;
        }

        if (letters == 1 && text.Length <= 2)
        {
            return false;
        }

        return (double)plausible / text.Length >= MinPlausibleRatio;
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool lastWasSpace = false;

        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            builder.Append(c);
            lastWasSpace = false;
        }

        return builder.ToString().TrimEnd();
    }
}
