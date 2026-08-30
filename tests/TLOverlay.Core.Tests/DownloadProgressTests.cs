using TLOverlay.Core.Setup;
using Xunit;

namespace TLOverlay.Core.Tests;

public class DownloadProgressTests
{
    [Fact]
    public void FractionIsTheShareCompleted()
    {
        var progress = new DownloadProgress(512, 2048, 100);

        Assert.Equal(0.25, progress.Fraction);
    }

    [Fact]
    public void FractionIsUnknownWithoutATotal()
    {
        // A server that sends no Content-Length leaves us with no percentage to
        // show, which the UI turns into an indeterminate bar rather than 0%.
        Assert.Null(new DownloadProgress(512, null, 100).Fraction);
    }

    [Fact]
    public void FractionNeverExceedsOne()
    {
        // Some servers under-report Content-Length on a resumed transfer.
        Assert.Equal(1.0, new DownloadProgress(3000, 2048, 100).Fraction);
    }

    [Fact]
    public void RemainingTimeComesFromTheCurrentSpeed()
    {
        var progress = new DownloadProgress(1_000_000, 3_000_000, 1_000_000);

        Assert.Equal(TimeSpan.FromSeconds(2), progress.Remaining);
    }

    [Fact]
    public void RemainingTimeIsUnknownWhenStalled()
    {
        Assert.Null(new DownloadProgress(100, 2048, 0).Remaining);
        Assert.Null(new DownloadProgress(100, null, 500).Remaining);
    }
}
