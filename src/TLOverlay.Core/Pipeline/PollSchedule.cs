namespace TLOverlay.Core.Pipeline;

/// <summary>
/// How long to wait before pulling the next frame.
///
/// Polling eight times a second is the right rate while dialogue is moving and
/// pure waste the rest of the time - menus, cutscenes, a paused game, or a
/// session left running while the player reads. Each poll costs a full
/// GPU-to-CPU frame copy, so on a low-end machine that waste is exactly the
/// budget the game needs.
///
/// The rule is deliberately asymmetric: back off slowly after the screen has
/// been quiet for a while, but return to full rate on the very first change.
/// Being late to the first line of a conversation is the one thing the player
/// would notice.
/// </summary>
public sealed class PollSchedule
{
    private readonly int _baseInterval;
    private readonly int _maxInterval;
    private readonly int _quietPollsBeforeBackoff;

    private int _quietPolls;
    private int _interval;

    public PollSchedule(int baseIntervalMilliseconds, int maxIntervalMilliseconds = 500, int quietPollsBeforeBackoff = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baseIntervalMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quietPollsBeforeBackoff);

        _baseInterval = baseIntervalMilliseconds;
        _maxInterval = Math.Max(baseIntervalMilliseconds, maxIntervalMilliseconds);
        _quietPollsBeforeBackoff = quietPollsBeforeBackoff;
        _interval = baseIntervalMilliseconds;
    }

    /// <summary>The interval the last call to <see cref="Next"/> settled on.</summary>
    public int CurrentIntervalMilliseconds => _interval;

    /// <summary>
    /// Reports what the poll found and returns how long to wait before the next
    /// one.
    /// </summary>
    public int Next(bool sawChange)
    {
        if (sawChange)
        {
            _quietPolls = 0;
            _interval = _baseInterval;
            return _interval;
        }

        _quietPolls++;

        if (_quietPolls >= _quietPollsBeforeBackoff)
        {
            _quietPolls = 0;
            _interval = Math.Min(_maxInterval, _interval * 2);
        }

        return _interval;
    }

    /// <summary>Back to full rate, for a change the poll itself cannot see.</summary>
    public void Reset()
    {
        _quietPolls = 0;
        _interval = _baseInterval;
    }
}
