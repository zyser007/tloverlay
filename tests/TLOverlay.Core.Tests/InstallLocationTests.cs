using TLOverlay.Core.Setup;
using Xunit;

namespace TLOverlay.Core.Tests;

public class InstallLocationTests
{
    private const string Temp = @"C:\Users\someone\AppData\Local\Temp";

    [Theory]
    // WinRAR's "open without extracting" lands here, and deletes it afterwards.
    [InlineData(@"C:\Users\someone\AppData\Local\Temp\Rar$EXa19868.20381.rartemp", true)]
    // 7-Zip does the same thing under a different prefix.
    [InlineData(@"C:\Users\someone\AppData\Local\Temp\7zO4C2B1F3A", true)]
    // Explorer's built-in zip viewer.
    [InlineData(@"C:\Users\someone\AppData\Local\Temp\Temp1_TLOverlay-win-x64.zip", true)]
    [InlineData(@"C:\Users\someone\AppData\Local\Temp", true)]
    // Properly extracted installs must not be flagged.
    [InlineData(@"C:\Games\TLOverlay", false)]
    [InlineData(@"C:\Users\someone\Desktop\TLOverlay", false)]
    // A directory that merely starts with the same characters is not inside it.
    [InlineData(@"C:\Users\someone\AppData\Local\TempStuff", false)]
    public void DetectsWhenTheAppIsRunningFromATemporaryCopy(string directory, bool expected)
    {
        Assert.Equal(expected, InstallLocation.IsUnderTemporaryDirectory(directory, Temp));
    }

    [Fact]
    public void TrailingSeparatorsDoNotChangeTheAnswer()
    {
        Assert.True(InstallLocation.IsUnderTemporaryDirectory(
            @"C:\Users\someone\AppData\Local\Temp\Rar$EXa1\",
            @"C:\Users\someone\AppData\Local\Temp\"));
    }

    [Fact]
    public void MissingPathsAreNotTemporary()
    {
        Assert.False(InstallLocation.IsUnderTemporaryDirectory("", Temp));
        Assert.False(InstallLocation.IsUnderTemporaryDirectory(@"C:\Games", ""));
    }

    [Fact]
    public void WritabilityIsProvedByWritingRatherThanAssumed()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"tloverlay-w-{Guid.NewGuid():N}");

        try
        {
            Assert.True(InstallLocation.IsWritable(directory));

            // The probe file must not be left behind.
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MovingADirectoryCarriesItsWholeTreeAcross()
    {
        string root = Path.Combine(Path.GetTempPath(), $"tloverlay-mv-{Guid.NewGuid():N}");
        string source = Path.Combine(root, "from");
        string destination = Path.Combine(root, "to");

        try
        {
            Directory.CreateDirectory(Path.Combine(source, "nested"));
            await File.WriteAllTextAsync(Path.Combine(source, "server.exe"), "binary");
            await File.WriteAllTextAsync(Path.Combine(source, "nested", "model.gguf"), "weights");

            var progress = new List<double>();
            await InstallLocation.MoveDirectoryAsync(source, destination, new Progress<double>(progress.Add));

            Assert.False(Directory.Exists(source));
            Assert.Equal("binary", await File.ReadAllTextAsync(Path.Combine(destination, "server.exe")));
            Assert.Equal("weights", await File.ReadAllTextAsync(Path.Combine(destination, "nested", "model.gguf")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MovingToTheSamePlaceIsANoOp()
    {
        string source = Path.Combine(Path.GetTempPath(), $"tloverlay-same-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "keep me");

            await InstallLocation.MoveDirectoryAsync(source, source + Path.DirectorySeparatorChar);

            // The guard matters: without it the copy would delete what it just read.
            Assert.True(File.Exists(Path.Combine(source, "a.txt")));
        }
        finally
        {
            if (Directory.Exists(source))
            {
                Directory.Delete(source, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MovingAMissingDirectoryDoesNothing()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"tloverlay-gone-{Guid.NewGuid():N}");

        await InstallLocation.MoveDirectoryAsync(missing, missing + "-target");

        Assert.False(Directory.Exists(missing + "-target"));
    }

    [Fact]
    public void FreeSpaceIsReportedForARealDirectory()
    {
        long? free = InstallLocation.FreeSpaceBytes(Path.GetTempPath());

        Assert.NotNull(free);
        Assert.True(free > 0);
    }

    [Fact]
    public void FreeSpaceIsUnknownForNonsense()
    {
        Assert.Null(InstallLocation.FreeSpaceBytes(""));
    }
}
