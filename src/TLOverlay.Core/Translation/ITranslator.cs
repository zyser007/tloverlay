namespace TLOverlay.Core.Translation;

/// <summary>
/// A source-to-target translator.
///
/// Implementations are either local - a model on this machine, reachable only
/// over loopback - or hosted, in which case the text being translated leaves the
/// machine. Which one is running is the player's choice and is stated plainly
/// where they make it, because on a PC that cannot hold a model at all the
/// alternative to sending text away is not translating at anything.
/// </summary>
public interface ITranslator : IAsyncDisposable
{
    /// <summary>
    /// Stable identity of the backend and model. Part of the cache key, so
    /// changing model invalidates previously cached translations rather than
    /// serving them from a different model's output.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Brings the backend up if it isn't already, and reports whether it can
    /// serve requests. Safe to call repeatedly.
    /// </summary>
    Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Translates one chunk of source text. Implementations must honour
    /// cancellation promptly: when on-screen text changes we abandon the
    /// in-flight translation rather than showing a stale line.
    /// </summary>
    Task<string> TranslateAsync(string text, CancellationToken cancellationToken = default);
}
