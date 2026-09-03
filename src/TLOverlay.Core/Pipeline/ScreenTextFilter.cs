using TLOverlay.Core.Ocr;
using TLOverlay.Core.Profiles;

namespace TLOverlay.Core.Pipeline;

/// <summary>One line worth translating, and where it sits.</summary>
public sealed record ScreenCandidate(string Text, RelativeRect Bounds);

/// <summary>
/// Decides which of a full screen's OCR lines are worth translating.
///
/// A sweep of a whole game window finds far more than dialogue: damage numbers,
/// a clock, a frame counter, single glyphs from HUD art, the same button label
/// in four places. Every one of those costs a line in the request and, on a
/// metered engine, money - and none of them reads any differently in Thai. The
/// filtering happens before anything is spent, not after.
/// </summary>
public static class ScreenTextFilter
{
    /// <summary>
    /// Lines per sweep. Past this the request is slow everywhere and expensive
    /// on a hosted engine, and a screen with forty distinct readable lines on it
    /// is already more than a player can take in.
    /// </summary>
    public const int MaxLines = 40;

    /// <summary>Characters per sweep, whichever cap bites first.</summary>
    public const int MaxCharacters = 4000;

    /// <summary>
    /// Below this many pixels tall, Windows OCR is guessing rather than reading -
    /// and a box that small cannot hold a legible Thai label anyway.
    /// </summary>
    private const double MinimumHeightPixels = 8;

    /// <summary>
    /// Picks the lines to translate from one full-frame OCR pass.
    ///
    /// When something has to be dropped, the biggest boxes are kept: if a screen
    /// is over the cap it is the tiny HUD text that goes, not the dialogue.
    /// </summary>
    public static IReadOnlyList<ScreenCandidate> Select(
        OcrResult? result,
        int frameWidth,
        int frameHeight,
        int maxLines = MaxLines,
        int maxCharacters = MaxCharacters)
    {
        if (result is null || result.Lines.Count == 0 || frameWidth <= 0 || frameHeight <= 0)
        {
            return [];
        }

        var kept = new List<ScreenCandidate>();
        int characters = 0;

        IEnumerable<OcrLine> byImportance = result.Lines
            .Where(line => IsWorthTranslating(line))
            .OrderByDescending(static line => line.Bounds.Width * line.Bounds.Height);

        foreach (OcrLine line in byImportance)
        {
            string text = line.Text.Trim();

            if (kept.Count >= maxLines || characters + text.Length > maxCharacters)
            {
                break;
            }

            characters += text.Length;
            kept.Add(new ScreenCandidate(text, ToFraction(line.Bounds, frameWidth, frameHeight)));
        }

        // Reading order for the result, even though the cap was applied by size.
        return [.. kept.OrderBy(static c => c.Bounds.Y).ThenBy(static c => c.Bounds.X)];
    }

    internal static bool IsWorthTranslating(OcrLine line)
    {
        string text = line.Text?.Trim() ?? string.Empty;

        if (text.Length < 2)
        {
            return false;
        }

        // Symbol soup and lone glyphs, using the same judgement the region path
        // has always applied.
        if (!TextAssembler.IsPlausibleText(text))
        {
            return false;
        }

        // Two letters, not one: the region path deliberately admits bare numbers
        // because a dialogue box rarely contains them, but on a whole screen
        // "1,240", "00:32" and "98%" are most of what OCR finds and their Thai is
        // identical to their English.
        if (text.Count(char.IsLetter) < 2)
        {
            return false;
        }

        // Thai already. The overlay is excluded from capture, but that call can
        // fail - the app warns when it does - and translating our own output back
        // would be a genuinely baffling thing to watch.
        if (text.Any(static c => c is >= '฀' and <= '๿'))
        {
            return false;
        }

        return line.Bounds.Width > 0
            && line.Bounds.Height >= MinimumHeightPixels;
    }

    private static RelativeRect ToFraction(TextRect bounds, int frameWidth, int frameHeight)
    {
        double x = Math.Clamp(bounds.X / frameWidth, 0, 1);
        double y = Math.Clamp(bounds.Y / frameHeight, 0, 1);

        // Deliberately not RelativeRect.Clamped(): its minimum size of 5% exists
        // to keep a dragged panel grabbable, and applying it here would inflate a
        // twenty-pixel HUD label into a block a twentieth of the screen tall.
        return new RelativeRect(
            x,
            y,
            Math.Clamp(bounds.Width / frameWidth, 0, 1 - x),
            Math.Clamp(bounds.Height / frameHeight, 0, 1 - y));
    }
}
