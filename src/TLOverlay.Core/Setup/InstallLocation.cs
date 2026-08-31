namespace TLOverlay.Core.Setup;

/// <summary>
/// Decides where the app may write multi-gigabyte files.
///
/// Writing beside the executable is wrong for this app in three common
/// situations, all of which look fine until they do not: the exe is launched
/// straight out of a zip or rar (archivers extract to a temp folder and run from
/// there, then delete it), the exe lives under Program Files (not writable
/// without elevation), or the exe sits on read-only media. A model downloaded
/// into any of those is either lost or refused.
/// </summary>
public static class InstallLocation
{
    /// <summary>
    /// Whether <paramref name="directory"/> sits inside the system temp folder.
    ///
    /// This is what catches "run directly from the archive": WinRAR extracts to
    /// <c>%Temp%\Rar$EXa…\</c>, 7-Zip to <c>%Temp%\7z…\</c>, and Explorer's own
    /// zip viewer to <c>%Temp%\Temp1_name\</c>. All are under temp, so one check
    /// covers them rather than a list of archiver-specific prefixes.
    /// </summary>
    public static bool IsUnderTemporaryDirectory(string directory, string temporaryDirectory)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(temporaryDirectory))
        {
            return false;
        }

        string candidate = Normalize(directory);
        string temp = Normalize(temporaryDirectory);

        return candidate.Equals(temp, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the app is running from a copy an archiver made, which means
    /// anything written next to it disappears when the archiver cleans up.
    /// </summary>
    public static bool IsRunningFromArchive(string baseDirectory) =>
        IsUnderTemporaryDirectory(baseDirectory, Path.GetTempPath());

    /// <summary>
    /// Whether files can actually be created in a directory. Existence is not
    /// enough - Program Files exists and is not writable - so this tries a real
    /// write rather than inspecting ACLs, which is both simpler and accurate.
    /// </summary>
    public static bool IsWritable(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(directory);

            string probe = Path.Combine(directory, $".tloverlay-write-test-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string Normalize(string path)
    {
        string full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
