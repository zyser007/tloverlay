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

    /// <summary>
    /// Free space on the volume holding <paramref name="directory"/>, or null
    /// when it cannot be determined (a network path, a drive that vanished).
    ///
    /// Worth showing before a download rather than after: the model is gigabytes
    /// and the failure at the end of one is expensive.
    /// </summary>
    public static long? FreeSpaceBytes(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(directory)) ?? string.Empty;

            if (root.Length == 0)
            {
                return null;
            }

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Moves a directory, across volumes if necessary.
    ///
    /// Directory.Move cannot cross a volume, and moving the model to a different
    /// drive is the entire point of letting the player choose where it lives, so
    /// this copies and then deletes. The source is only removed once every file
    /// has arrived, so an interrupted move leaves the original intact rather than
    /// destroying a two-gigabyte download.
    /// </summary>
    public static async Task MoveDirectoryAsync(
        string source,
        string destination,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        if (!Directory.Exists(source))
        {
            return;
        }

        if (Normalize(source).Equals(Normalize(destination), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        long total = files.Sum(static f => new FileInfo(f).Length);
        long copied = 0;

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
            await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            copied += new FileInfo(file).Length;
            progress?.Report(total > 0 ? (double)copied / total : 1);
        }

        // Only now is it safe to let go of the originals.
        Directory.Delete(source, recursive: true);
        progress?.Report(1);
    }

    private static string Normalize(string path)
    {
        string full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
