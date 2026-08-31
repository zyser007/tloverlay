using System.Text.Json;
using TLOverlay.Core.Update;
using Xunit;

namespace TLOverlay.Core.Tests;

/// <summary>
/// Release payloads, in the shapes GitHub actually produces. The selection rules
/// are the whole of the update check, and they are the part that decides whether
/// a player is offered a build that cannot be installed.
/// </summary>
public class GitHubUpdateSourceTests
{
    private static JsonElement Releases(string json) => JsonDocument.Parse(json).RootElement;

    private static string Release(
        string tag,
        bool prerelease = false,
        bool draft = false,
        bool withExecutable = true,
        bool withChecksums = true,
        long size = 76_000_000)
    {
        var assets = new List<string>();

        if (withExecutable)
        {
            assets.Add($$"""
                {"name":"TLOverlay.exe","size":{{size}},
                 "browser_download_url":"https://github.com/zyser007/tloverlay/releases/download/{{tag}}/TLOverlay.exe"}
                """);
        }

        assets.Add($$"""
            {"name":"TLOverlay-1.0.0-win-x64.zip","size":75000000,
             "browser_download_url":"https://github.com/zyser007/tloverlay/releases/download/{{tag}}/zip"}
            """);

        if (withChecksums)
        {
            assets.Add($$"""
                {"name":"SHA256SUMS.txt","size":200,
                 "browser_download_url":"https://github.com/zyser007/tloverlay/releases/download/{{tag}}/SHA256SUMS.txt"}
                """);
        }

        return $$"""
            {"tag_name":"{{tag}}","draft":{{(draft ? "true" : "false")}},
             "prerelease":{{(prerelease ? "true" : "false")}},
             "html_url":"https://github.com/zyser007/tloverlay/releases/tag/{{tag}}",
             "body":"notes for {{tag}}",
             "assets":[{{string.Join(",", assets)}}]}
            """;
    }

    [Fact]
    public void ANewerReleaseIsOffered()
    {
        JsonElement releases = Releases($"[{Release("v0.2.0")}]");

        UpdateManifest? found = GitHubUpdateSource.SelectFromReleases(
            releases, new Version(0, 1, 0), includePrerelease: false);

        Assert.NotNull(found);
        Assert.Equal(new Version(0, 2, 0), found.Version);
        Assert.Equal("v0.2.0", found.Tag);
        Assert.EndsWith("TLOverlay.exe", found.ExecutableUrl.ToString(), StringComparison.Ordinal);
        Assert.EndsWith("SHA256SUMS.txt", found.ChecksumsUrl.ToString(), StringComparison.Ordinal);
        Assert.Equal("notes for v0.2.0", found.Notes);
    }

    [Fact]
    public void TheSameVersionIsNotAnUpdate()
    {
        JsonElement releases = Releases($"[{Release("v0.1.0")}]");

        Assert.Null(GitHubUpdateSource.SelectFromReleases(
            releases, new Version(0, 1, 0), includePrerelease: false));
    }

    [Fact]
    public void AnOlderReleaseIsNeverOfferedAsAnUpdate()
    {
        JsonElement releases = Releases($"[{Release("v0.0.9")}]");

        // A downgrade would be worse than doing nothing, and this is exactly what
        // a re-published old tag looks like from here.
        Assert.Null(GitHubUpdateSource.SelectFromReleases(
            releases, new Version(0, 1, 0), includePrerelease: false));
    }

    [Fact]
    public void TheNewestReleaseWinsWhateverOrderTheyArrivedIn()
    {
        JsonElement releases = Releases(
            $"[{Release("v0.2.0")},{Release("v0.4.0")},{Release("v0.3.0")}]");

        UpdateManifest? found = GitHubUpdateSource.SelectFromReleases(
            releases, new Version(0, 1, 0), includePrerelease: false);

        Assert.Equal(new Version(0, 4, 0), found?.Version);
    }

    [Fact]
    public void PrereleasesAreSkippedUnlessAskedFor()
    {
        JsonElement releases = Releases($"[{Release("v0.9.0", prerelease: true)},{Release("v0.2.0")}]");

        Assert.Equal(
            new Version(0, 2, 0),
            GitHubUpdateSource.SelectFromReleases(releases, new Version(0, 1, 0), includePrerelease: false)?.Version);

        Assert.Equal(
            new Version(0, 9, 0),
            GitHubUpdateSource.SelectFromReleases(releases, new Version(0, 1, 0), includePrerelease: true)?.Version);
    }

    [Fact]
    public void DraftsAreNeverOffered()
    {
        JsonElement releases = Releases($"[{Release("v0.9.0", draft: true)},{Release("v0.2.0")}]");

        Assert.Equal(
            new Version(0, 2, 0),
            GitHubUpdateSource.SelectFromReleases(releases, new Version(0, 1, 0), includePrerelease: false)?.Version);
    }

    [Fact]
    public void AReleaseWithoutTheExecutableIsPassedOver()
    {
        // What a release looks like while its assets are still uploading, and
        // what every release cut before the updater existed looks like.
        JsonElement releases = Releases(
            $"[{Release("v0.9.0", withExecutable: false)},{Release("v0.2.0")}]");

        Assert.Equal(
            new Version(0, 2, 0),
            GitHubUpdateSource.SelectFromReleases(releases, new Version(0, 1, 0), includePrerelease: false)?.Version);
    }

    [Fact]
    public void AReleaseWithoutChecksumsIsPassedOver()
    {
        // Unsigned binaries: without the hashes there is nothing to check the
        // download against, so it is not installable rather than installed
        // unverified.
        JsonElement releases = Releases($"[{Release("v0.9.0", withChecksums: false)}]");

        Assert.Null(GitHubUpdateSource.SelectFromReleases(
            releases, new Version(0, 1, 0), includePrerelease: false));
    }

    [Fact]
    public void AnEmptyOrOddPayloadIsNotACrash()
    {
        Assert.Null(GitHubUpdateSource.SelectFromReleases(
            Releases("[]"), new Version(0, 1, 0), includePrerelease: false));

        Assert.Null(GitHubUpdateSource.SelectFromReleases(
            Releases("{}"), new Version(0, 1, 0), includePrerelease: false));

        Assert.Null(GitHubUpdateSource.SelectFromReleases(
            Releases("""[{"tag_name":"v9.9.9"}]"""), new Version(0, 1, 0), includePrerelease: false));
    }

    [Fact]
    public void TheAssetSizeIsCarriedThroughForTheProgressBar()
    {
        JsonElement releases = Releases($"[{Release("v0.2.0", size: 12345)}]");

        Assert.Equal(
            12345,
            GitHubUpdateSource.SelectFromReleases(releases, new Version(0, 1, 0), includePrerelease: false)?.SizeBytes);
    }
}
