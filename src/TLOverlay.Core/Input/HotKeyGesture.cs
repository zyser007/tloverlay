using System.Globalization;

namespace TLOverlay.Core.Input;

/// <summary>
/// A hotkey written the way a player reads it: "Ctrl+Alt+T".
///
/// Deliberately a string of modifiers plus a key *name* rather than Win32 codes,
/// because this is what goes into settings.json. A saved binding has to survive
/// being read back by a later version, and a line someone can read - and fix by
/// hand when a key turns out to be taken - is worth more here than a pair of
/// integers.
///
/// Lives in Core, away from WPF, so the parsing has tests. Turning the key name
/// into a virtual key is the app's job, and it is one lookup.
/// </summary>
public readonly record struct HotKeyGesture(bool Control, bool Alt, bool Shift, bool Windows, string KeyName)
{
    /// <summary>
    /// Whether this can be registered as a global hotkey at all.
    ///
    /// At least one modifier is required, and that is not a formality: a global
    /// hotkey with no modifier swallows that key everywhere, so binding "T" alone
    /// would take the letter away from the game the overlay is sitting on.
    /// </summary>
    public bool IsValid =>
        (Control || Alt || Shift || Windows) && !string.IsNullOrWhiteSpace(KeyName);

    /// <summary>
    /// How the key reads on its own. The Key enum spells the number row D1..D0,
    /// which no player would recognise as the 1 key.
    /// </summary>
    public string DisplayKey => KeyName.Length == 2
        && KeyName[0] == 'D'
        && char.IsAsciiDigit(KeyName[1])
            ? KeyName[1..]
            : KeyName;

    public override string ToString()
    {
        var parts = new List<string>(5);

        // Fixed order, so the same binding is always written the same way and
        // two gestures can be compared as strings.
        if (Control)
        {
            parts.Add("Ctrl");
        }

        if (Alt)
        {
            parts.Add("Alt");
        }

        if (Shift)
        {
            parts.Add("Shift");
        }

        if (Windows)
        {
            parts.Add("Win");
        }

        parts.Add(DisplayKey);

        return string.Join('+', parts);
    }

    /// <summary>
    /// Reads a gesture back. Accepts what this writes, and the spellings a person
    /// is likely to type into settings.json by hand: "control", "ctl", "windows",
    /// spaces around the pluses, and a bare digit for the number row.
    /// </summary>
    public static bool TryParse(string? text, out HotKeyGesture gesture)
    {
        gesture = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        bool control = false;
        bool alt = false;
        bool shift = false;
        bool windows = false;
        string? key = null;

        foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLower(CultureInfo.InvariantCulture))
            {
                case "ctrl" or "control" or "ctl":
                    control = true;
                    break;

                case "alt":
                    alt = true;
                    break;

                case "shift":
                    shift = true;
                    break;

                case "win" or "windows" or "meta":
                    windows = true;
                    break;

                default:
                    // The last non-modifier wins rather than the first: "Ctrl+A+B"
                    // is malformed either way, and taking the last one matches how
                    // the capture box builds a gesture.
                    key = Normalize(raw);
                    break;
            }
        }

        if (key is null)
        {
            return false;
        }

        gesture = new HotKeyGesture(control, alt, shift, windows, key);
        return gesture.IsValid;
    }

    /// <summary>Turns what was typed into the name the Key enum uses.</summary>
    private static string Normalize(string key)
    {
        if (key.Length == 1 && char.IsAsciiDigit(key[0]))
        {
            return "D" + key;
        }

        return key.Length == 1
            ? key.ToUpper(CultureInfo.InvariantCulture)
            : string.Concat(key[..1].ToUpper(CultureInfo.InvariantCulture), key[1..]);
    }
}
