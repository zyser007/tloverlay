namespace TLOverlay.Core.Profiles;

public enum OverlayDisplayMode
{
    /// <summary>Draw an opaque panel directly over the original text.</summary>
    Inline,

    /// <summary>One subtitle bar near the bottom of the game window.</summary>
    Subtitle,

    /// <summary>A separate window beside the game, never covering it.</summary>
    SidePanel,
}

/// <summary>
/// A rectangle stored as a fraction of the game window (0..1 on both axes).
///
/// Relative rather than absolute on purpose: the player changes resolution,
/// drags the game to a different monitor, or plays windowed at an odd size, and
/// the dialogue box stays in the same relative place every time.
/// </summary>
public sealed record CaptureRegion(string Name, double X, double Y, double Width, double Height)
{
    public static CaptureRegion BottomDialogue { get; } = new("Dialogue", 0.15, 0.72, 0.70, 0.22);

    /// <summary>
    /// Picks a name not already taken. Regions are addressed by name across the
    /// pipeline and the overlay, so two sharing one would have their detectors
    /// and their on-screen panels collide.
    /// </summary>
    public static string UniqueName(IEnumerable<string> existing, string baseName = "Dialogue")
    {
        ArgumentNullException.ThrowIfNull(existing);

        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(baseName))
        {
            return baseName;
        }

        for (int suffix = 2; suffix < 1000; suffix++)
        {
            string candidate = $"{baseName} {suffix}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{baseName} {Guid.NewGuid():N}";
    }

    public bool IsValid =>
        Width > 0 && Height > 0
        && X >= 0 && Y >= 0
        && X + Width <= 1.0001
        && Y + Height <= 1.0001;

    /// <summary>Projects onto a game window of the given pixel size.</summary>
    public (int X, int Y, int Width, int Height) ToPixels(int windowWidth, int windowHeight)
    {
        int x = (int)Math.Round(X * windowWidth);
        int y = (int)Math.Round(Y * windowHeight);
        int w = (int)Math.Round(Width * windowWidth);
        int h = (int)Math.Round(Height * windowHeight);

        // Clamp so a region saved at a slightly different aspect ratio can never
        // produce an out-of-bounds crop.
        x = Math.Clamp(x, 0, Math.Max(0, windowWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, windowHeight - 1));
        w = Math.Clamp(w, 1, windowWidth - x);
        h = Math.Clamp(h, 1, windowHeight - y);

        return (x, y, w, h);
    }
}

public sealed class GameProfile
{
    public string Name { get; set; } = "Default";

    /// <summary>Process name without extension, used to auto-select this profile.</summary>
    public string? ProcessName { get; set; }

    /// <summary>Substring match against the window title, for launchers that share an executable.</summary>
    public string? WindowTitleContains { get; set; }

    public List<CaptureRegion> Regions { get; set; } = [CaptureRegion.BottomDialogue];

    public OverlayDisplayMode DisplayMode { get; set; } = OverlayDisplayMode.Subtitle;

    public double FontSize { get; set; } = 22;

    /// <summary>Background opacity of the translation panel, 0..1.</summary>
    public double BackgroundOpacity { get; set; } = 0.82;

    /// <summary>Milliseconds a region must hold still before we OCR it.</summary>
    public int SettleMilliseconds { get; set; } = 150;

    /// <summary>How often we pull a frame for change detection.</summary>
    public int PollIntervalMilliseconds { get; set; } = 120;

    public List<GlossaryTerm> Glossary { get; set; } = [];

    public static GameProfile CreateDefault(string name) => new() { Name = name };
}

/// <summary>Serialisable form of a glossary entry.</summary>
public sealed class GlossaryTerm
{
    public string Source { get; set; } = string.Empty;

    /// <summary>Null or empty means "leave the source text untranslated".</summary>
    public string? Target { get; set; }
}
