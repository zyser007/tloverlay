using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TLOverlay.Core.Translation;

/// <summary>
/// The instructions and the output cleanup shared by every chat-style
/// translator, local or hosted.
///
/// One copy on purpose. The local model and a hosted one answer the same
/// request over the same API shape, and a prompt that drifted between them
/// would mean a player who switches backends gets a different voice for the
/// same game - which is exactly the thing the glossary and the cache exist to
/// prevent.
/// </summary>
public static class ChatTranslationPrompt
{
    public const string System =
        "You are a translation engine embedded in a video game overlay. " +
        "Translate the user's English text into natural, conversational Thai. " +
        "Rules: output ONLY the Thai translation; no explanations, no romanisation, " +
        "no quotation marks around the result, no English echo. " +
        "Keep the register of game dialogue rather than formal written Thai. " +
        "Copy any [[0]], [[1]] placeholder tokens through unchanged and in place.";

    // Two shots are enough to lock the output shape; more just costs prompt time
    // on every uncached line - and with a hosted model, money.
    public static readonly (string User, string Assistant)[] FewShot =
    [
        ("You have no idea what you're dealing with.", "นายไม่รู้หรอกว่ากำลังยุ่งกับอะไรอยู่"),
        ("[[0]] restores 40 HP to a single ally.", "[[0]] ฟื้นฟู HP 40 หน่วยให้พวกพ้องหนึ่งคน"),
    ];

    /// <summary>Builds the message list an OpenAI-shaped chat endpoint expects.</summary>
    public static List<object> BuildMessages(string sourceText)
    {
        var messages = new List<object>(2 + (FewShot.Length * 2))
        {
            new { role = "system", content = System },
        };

        foreach (var (user, assistant) in FewShot)
        {
            messages.Add(new { role = "user", content = user });
            messages.Add(new { role = "assistant", content = assistant });
        }

        messages.Add(new { role = "user", content = sourceText });

        return messages;
    }


    /// <summary>
    /// The instructions for translating many lines at once.
    ///
    /// A separate prompt rather than a reuse of <see cref="System"/>, which says
    /// "output ONLY the Thai translation, no English echo" - the exact opposite
    /// of what a numbered list needs. Models follow the examples over the rules,
    /// so the batch path needs its own example too.
    /// </summary>
    public const string BatchSystem =
        "You are a translation engine embedded in a video game overlay. " +
        "The user sends numbered English lines taken from different parts of one game screen. " +
        "Translate each line into natural, conversational Thai. " +
        "Rules: answer with the same numbers, one line each, in the form \"1. \u0e04\u0e33\u0e41\u0e1b\u0e25\". " +
        "Every input number must appear exactly once. " +
        "Translate each line on its own - they are unrelated to each other. " +
        "No explanations, no romanisation, no English echo, no extra lines. " +
        "Copy any [[0]], [[1]] placeholder tokens through unchanged and in place.";

    /// <summary>
    /// One example is enough to lock the shape, and every extra one is paid for
    /// on every sweep - with a hosted model, in money.
    /// </summary>
    public static readonly (string User, string Assistant) BatchExample =
    (
        "1. Start Game\n2. You have no idea what you're dealing with.\n3. Options",
        "1. \u0e40\u0e23\u0e34\u0e48\u0e21\u0e40\u0e01\u0e21\n2. \u0e19\u0e32\u0e22\u0e44\u0e21\u0e48\u0e23\u0e39\u0e49\u0e2b\u0e23\u0e2d\u0e01\u0e27\u0e48\u0e32\u0e01\u0e33\u0e25\u0e31\u0e07\u0e22\u0e38\u0e48\u0e07\u0e01\u0e31\u0e1a\u0e2d\u0e30\u0e44\u0e23\u0e2d\u0e22\u0e39\u0e48\n3. \u0e15\u0e31\u0e49\u0e07\u0e04\u0e48\u0e32"
    );

    /// <summary>Builds the message list for a batch of lines.</summary>
    public static List<object> BuildBatchMessages(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var numbered = new StringBuilder();

        for (int i = 0; i < lines.Count; i++)
        {
            numbered.Append(i + 1).Append(". ").AppendLine(lines[i]);
        }

        return
        [
            new { role = "system", content = BatchSystem },
            new { role = "user", content = BatchExample.User },
            new { role = "assistant", content = BatchExample.Assistant },
            new { role = "user", content = numbered.ToString().TrimEnd() },
        ];
    }

    /// <summary>
    /// How many output tokens a batch of this size needs.
    ///
    /// The single-line path's 512 is nowhere near enough for forty Thai lines,
    /// and a truncated response loses every line after the cut.
    /// </summary>
    public static int MaxTokensFor(int lineCount) => Math.Clamp(96 * lineCount, 256, 8192);

    /// <summary>
    /// Reads a numbered answer back. Entries the model dropped come back null so
    /// the caller can re-ask for exactly those rather than the whole sweep.
    ///
    /// Indexed by the number the model wrote rather than by position: models
    /// reorder, and a list rebuilt by position would put every translation after
    /// a dropped line one row out - Thai painted over the wrong English.
    /// </summary>
    public static IReadOnlyList<string?> ParseNumberedOutput(string? raw, int expected)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expected);

        var results = new string?[expected];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return results;
        }

        foreach (string line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Match match = NumberedLine.Match(line);

            if (!match.Success)
            {
                // A preamble - "Here are the translations:" - or a blank.
                continue;
            }

            if (!int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                || number < 1
                || number > expected)
            {
                continue;
            }

            // First writer wins: a model that repeats a number is more likely to
            // be echoing than correcting.
            results[number - 1] ??= CleanModelOutput(match.Groups[2].Value);
        }

        return results;
    }

    /// <summary>
    /// A number, a separator, then the text.
    ///
    /// The separator is required, and that is the whole point of this pattern: a
    /// Thai translation can itself begin with a digit - "3 \u0e19\u0e32\u0e17\u0e35" - and a looser
    /// rule would slice the number off the front of somebody's translation.
    /// </summary>
    private static readonly Regex NumberedLine = new(
        @"^\s*(\d{1,3})\s*[\.\)\:\-]\s*(.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Instruct models leak scaffolding even with a strict system prompt: wrapping
    /// quotes, a "Thai:" label, or a trailing note. Strip the common shapes rather
    /// than showing them over the game.
    /// </summary>
    public static string CleanModelOutput(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string text = raw.Trim();

        foreach (string label in new[] { "Thai:", "Translation:", "คำแปล:", "แปล:" })
        {
            if (text.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                text = text[label.Length..].TrimStart();
            }
        }

        text = TrimMatchingQuotes(text);

        // Reasoning-flavoured models sometimes append a note after a blank line.
        int blankLine = text.IndexOf("\n\n", StringComparison.Ordinal);
        if (blankLine > 0)
        {
            text = text[..blankLine];
        }

        return text.Trim();
    }

    private static string TrimMatchingQuotes(string text)
    {
        if (text.Length < 2)
        {
            return text;
        }

        char first = text[0];
        char last = text[^1];

        bool matched =
            (first == '"' && last == '"')
            || (first == '\'' && last == '\'')
            || (first == '“' && last == '”');

        return matched ? text[1..^1].Trim() : text;
    }
}
