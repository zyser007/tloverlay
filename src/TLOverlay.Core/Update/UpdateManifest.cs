namespace TLOverlay.Core.Update;

/// <summary>A release newer than what is running, and how to fetch it.</summary>
public sealed record UpdateManifest(
    Version Version,
    string Tag,
    Uri ExecutableUrl,
    long SizeBytes,
    Uri ChecksumsUrl,
    Uri ReleasePage,
    string Notes,
    bool IsPrerelease)
{
    public double MegabytesApproximately => SizeBytes / 1024d / 1024d;
}

/// <summary>How the app should behave about updates.</summary>
public enum UpdatePolicy
{
    /// <summary>Never contact GitHub.</summary>
    Off,

    /// <summary>Check and say so. Nothing is downloaded until the player asks.</summary>
    Notify,

    /// <summary>Check, download, and offer to restart into it.</summary>
    Automatic,
}

/// <summary>Reads the running application's version.</summary>
public static class AppVersion
{
    /// <summary>
    /// The running version, or 0.0.0 when it cannot be read.
    ///
    /// Parsed from the informational version so a build stamped "0.2.0+abc1234"
    /// still compares as 0.2.0 - the build metadata is not part of the version
    /// and must not make every check look like a downgrade.
    /// </summary>
    public static Version Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new Version(0, 0, 0);
        }

        string text = value.Trim();

        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        int metadata = text.IndexOfAny(['+', '-']);
        if (metadata > 0)
        {
            text = text[..metadata];
        }

        return Version.TryParse(text, out Version? parsed)
            ? Normalize(parsed)
            : new Version(0, 0, 0);
    }

    /// <summary>
    /// Drops the revision and fills in a missing build, so 0.2 and 0.2.0.0 both
    /// compare as 0.2.0. Tags carry three parts; assembly versions carry four.
    /// </summary>
    public static Version Normalize(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }
}
