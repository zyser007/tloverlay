using System.Security.Cryptography;
using TLOverlay.Core.Update;
using Xunit;

namespace TLOverlay.Core.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("0.2.0", "0.2.0")]
    [InlineData("V1.10.3", "1.10.3")]
    [InlineData("0.2", "0.2.0")]
    [InlineData("0.2.0.0", "0.2.0")]
    public void TagsAndAssemblyVersionsBothParse(string input, string expected)
    {
        Assert.Equal(Version.Parse(expected), AppVersion.Parse(input));
    }

    [Fact]
    public void BuildMetadataIsNotPartOfTheVersion()
    {
        // A build stamped with a commit must not read as a different version, or
        // every check would look like an update.
        Assert.Equal(new Version(0, 2, 0), AppVersion.Parse("0.2.0+3fd0211"));
        Assert.Equal(new Version(0, 2, 0), AppVersion.Parse("v0.2.0-beta.1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a version")]
    public void SomethingUnreadableIsTreatedAsTheOldestPossibleVersion(string? input)
    {
        Assert.Equal(new Version(0, 0, 0), AppVersion.Parse(input));
    }
}

public class UpdateInstallerTests
{
    [Fact]
    public void AChecksumIsFoundInSha256sumOutput()
    {
        const string File = """
            9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08 *TLOverlay.exe
            5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03 *TLOverlay-0.2.0-win-x64.zip
            """;

        Assert.Equal(
            "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
            UpdateInstaller.FindChecksum(File, "TLOverlay.exe"));
    }

    [Fact]
    public void BothSha256sumStylesAreRead()
    {
        // The star marks binary mode; plain spaces are what some tools write.
        const string Binary = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08 *TLOverlay.exe";
        const string Text = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08  TLOverlay.exe";

        Assert.Equal(
            UpdateInstaller.FindChecksum(Binary, "TLOverlay.exe"),
            UpdateInstaller.FindChecksum(Text, "TLOverlay.exe"));
    }

    [Fact]
    public void AMissingOrMalformedChecksumIsNotGuessedAt()
    {
        Assert.Null(UpdateInstaller.FindChecksum("", "TLOverlay.exe"));
        Assert.Null(UpdateInstaller.FindChecksum("nonsense", "TLOverlay.exe"));
        Assert.Null(UpdateInstaller.FindChecksum("abc *TLOverlay.exe", "TLOverlay.exe"));
        Assert.Null(UpdateInstaller.FindChecksum(
            "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08 *other.exe",
            "TLOverlay.exe"));
    }

    [Fact]
    public async Task TheHashMatchesWhatSha256Produces()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tloverlay-hash-{Guid.NewGuid():N}");

        try
        {
            await File.WriteAllTextAsync(path, "hello");

            string expected = Convert.ToHexString(SHA256.HashData("hello"u8.ToArray())).ToLowerInvariant();

            Assert.Equal(expected, await UpdateInstaller.ComputeSha256Async(path, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ApplyPutsTheNewBuildInPlaceAndKeepsTheOldOne()
    {
        using var folder = new TempFolder();

        string current = folder.Write("TLOverlay.exe", "old build");
        string staged = folder.Write("TLOverlay-0.2.0.exe", "new build");

        UpdateInstaller.Apply(staged, current);

        Assert.Equal("new build", File.ReadAllText(current));

        // Kept, not deleted: it is what the app falls back to if the swap is
        // interrupted, and it cannot be removed while the old one is still
        // running anyway.
        Assert.Equal("old build", File.ReadAllText(current + UpdateInstaller.PreviousSuffix));
    }

    [Fact]
    public void ApplyRefusesWhenThereIsNothingStaged()
    {
        using var folder = new TempFolder();

        string current = folder.Write("TLOverlay.exe", "old build");

        Assert.Throws<UpdateInstallException>(() =>
            UpdateInstaller.Apply(Path.Combine(folder.Path, "missing.exe"), current));

        Assert.Equal("old build", File.ReadAllText(current));
    }

    [Fact]
    public void CleanUpRemovesThePreviousVersionOnceTheNewOneIsInPlace()
    {
        using var folder = new TempFolder();

        string current = folder.Write("TLOverlay.exe", "new build");
        string previous = folder.Write("TLOverlay.exe" + UpdateInstaller.PreviousSuffix, "old build");

        UpdateInstaller.CleanUpAfterUpdate(current);

        Assert.False(File.Exists(previous));
        Assert.Equal("new build", File.ReadAllText(current));
    }

    [Fact]
    public void CleanUpRescuesAnInstallThatDiedBetweenTheTwoRenames()
    {
        using var folder = new TempFolder();

        // The one moment the folder can hold a .old and no program at all.
        string current = Path.Combine(folder.Path, "TLOverlay.exe");
        folder.Write("TLOverlay.exe" + UpdateInstaller.PreviousSuffix, "old build");

        UpdateInstaller.CleanUpAfterUpdate(current);

        Assert.Equal("old build", File.ReadAllText(current));
        Assert.False(File.Exists(current + UpdateInstaller.PreviousSuffix));
    }

    [Fact]
    public void CleanUpDoesNothingWhenThereIsNothingToClean()
    {
        using var folder = new TempFolder();

        string current = folder.Write("TLOverlay.exe", "only build");

        UpdateInstaller.CleanUpAfterUpdate(current);
        UpdateInstaller.CleanUpAfterUpdate(string.Empty);

        Assert.Equal("only build", File.ReadAllText(current));
    }

    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tloverlay-upd-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string name, string contents)
        {
            string full = System.IO.Path.Combine(Path, name);
            File.WriteAllText(full, contents);
            return full;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
