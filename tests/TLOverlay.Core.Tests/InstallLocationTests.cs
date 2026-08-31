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
}
