using TLOverlay.Core.Profiles;

namespace TLOverlay.Core.Pipeline;

/// <summary>How much of the screen gets translated, and when.</summary>
public enum TranslationMode
{
    /// <summary>The capture region, translated as it settles. The original mode.</summary>
    Automatic,

    /// <summary>The capture region, only when the player asks.</summary>
    OnDemandRegion,

    /// <summary>
    /// Every line of text anywhere on the game window, only when the player asks.
    ///
    /// On demand is not a preference here, it is the only way this can work: a
    /// whole game screen always has something moving on it - a clock, a health
    /// bar, an idle animation - so change detection over the whole window would
    /// fire continuously and translate everything several times a second.
    /// </summary>
    OnDemandFullScreen,
}

/// <summary>
/// One recognised line and its Thai, positioned as a fraction of the game
/// window.
///
/// Fractions rather than pixels because that is what the overlay already speaks:
/// its window matches the game's client area, so a fraction needs no DPI
/// conversion, and it stays correct when the player drags the game to a monitor
/// with different scaling.
/// </summary>
public sealed record ScreenLine(string SourceText, string TranslatedText, RelativeRect Bounds);

/// <summary>Everything one full-screen pass found and translated.</summary>
public sealed record ScreenTranslation(IReadOnlyList<ScreenLine> Lines)
{
    public static readonly ScreenTranslation Empty = new(Array.Empty<ScreenLine>());
}
