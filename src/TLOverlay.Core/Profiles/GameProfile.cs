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
/// both the capture region and the translation panel stay where they were put.
/// </summary>
public sealed record RelativeRect(double X, double Y, double Width, double Height)
{
    public bool IsValid =>
        Width > 0 && Height > 0
        && X >= 0 && Y >= 0
        && X + Width <= 1.0001
        && Y + Height <= 1.0001;

    /// <summary>Projects onto a window of the given pixel size.</summary>
    public (int X, int Y, int Width, int Height) ToPixels(int windowWidth, int windowHeight)
    {
        int x = (int)Math.Round(X * windowWidth);
        int y = (int)Math.Round(Y * windowHeight);
        int w = (int)Math.Round(Width * windowWidth);
        int h = (int)Math.Round(Height * windowHeight);

        // Clamp so a rectangle saved at a slightly different aspect ratio can
        // never produce an out-of-bounds crop.
        x = Math.Clamp(x, 0, Math.Max(0, windowWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, windowHeight - 1));
        w = Math.Clamp(w, 1, windowWidth - x);
        h = Math.Clamp(h, 1, windowHeight - y);

        return (x, y, w, h);
    }

    /// <summary>
    /// Keeps a dragged or resized rectangle inside the window and above a
    /// usable minimum, so a panel can never be shoved off-screen or collapsed to
    /// nothing and become impossible to grab again.
    /// </summary>
    public RelativeRect Clamped(double minimumSize = 0.05)
    {
        double width = Math.Clamp(Width, minimumSize, 1);
        double height = Math.Clamp(Height, minimumSize, 1);

        return new RelativeRect(
            Math.Clamp(X, 0, 1 - width),
            Math.Clamp(Y, 0, 1 - height),
            width,
            height);
    }
}

/// <summary>The area of the game window the pipeline reads text from.</summary>
public sealed record CaptureRegion(string Name, double X, double Y, double Width, double Height)
{
    /// <summary>There is exactly one region per profile, and this is its name.</summary>
    public const string DefaultName = "Dialogue";

    public static CaptureRegion BottomDialogue { get; } = new(DefaultName, 0.15, 0.72, 0.70, 0.22);

    public RelativeRect Bounds => new(X, Y, Width, Height);

    public bool IsValid => Bounds.IsValid;

    public static CaptureRegion FromBounds(RelativeRect bounds, string name = DefaultName) =>
        new(name, bounds.X, bounds.Y, bounds.Width, bounds.Height);

    /// <summary>Projects onto a game window of the given pixel size.</summary>
    public (int X, int Y, int Width, int Height) ToPixels(int windowWidth, int windowHeight) =>
        Bounds.ToPixels(windowWidth, windowHeight);
}

public sealed class GameProfile
{
    public string Name { get; set; } = "Default";

    /// <summary>Process name without extension, used to auto-select this profile.</summary>
    public string? ProcessName { get; set; }

    /// <summary>Substring match against the window title, for launchers that share an executable.</summary>
    public string? WindowTitleContains { get; set; }

    /// <summary>
    /// Still a list, because that is what the pipeline and the saved profiles
    /// already speak, but the UI edits exactly one. Several regions turned out to
    /// be more configuration than the job needs.
    ///
    /// Null-tolerant on the way in: System.Text.Json calls the setter with null
    /// for "regions": null, which replaces the default and turns the next read
    /// of <see cref="Region"/> into a NullReferenceException nowhere near the
    /// profile file that caused it.
    /// </summary>
    public List<CaptureRegion> Regions
    {
        get => _regions;
        set => _regions = value ?? [];
    }

    /// <summary>
    /// Where the player dragged the translation panel, if they did.
    ///
    /// Null means "lay it out from <see cref="DisplayMode"/>". Once set, the
    /// manual placement wins: a panel that snapped back on the next line would
    /// make dragging feel broken.
    /// </summary>
    public RelativeRect? PanelBounds { get; set; }

    public OverlayDisplayMode DisplayMode { get; set; } = OverlayDisplayMode.Subtitle;

    /// <summary>Smallest translation text the player may choose, in points.</summary>
    public const double MinimumFontSize = 12;

    /// <summary>
    /// Largest translation text the player may choose.
    ///
    /// Not a technical limit: past roughly this size a full-screen label covers
    /// the lines above and below the one it is translating, and the screen stops
    /// being readable at all.
    /// </summary>
    public const double MaximumFontSize = 48;

    /// <summary>
    /// Size of the translation text.
    ///
    /// Used outright by the subtitle panel, and as the scale for full-screen
    /// labels, whose size comes from each line's own OCR box - see
    /// <see cref="Pipeline.ScreenLabelMetrics"/>.
    /// </summary>
    public double FontSize { get; set; } = Pipeline.ScreenLabelMetrics.NeutralFontSize;

    /// <summary>Background opacity of the translation panel, 0..1.</summary>
    public double BackgroundOpacity { get; set; } = 0.82;

    /// <summary>
    /// Opacity of the boxes drawn over the original text in full-screen mode.
    ///
    /// Separate from <see cref="BackgroundOpacity"/> and opaque by default,
    /// because the two want opposite things: the subtitle panel is deliberately
    /// see-through so the art shows behind it, while a full-screen label exists
    /// to replace the English underneath it. Sharing one number would ship every
    /// existing profile a half-transparent full-screen mode, with no way to tell
    /// a deliberate 0.82 from an inherited one.
    /// </summary>
    public double ScreenOverlayOpacity { get; set; } = 1.0;

    /// <summary>Milliseconds a region must hold still before we OCR it.</summary>
    public int SettleMilliseconds { get; set; } = 150;

    /// <summary>How often we pull a frame for change detection.</summary>
    public int PollIntervalMilliseconds { get; set; } = 120;

    /// <summary>Null-tolerant for the same reason as <see cref="Regions"/>.</summary>
    public List<GlossaryTerm> Glossary
    {
        get => _glossary;
        set => _glossary = value ?? [];
    }

    private List<CaptureRegion> _regions = [CaptureRegion.BottomDialogue];
    private List<GlossaryTerm> _glossary = [];

    /// <summary>The one capture region, or null when none has been drawn yet.</summary>
    public CaptureRegion? Region => Regions.FirstOrDefault(static r => r.IsValid);

    /// <summary>Replaces the capture region, keeping the list to a single entry.</summary>
    public void SetRegion(CaptureRegion? region)
    {
        Regions = region is null ? [] : [region with { Name = CaptureRegion.DefaultName }];
    }

    public static GameProfile CreateDefault(string name) => new() { Name = name };
}

/// <summary>Serialisable form of a glossary entry.</summary>
public sealed class GlossaryTerm
{
    public string Source { get; set; } = string.Empty;

    /// <summary>Null or empty means "leave the source text untranslated".</summary>
    public string? Target { get; set; }
}
