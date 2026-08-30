using System.IO.Compression;

namespace TLOverlay.Core.Setup;

/// <summary>
/// Unpacks a llama.cpp release archive into the runtime directory.
/// </summary>
public static class RuntimeInstaller
{
    public const string ServerFileName = "llama-server.exe";

    /// <summary>
    /// Extracts <paramref name="archivePath"/> and leaves
    /// <see cref="ServerFileName"/> directly in
    /// <paramref name="runtimeDirectory"/>.
    ///
    /// The flattening step is the point: release archives nest the binaries a
    /// directory deep, and the name of that directory changes with every build,
    /// so the app cannot rely on a fixed path unless we normalise it here. The
    /// server's sibling DLLs move with it - it will not load without them.
    /// </summary>
    /// <returns>Full path to the extracted server executable.</returns>
    public static string InstallFromArchive(string archivePath, string runtimeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeDirectory);

        Directory.CreateDirectory(runtimeDirectory);

        try
        {
            ZipFile.ExtractToDirectory(archivePath, runtimeDirectory, overwriteFiles: true);
        }
        catch (InvalidDataException ex)
        {
            throw new ModelDownloadException("ไฟล์ที่โหลดมาไม่ใช่ zip ที่ถูกต้อง", ex);
        }

        string expected = Path.Combine(runtimeDirectory, ServerFileName);
        if (File.Exists(expected))
        {
            return expected;
        }

        string? found = Directory
            .EnumerateFiles(runtimeDirectory, ServerFileName, SearchOption.AllDirectories)
            .FirstOrDefault();

        if (found is null)
        {
            throw new ModelDownloadException($"ไม่พบ {ServerFileName} ในไฟล์ที่แตกออกมา");
        }

        string sourceDirectory = Path.GetDirectoryName(found)!;

        foreach (string file in Directory.EnumerateFiles(sourceDirectory))
        {
            string target = Path.Combine(runtimeDirectory, Path.GetFileName(file));
            if (!string.Equals(file, target, StringComparison.OrdinalIgnoreCase))
            {
                File.Move(file, target, overwrite: true);
            }
        }

        return expected;
    }
}
