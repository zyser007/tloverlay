namespace TLOverlay.Core.Pipeline;

public enum ChangeState
{
    /// <summary>Region matches what we last acted on. Do nothing.</summary>
    Unchanged,

    /// <summary>Region is mid-transition. Wait for it to settle.</summary>
    Changing,

    /// <summary>Region changed and has now held still. Run OCR.</summary>
    Settled,
}

/// <summary>
/// Per-region gate in front of OCR.
///
/// Two jobs, and the second one matters more than it looks: many games reveal
/// dialogue one character at a time, so a detector that fired on first
/// difference would translate a dozen partial sentences per line. Waiting for
/// the region to hold still for <see cref="SettleTime"/> means we translate the
/// finished sentence once.
/// </summary>
public sealed class ChangeDetector
{
    private byte[]? _committed;
    private byte[]? _pending;
    private DateTimeOffset _pendingSince;

    public ChangeDetector(double threshold = 2.5, TimeSpan? settleTime = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(threshold);
        Threshold = threshold;
        SettleTime = settleTime ?? TimeSpan.FromMilliseconds(150);
    }

    /// <summary>
    /// Mean absolute luma difference below which two signatures count as the
    /// same picture. Low enough to catch a single new word, high enough to
    /// ignore video-compression-grade noise and animated backgrounds.
    /// </summary>
    public double Threshold { get; }

    /// <summary>How long the region must hold still before we call it settled.</summary>
    public TimeSpan SettleTime { get; }

    /// <summary>
    /// Feeds one observation. <paramref name="now"/> is passed in rather than
    /// read from the clock so the settle behaviour is testable.
    /// </summary>
    public ChangeState Observe(byte[] signature, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(signature);

        if (_committed is not null && FrameSignature.Difference(signature, _committed) < Threshold)
        {
            // Back to (or still at) the picture we already handled.
            _pending = null;
            return ChangeState.Unchanged;
        }

        if (_pending is null || FrameSignature.Difference(signature, _pending) >= Threshold)
        {
            // Still moving - restart the settle timer against the newest picture.
            _pending = signature;
            _pendingSince = now;
            return ChangeState.Changing;
        }

        if (now - _pendingSince < SettleTime)
        {
            return ChangeState.Changing;
        }

        _committed = signature;
        _pending = null;
        return ChangeState.Settled;
    }

    /// <summary>
    /// Forgets what we last acted on, so the next observation is treated as new.
    /// Called when the user moves a region or switches profile.
    /// </summary>
    public void Reset()
    {
        _committed = null;
        _pending = null;
    }
}
