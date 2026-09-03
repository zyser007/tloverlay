namespace TLOverlay.Core.Translation;

/// <summary>Which engine does the translating.</summary>
public enum TranslationBackend
{
    /// <summary>
    /// A model running on this machine. Private and free, and the only option
    /// that works with no connection - but it needs a few gigabytes of memory
    /// that a low-end PC does not have to spare.
    /// </summary>
    Local,

    /// <summary>
    /// Google Translate. Fast on any machine because none of the work happens
    /// here, at the cost of sending the game's text to Google.
    /// </summary>
    Google,

    /// <summary>
    /// A hosted OpenAI-compatible chat model. The best Thai of the three for
    /// game dialogue, billed per line, and the text leaves the machine.
    /// </summary>
    OpenAi,
}
