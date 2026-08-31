using TLOverlay.Core.Pipeline;
using Xunit;

namespace TLOverlay.Core.Tests;

public class PollScheduleTests
{
    [Fact]
    public void ABusyScreenIsPolledAtTheBaseRate()
    {
        var schedule = new PollSchedule(120);

        for (int poll = 0; poll < 50; poll++)
        {
            Assert.Equal(120, schedule.Next(sawChange: true));
        }
    }

    [Fact]
    public void AQuietScreenBacksOffToTheCeiling()
    {
        var schedule = new PollSchedule(120, maxIntervalMilliseconds: 500, quietPollsBeforeBackoff: 4);

        // Four quiet polls at a time, each doubling until the ceiling.
        Quiet(schedule, 4);
        Assert.Equal(240, schedule.CurrentIntervalMilliseconds);

        Quiet(schedule, 4);
        Assert.Equal(480, schedule.CurrentIntervalMilliseconds);

        Quiet(schedule, 40);
        Assert.Equal(500, schedule.CurrentIntervalMilliseconds);
    }

    [Fact]
    public void OneChangeIsEnoughToGoBackToFullRate()
    {
        var schedule = new PollSchedule(120, maxIntervalMilliseconds: 500, quietPollsBeforeBackoff: 2);

        Quiet(schedule, 20);
        Assert.Equal(500, schedule.CurrentIntervalMilliseconds);

        // Being late to the first line of a conversation is the one thing the
        // player would notice, so the recovery is immediate rather than gradual.
        Assert.Equal(120, schedule.Next(sawChange: true));
    }

    [Fact]
    public void TheCeilingIsNeverBelowTheBaseRate()
    {
        var schedule = new PollSchedule(800, maxIntervalMilliseconds: 500);

        Quiet(schedule, 100);

        Assert.Equal(800, schedule.CurrentIntervalMilliseconds);
    }

    [Fact]
    public void ResetGoesBackToFullRate()
    {
        var schedule = new PollSchedule(120, quietPollsBeforeBackoff: 1);

        Quiet(schedule, 10);
        Assert.True(schedule.CurrentIntervalMilliseconds > 120);

        schedule.Reset();

        Assert.Equal(120, schedule.CurrentIntervalMilliseconds);
    }

    private static void Quiet(PollSchedule schedule, int polls)
    {
        for (int poll = 0; poll < polls; poll++)
        {
            schedule.Next(sawChange: false);
        }
    }
}
