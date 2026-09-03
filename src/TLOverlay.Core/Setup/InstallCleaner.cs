namespace TLOverlay.Core.Setup;

/// <summary>
/// Removes what Setup downloaded.
///
/// Exists because the model is the largest thing this app puts on a disk by two
/// orders of magnitude, and a player who has moved to a cloud engine - or who
/// simply needs the space back for a game - should not have to go hunting
/// through %LocalAppData% to reclaim it.
/// </summary>
public static class InstallCleaner
{
    /// <summary>The folder Setup extracts the server into, under the install root.</summary>
    public const string RuntimeFolderName = "runtime";

    /// <summary>
    /// Bytes a file or folder occupies, or zero when it is not there. Shown
    /// before deleting, because "free 2.3 GB" and "free 0 bytes" are different
    /// decisions.
    /// </summary>
    public static long SizeOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return 0;
        }

        try
        {
            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }

            if (!Directory.Exists(path))
            {
                return 0;
            }

            long total = 0;

            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                total += new FileInfo(file).Length;
            }

            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>What deleting the model would actually free: the file and its partial.</summary>
    public static long ModelSize(string? modelPath) =>
        string.IsNullOrWhiteSpace(modelPath)
            ? 0
            : SizeOf(modelPath) + SizeOf(ModelDownloader.PartialPathFor(modelPath));

    /// <summary>
    /// What deleting the server would free: the whole runtime folder when that is
    /// what would go, rather than just the executable inside it.
    /// </summary>
    public static long RuntimeSize(string? executablePath, string? installRoot)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return 0;
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(executablePath));

        return directory is not null
            && !string.IsNullOrWhiteSpace(installRoot)
            && IsManagedRuntime(directory, installRoot)
                ? SizeOf(directory)
                : SizeOf(executablePath);
    }

    /// <summary>
    /// Deletes a downloaded model, and any half-finished download beside it.
    ///
    /// The partial matters: it is invisible in the app but can be most of a
    /// gigabyte, and someone clearing space to make room would never think to
    /// look for it.
    /// </summary>
    public static void DeleteModel(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return;
        }

        Delete(modelPath);
        Delete(ModelDownloader.PartialPathFor(modelPath));
    }

    /// <summary>
    /// Deletes the translation server.
    ///
    /// The whole folder when it is the one this app extracted into, because
    /// llama-server.exe does not run without the DLLs beside it and leaving them
    /// would reclaim a fraction of the space while looking like a full cleanup.
    /// A server the player pointed at somewhere else is left alone apart from the
    /// executable itself - that folder is theirs, and may hold anything.
    /// </summary>
    public static void DeleteRuntime(string? executablePath, string? installRoot)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(executablePath));

        if (directory is not null
            && !string.IsNullOrWhiteSpace(installRoot)
            && IsManagedRuntime(directory, installRoot))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Something in there is held open - the server may still be
                // running. Fall through and let the file delete report it.
            }
        }

        Delete(executablePath);
    }

    private static bool IsManagedRuntime(string directory, string installRoot)
    {
        string expected = Path.GetFullPath(Path.Combine(installRoot, RuntimeFolderName));

        return string.Equals(
            directory.TrimEnd(Path.DirectorySeparatorChar),
            expected.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
