namespace TLOverlay.Core.Pipeline;

/// <summary>
/// How large the Thai on a full-screen label is allowed to be.
///
/// Kept out of the overlay so the policy can be tested without a window. It is
/// only arithmetic about boxes, but every mistake in it shows on screen as text
/// that is either too small to read or spilling over the line below - and the
/// overlay itself cannot be exercised on CI.
/// </summary>
public static class ScreenLabelMetrics
{
    /// <summary>
    /// The font size that means "leave the automatic fit alone".
    ///
    /// A full-screen label's size is derived from its own OCR box rather than
    /// set outright, because a menu label and a line of dialogue want very
    /// different sizes on the same screen. The player's setting scales that fit
    /// instead of replacing it, and this - the default in GameProfile - is the
    /// value it scales from.
    /// </summary>
    public const double NeutralFontSize = 22;

    /// <summary>Below this nothing is worth drawing, so the text is trimmed instead.</summary>
    public const double MinimumFontSize = 9;

    /// <summary>
    /// How much wider than its English a label may grow, at the default size,
    /// before the text shrinks instead.
    /// </summary>
    public const double MaxGrowth = 1.6;

    /// <summary>Room the label keeps either side of the text, in pixels.</summary>
    public const double SideMargin = 6;

    /// <summary>
    /// A fraction of the box rather than all of it: an OCR rectangle includes
    /// the line's leading, so filling it exactly gives text that touches both
    /// edges.
    /// </summary>
    private const double HeightToFontSize = 0.68;

    /// <summary>
    /// The size to try first for a line in a box this tall, at the player's
    /// chosen setting.
    /// </summary>
    public static double StartingFontSize(double boxHeight, double profileFontSize)
    {
        double scale = profileFontSize > 0 ? profileFontSize / NeutralFontSize : 1;

        return Math.Max(MinimumFontSize, boxHeight * HeightToFontSize * scale);
    }

    /// <summary>
    /// How much wider than its English a label may grow at this setting.
    ///
    /// Scaled with the setting rather than fixed. Shrinking to fit is
    /// proportional, so a line long enough to need it lands on the same size
    /// whatever size it started at - the box has to be allowed to grow with the
    /// text, or asking for larger text does nothing at all to exactly the lines
    /// that were hardest to read. Turning the setting down never tightens the
    /// box below the default: smaller text in the same box is the point, not a
    /// narrower box.
    /// </summary>
    public static double GrowthFor(double profileFontSize) =>
        MaxGrowth * Math.Max(1, profileFontSize > 0 ? profileFontSize / NeutralFontSize : 1);

    /// <summary>
    /// How wide the text may be before it has to shrink.
    ///
    /// Measured against the grown box, not the English one. Thai is almost
    /// always longer than the English it replaces, so fitting it to the original
    /// width means shrinking nearly every line - which is what made full-screen
    /// labels hard to read.
    /// </summary>
    public static double WidthBudget(double boxWidth, double profileFontSize) =>
        Math.Max(1, (boxWidth * GrowthFor(profileFontSize)) - SideMargin);

    /// <summary>
    /// The size at which text needing <paramref name="neededWidth"/> fits
    /// <paramref name="budget"/>, or the size unchanged when it already does.
    /// </summary>
    public static double ShrinkToFit(double size, double neededWidth, double budget) =>
        neededWidth <= budget || neededWidth <= 0
            ? size
            : Math.Max(MinimumFontSize, size * budget / neededWidth);
}
