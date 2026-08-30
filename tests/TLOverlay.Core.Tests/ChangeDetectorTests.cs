using TLOverlay.Core.Pipeline;
using Xunit;

namespace TLOverlay.Core.Tests;

public class ChangeDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static byte[] Uniform(byte value)
    {
        var signature = new byte[FrameSignature.Length];
        Array.Fill(signature, value);
        return signature;
    }

    [Fact]
    public void FirstObservationIsChangingNotSettled()
    {
        var detector = new ChangeDetector(settleTime: TimeSpan.FromMilliseconds(150));

        Assert.Equal(ChangeState.Changing, detector.Observe(Uniform(10), T0));
    }

    [Fact]
    public void SettlesOnceRegionHoldsStillForSettleTime()
    {
        var detector = new ChangeDetector(settleTime: TimeSpan.FromMilliseconds(150));

        Assert.Equal(ChangeState.Changing, detector.Observe(Uniform(10), T0));
        Assert.Equal(ChangeState.Changing, detector.Observe(Uniform(10), T0.AddMilliseconds(100)));
        Assert.Equal(ChangeState.Settled, detector.Observe(Uniform(10), T0.AddMilliseconds(160)));
    }

    [Fact]
    public void IdenticalFramesAfterSettlingReportUnchanged()
    {
        var detector = new ChangeDetector(settleTime: TimeSpan.FromMilliseconds(150));

        detector.Observe(Uniform(10), T0);
        detector.Observe(Uniform(10), T0.AddMilliseconds(200));

        Assert.Equal(ChangeState.Unchanged, detector.Observe(Uniform(10), T0.AddMilliseconds(400)));
        Assert.Equal(ChangeState.Unchanged, detector.Observe(Uniform(10), T0.AddMilliseconds(600)));
    }

    [Fact]
    public void TextRevealedOneCharacterAtATimeSettlesOnlyOnce()
    {
        // The behaviour that matters most: a game typing out dialogue must not
        // produce a translation per character.
        var detector = new ChangeDetector(settleTime: TimeSpan.FromMilliseconds(150));
        var now = T0;
        int settled = 0;

        for (byte step = 0; step < 20; step++)
        {
            // Each step differs enough to be a real change, 50ms apart - faster
            // than the settle window.
            if (detector.Observe(Uniform((byte)(10 + (step * 5))), now) == ChangeState.Settled)
            {
                settled++;
            }

            now = now.AddMilliseconds(50);
        }

        Assert.Equal(0, settled);

        // Text finishes revealing and the box stops changing.
        var final = Uniform(200);
        Assert.Equal(ChangeState.Changing, detector.Observe(final, now));
        Assert.Equal(ChangeState.Settled, detector.Observe(final, now.AddMilliseconds(200)));
    }

    [Fact]
    public void NoiseBelowThresholdDoesNotRetrigger()
    {
        var detector = new ChangeDetector(threshold: 5.0, settleTime: TimeSpan.FromMilliseconds(100));

        detector.Observe(Uniform(100), T0);
        detector.Observe(Uniform(100), T0.AddMilliseconds(200));

        // Two levels of luma drift is well inside the threshold.
        Assert.Equal(ChangeState.Unchanged, detector.Observe(Uniform(102), T0.AddMilliseconds(400)));
    }

    [Fact]
    public void ResetMakesTheNextFrameNewAgain()
    {
        var detector = new ChangeDetector(settleTime: TimeSpan.FromMilliseconds(100));

        detector.Observe(Uniform(50), T0);
        detector.Observe(Uniform(50), T0.AddMilliseconds(200));
        Assert.Equal(ChangeState.Unchanged, detector.Observe(Uniform(50), T0.AddMilliseconds(300)));

        detector.Reset();

        Assert.Equal(ChangeState.Changing, detector.Observe(Uniform(50), T0.AddMilliseconds(400)));
    }
}
