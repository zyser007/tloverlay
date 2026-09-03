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
