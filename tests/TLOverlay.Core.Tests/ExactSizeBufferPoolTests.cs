using TLOverlay.Core.Capture;
using Xunit;

namespace TLOverlay.Core.Tests;

public class ExactSizeBufferPoolTests
{
    [Fact]
    public void ARentedBufferIsExactlyTheSizeAskedFor()
    {
        var pool = new ExactSizeBufferPool();

        // The whole reason this exists rather than ArrayPool: DataReader fills
        // every byte of the array it is handed.
        Assert.Equal(1000, pool.Rent(1000).Length);
    }

    [Fact]
    public void AReturnedBufferComesBackOutAgain()
    {
        var pool = new ExactSizeBufferPool();

        byte[] first = pool.Rent(512);
        pool.Return(first);

        Assert.Same(first, pool.Rent(512));
    }

    [Fact]
    public void ABufferStillOutOnLoanIsNotHandedToSomebodyElse()
    {
        var pool = new ExactSizeBufferPool();

        byte[] first = pool.Rent(512);
        byte[] second = pool.Rent(512);

        Assert.NotSame(first, second);
    }

    [Fact]
    public void ChangingSizeDropsTheOldBuffers()
    {
        var pool = new ExactSizeBufferPool();

        byte[] small = pool.Rent(256);
        pool.Return(small);

        byte[] large = pool.Rent(4096);

        Assert.Equal(4096, large.Length);

        pool.Return(large);
        Assert.Same(large, pool.Rent(4096));

        // The 256-byte buffer was let go when the size changed, rather than kept
        // for a frame size nobody is capturing any more.
        Assert.NotSame(small, pool.Rent(256));
    }

    [Fact]
    public void AWrongSizedBufferIsNotAccepted()
    {
        var pool = new ExactSizeBufferPool();

        pool.Rent(512);
        pool.Return(new byte[64]);

        Assert.Equal(512, pool.Rent(512).Length);
    }
}
